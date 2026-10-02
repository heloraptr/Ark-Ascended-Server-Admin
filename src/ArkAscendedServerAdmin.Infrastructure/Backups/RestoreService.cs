using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Backups;

/// <summary>
/// World restore from a backup (B2), the sibling of <see cref="BackupService"/>. Validates the archive on the one
/// handle that is later extracted, takes the target's lock (or the cluster reservation plus every member's lock in
/// ascending id order), re-checks that every affected instance is stopped, copies the current files to a safety
/// directory, writes a journal, replaces the complete selected set, and rolls back from the safety copy on any
/// failure. The destructive phase runs on a detached job with no cancellation; an interruption leaves the journal,
/// which every launch, restore, delete, and membership change honors until the owner recovers or discards it.
/// </summary>
public sealed class RestoreService(
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    IInstanceLocks locks,
    IProcessManager processManager,
    DetachedJobs jobs,
    IRestoreJournals journals,
    IConsoleService console,
    TimeProvider timeProvider,
    ILogger<RestoreService> logger)
{
    /// <summary>Restore records kept per instance; older ones are deleted.</summary>
    public const int RecordCap = 50;

    /// <summary>Safety copies kept per instance besides any a journal references.</summary>
    public const int SafetyCopiesKept = 3;

    private const string WorldCopy = "World";

    private const string ClusterCopy = "Cluster";

    private readonly object _sync = new();
    private Task _lastJob = Task.CompletedTask;

    public event Action<RestoreRecord>? Restored;

    /// <summary>The most recently accepted restore or recovery job (a completed task when idle); tests await it.</summary>
    public Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _lastJob;
            }
        }
    }

    public async Task<RestoreInspection> InspectAsync(int instanceId, string fileName, CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(instanceId, cancellationToken);
        if (target is null)
        {
            return new RestoreInspection("The instance no longer exists.", false, "The instance no longer exists.", []);
        }

        var siblings = target.Cluster?.Instances.Where(i => i.Id != instanceId).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => i.Name).ToList() ?? [];
        var archivePath = await ResolveArchiveAsync(target, fileName, cancellationToken);
        if (archivePath.Problem is not null)
        {
            return new RestoreInspection(archivePath.Problem, false, archivePath.Problem, siblings);
        }

        BackupManifest manifest;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath.Path!);
            var read = ReadManifest(archive);
            if (read.Problem is not null)
            {
                return new RestoreInspection(read.Problem, false, read.Problem, siblings);
            }

            manifest = read.Manifest!;
            var check = RestoreArchiveRules.Check(manifest, Entries(archive), target.Slug, MapKeyOf(target));
            if (check.Problem is not null)
            {
                return new RestoreInspection(check.Problem, false, check.Problem, siblings);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new RestoreInspection($"the archive could not be read: {ex.Message}", false, ex.Message, siblings);
        }

        var cluster = ClusterAvailability(target, manifest);
        return new RestoreInspection(null, cluster is null, cluster, siblings);
    }

    public async Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(instanceId, cancellationToken);
        if (target is null)
        {
            return OperationOutcome.Rejected($"Instance {instanceId} does not exist.");
        }

        var mapKey = MapKeyOf(target);
        var archivePath = await ResolveArchiveAsync(target, fileName, cancellationToken);
        if (archivePath.Problem is not null)
        {
            return OperationOutcome.Rejected(Capitalize(archivePath.Problem));
        }

        ZipArchive? archive = null;
        Ownership? ownership = null;
        IDisposable? registration = null;
        try
        {
            archive = ZipFile.OpenRead(archivePath.Path!);
            var read = ReadManifest(archive);
            if (read.Problem is not null)
            {
                return OperationOutcome.Rejected($"The backup cannot be restored: {read.Problem}.");
            }

            var manifest = read.Manifest!;
            var check = RestoreArchiveRules.Check(manifest, Entries(archive), target.Slug, mapKey);
            if (check.Problem is not null)
            {
                return OperationOutcome.Rejected($"The backup cannot be restored: {check.Problem}.");
            }

            if (includeCluster && ClusterAvailability(target, manifest) is { } unavailable)
            {
                return OperationOutcome.Rejected($"Cluster data cannot be included: {unavailable}.");
            }

            // Hashing every entry is long synchronous work; on the thread pool it does not freeze the calling circuit.
            var hashedArchive = archive;
            if (await Task.Run(() => VerifyHashes(hashedArchive, check), cancellationToken) is { } corrupt)
            {
                return OperationOutcome.Rejected($"The backup failed verification: {corrupt}.");
            }

            var cluster = includeCluster ? target.Cluster : null;
            var affected = cluster is null
                ? [(target.Id, target.Name)]
                : cluster.Instances.Select(i => (i.Id, i.Name)).ToList();
            var owned = TryOwn(cluster?.Id, affected, out ownership);
            if (!owned.Succeeded)
            {
                return owned;
            }

            var ready = CheckReady(cluster?.Id, affected);
            if (!ready.Succeeded)
            {
                return ready;
            }

            var worldDirectory = layout.InstanceWorldDirectory(target.Slug, mapKey);
            var clusterDirectory = cluster is null ? null : layout.ClusterDirectory(cluster.Slug);
            if ((FindReparsePoint(worldDirectory) ?? (clusterDirectory is null ? null : FindReparsePoint(clusterDirectory))) is { } reparse)
            {
                return OperationOutcome.Rejected($"{reparse} is a junction or symbolic link; a restore only replaces real directories.");
            }

            registration = jobs.TryBegin($"restore {target.Slug}");
            if (registration is null)
            {
                return OperationOutcome.Rejected("The service is stopping; the restore was not started.");
            }

            var plan = new RestorePlan(target, mapKey, cluster, fileName, affected.Select(a => a.Id).Order().ToList(), worldDirectory, clusterDirectory, archive, check);
            var (heldLocks, heldJob) = (ownership, registration);
            var job = Task.Run(() => RunRestoreAsync(plan, heldLocks, heldJob), CancellationToken.None);
            archive = null;
            ownership = null;
            registration = null;
            lock (_sync)
            {
                _lastJob = job;
            }

            return await job.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "Restore of instance {InstanceId} from {FileName} could not start.", instanceId, fileName);
            return OperationOutcome.Rejected($"The backup could not be read: {ex.Message}");
        }
        finally
        {
            registration?.Dispose();
            ownership?.Dispose();
            archive?.Dispose();
        }
    }

    public async Task<OperationOutcome> RecoverAsync(string operationId, CancellationToken cancellationToken)
    {
        var journal = journals.Find(operationId);
        if (journal is null)
        {
            return OperationOutcome.Rejected($"There is no restore journal named '{operationId}'.");
        }

        var affected = await NamesAsync(journal.AffectedInstanceIds, cancellationToken);
        var owned = TryOwn(journal.ClusterId, affected, out var ownership);
        if (!owned.Succeeded)
        {
            return owned;
        }

        IDisposable? registration = null;
        try
        {
            var ready = CheckReady(journal.ClusterId, affected, journal.OperationId);
            if (!ready.Succeeded)
            {
                return ready;
            }

            registration = jobs.TryBegin($"recover {journal.InstanceSlug}");
            if (registration is null)
            {
                return OperationOutcome.Rejected("The service is stopping; the recovery was not started.");
            }

            var (heldLocks, heldJob) = (ownership, registration);
            var job = Task.Run(() => RunRecoverAsync(journal, heldLocks, heldJob), CancellationToken.None);
            ownership = null;
            registration = null;
            lock (_sync)
            {
                _lastJob = job;
            }

            return await job.WaitAsync(cancellationToken);
        }
        finally
        {
            registration?.Dispose();
            ownership?.Dispose();
        }
    }

    public async Task<OperationOutcome> DiscardJournalAsync(string operationId, CancellationToken cancellationToken)
    {
        var journal = journals.Find(operationId);
        if (journal is null)
        {
            return OperationOutcome.Rejected($"There is no restore journal named '{operationId}'.");
        }

        var affected = await NamesAsync(journal.AffectedInstanceIds, cancellationToken);
        var owned = TryOwn(journal.ClusterId, affected, out var ownership);
        if (!owned.Succeeded)
        {
            return owned;
        }

        using (ownership)
        {
            journals.Delete(operationId);
        }

        logger.LogWarning("Restore journal {OperationId} discarded by the owner; the safety copy at {SafetyCopy} is kept until pruned.", operationId, journal.SafetyCopyPath);
        Info(journal.InstanceId, $"Restore journal {operationId} discarded; the safety copy at {journal.SafetyCopyPath} stays until it is pruned.", ConsoleLineKind.Warning);
        return OperationOutcome.Success;
    }

    // ---- the jobs --------------------------------------------------------------------------------------

    private async Task<OperationOutcome> RunRestoreAsync(RestorePlan plan, Ownership ownership, IDisposable registration)
    {
        var instance = plan.Target;
        var startedAt = timeProvider.GetUtcNow();
        RestoreJournal? journal = null;
        try
        {
            using var archive = plan.Archive;
            var safety = NewSafetyDirectory(instance.Slug);
            Info(instance.Id, $"Restore: copying the current files to {safety}.");
            CopyDirectory(plan.WorldDirectory, Path.Combine(safety, WorldCopy));
            if (plan.ClusterDirectory is not null)
            {
                CopyDirectory(plan.ClusterDirectory, Path.Combine(safety, ClusterCopy));
            }

            journal = new RestoreJournal(
                $"{instance.Slug}-{Path.GetFileName(safety)}",
                startedAt,
                instance.Id,
                instance.Slug,
                plan.MapKey,
                plan.Cluster?.Id,
                plan.Cluster?.Slug,
                plan.AffectedIds,
                safety,
                plan.FileName,
                RestorePhase.Replacing);
            journals.Write(journal);

            try
            {
                Info(instance.Id, $"Restore: replacing the world files{(plan.ClusterDirectory is null ? string.Empty : " and the cluster directory")} from {plan.FileName}.");
                DeleteSelectedWorldFiles(plan.WorldDirectory, plan.MapKey);
                if (plan.ClusterDirectory is not null)
                {
                    ClearDirectory(plan.ClusterDirectory);
                    foreach (var directory in plan.Check.Directories)
                    {
                        Directory.CreateDirectory(Path.Combine(plan.ClusterDirectory, directory.Replace('/', Path.DirectorySeparatorChar)));
                    }
                }

                Extract(archive, plan.Check, plan.WorldDirectory, plan.ClusterDirectory);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Restore of instance {InstanceId} from {FileName} failed; rolling back from {SafetyCopy}.", instance.Id, plan.FileName, safety);
                Info(instance.Id, $"Restore failed ({ex.Message}); putting the previous files back from {safety}.", ConsoleLineKind.Error);
                journals.Write(journal = journal with { Phase = RestorePhase.RollingBack });
                try
                {
                    RollBack(journal);
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Rollback of restore {OperationId} failed; the journal remains.", journal.OperationId);
                    journals.Write(journal with { Phase = RestorePhase.RollbackFailed });
                    var stuck = $"Restore failed ({ex.Message}) and the rollback failed too ({rollbackEx.Message}); journal {journal.OperationId} remains and the safety copy is at {safety}.";
                    Info(instance.Id, stuck, ConsoleLineKind.Error);
                    await PersistAsync(Record(instance.Id, startedAt, plan.FileName, plan.Cluster is not null, RestoreOutcome.Failed, stuck));
                    return OperationOutcome.Rejected(stuck);
                }

                var leftover = Finish(journal);
                var rolledBack = $"Restore failed and the previous files were put back: {ex.Message}{leftover}";
                Info(instance.Id, rolledBack, ConsoleLineKind.Warning);
                await PersistAsync(Record(instance.Id, startedAt, plan.FileName, plan.Cluster is not null, RestoreOutcome.RolledBack, ex.Message + leftover));
                return OperationOutcome.Rejected(rolledBack);
            }

            var note = Finish(journal);
            Info(instance.Id, $"Restored {plan.FileName}{(plan.ClusterDirectory is null ? string.Empty : " including the cluster directory")}; the previous files are at {safety}.{note}", note is null ? ConsoleLineKind.Info : ConsoleLineKind.Warning);
            await PersistAsync(Record(instance.Id, startedAt, plan.FileName, plan.Cluster is not null, RestoreOutcome.Success, note?.Trim()));
            return OperationOutcome.Success;
        }
        catch (Exception ex)
        {
            // Before the journal nothing was deleted and a safety copy is harmless; after the replacement the inner handlers ran,
            // so this is a failure in the bookkeeping and the journal (if still present) keeps the launch interlock.
            var where = journal is null ? "before any file was replaced" : $"after the files were replaced; journal {journal.OperationId} may remain";
            logger.LogError(ex, "Restore of instance {InstanceId} from {FileName} failed {Where}.", instance.Id, plan.FileName, where);
            Info(instance.Id, $"Restore failed {where}: {ex.Message}", ConsoleLineKind.Error);
            await PersistAsync(Record(instance.Id, startedAt, plan.FileName, plan.Cluster is not null, RestoreOutcome.Failed, ex.Message));
            return OperationOutcome.Rejected($"Restore failed {where}: {ex.Message}");
        }
        finally
        {
            ownership.Dispose();
            registration.Dispose();
        }
    }

    private async Task<OperationOutcome> RunRecoverAsync(RestoreJournal journal, Ownership ownership, IDisposable registration)
    {
        var startedAt = timeProvider.GetUtcNow();
        try
        {
            Info(journal.InstanceId, $"Recovering from the safety copy at {journal.SafetyCopyPath} (journal {journal.OperationId}).");
            journals.Write(journal with { Phase = RestorePhase.RollingBack });
            RollBack(journal);
            var note = Finish(journal);
            Info(journal.InstanceId, $"Recovered: the files from before restore {journal.OperationId} are back in place.{note}", note is null ? ConsoleLineKind.Info : ConsoleLineKind.Warning);
            await PersistAsync(Record(journal.InstanceId, startedAt, journal.SourceFileName, journal.IncludesCluster, RestoreOutcome.RolledBack, $"recovered from {journal.SafetyCopyPath}{note}"));
            return OperationOutcome.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recovery of restore {OperationId} failed; the journal remains.", journal.OperationId);
            journals.Write(journal with { Phase = RestorePhase.RollbackFailed });
            var reason = $"Recovery failed ({ex.Message}); journal {journal.OperationId} remains and the safety copy is at {journal.SafetyCopyPath}.";
            Info(journal.InstanceId, reason, ConsoleLineKind.Error);
            await PersistAsync(Record(journal.InstanceId, startedAt, journal.SourceFileName, journal.IncludesCluster, RestoreOutcome.Failed, reason));
            return OperationOutcome.Rejected(reason);
        }
        finally
        {
            ownership.Dispose();
            registration.Dispose();
        }
    }

    // ---- loading and validation ------------------------------------------------------------------------

    private async Task<Instance?> LoadTargetAsync(int instanceId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Instances.AsNoTrackingWithIdentityResolution()
            .Include(i => i.Map)
            .Include(i => i.Cluster).ThenInclude(c => c!.Instances)
            .SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
    }

    private static string MapKeyOf(Instance instance) => instance.Map?.Key ?? throw new InvalidOperationException($"Instance {instance.Id} has no map.");

    /// <summary>The archive is resolved through the instance's own successful record, never a caller-supplied path.</summary>
    private async Task<(string? Path, string? Problem)> ResolveArchiveAsync(Instance instance, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return (null, "the backup name is not a plain file name");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = await db.BackupRecords.AsNoTracking()
            .AnyAsync(r => r.InstanceId == instance.Id && r.Outcome == BackupOutcome.Success && r.FileName == fileName, cancellationToken);
        if (!known)
        {
            return (null, $"the instance has no successful backup named {fileName}");
        }

        var path = Path.Combine(layout.InstanceBackupDirectory(instance.Slug), fileName);
        return File.Exists(path) ? (path, null) : (null, $"the archive {path} is missing");
    }

    private static (BackupManifest? Manifest, string? Problem) ReadManifest(ZipArchive archive)
    {
        var entry = archive.Entries.SingleOrDefault(e => string.Equals(Normalize(e.FullName), BackupManifest.FileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return (null, "the archive has no manifest.json");
        }

        using var reader = new StreamReader(entry.Open());
        var manifest = BackupManifest.FromJson(reader.ReadToEnd());
        return manifest is null ? (null, "the archived manifest.json is not readable") : (manifest, null);
    }

    private static List<RestoreArchiveRules.ArchiveEntry> Entries(ZipArchive archive) =>
        archive.Entries.Select(e => new RestoreArchiveRules.ArchiveEntry(Normalize(e.FullName), e.Length)).ToList();

    private static string Normalize(string entryName) => entryName.Replace('\\', '/');

    /// <summary>Null when cluster data may be included, otherwise the reason the checkbox is disabled.</summary>
    private static string? ClusterAvailability(Instance instance, BackupManifest manifest)
    {
        if (instance.Cluster is not { } cluster)
        {
            return "the instance is not in a cluster";
        }

        if (manifest.ClusterSlug is null)
        {
            return manifest.ClusterCaptured
                ? "the backup does not say which cluster it belongs to"
                : "the backup holds no cluster data (taken before cluster data was recorded, or while the instance was standalone)";
        }

        if (!string.Equals(manifest.ClusterSlug, cluster.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return $"the backup was taken in cluster '{manifest.ClusterSlug}', not '{cluster.Slug}'";
        }

        return manifest.ClusterCaptured ? null : "the cluster directory did not exist when the backup was taken";
    }

    /// <summary>Hashes every payload entry on the opened archive; the first mismatch, or null.</summary>
    private static string? VerifyHashes(ZipArchive archive, RestoreArchiveRules.CheckResult check)
    {
        var byName = archive.Entries.ToDictionary(e => Normalize(e.FullName), StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        foreach (var (entry, manifest) in check.Payload)
        {
            using var hash = SHA256.Create();
            using var stream = byName[entry.Name].Open();
            long length = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.TransformBlock(buffer, 0, read, null, 0);
                length += read;
            }

            hash.TransformFinalBlock([], 0, 0);
            if (length != manifest.Length)
            {
                return $"{entry.Name} extracted to {length} bytes, expected {manifest.Length}";
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.Hash!), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"{entry.Name} SHA-256 does not match the manifest";
            }
        }

        return null;
    }

    // ---- ownership -------------------------------------------------------------------------------------

    /// <summary>
    /// Reserves the cluster first (when cluster data is included), then takes every affected instance's lock once in
    /// ascending id order; all or nothing, naming the busy one. The result owns whatever was taken.
    /// </summary>
    private OperationOutcome TryOwn(int? clusterId, IReadOnlyList<(int Id, string Name)> affected, out Ownership ownership)
    {
        ownership = new Ownership(processManager);
        if (clusterId is { } id)
        {
            var reservation = locks.TryReserveCluster(id);
            if (reservation is null)
            {
                return OperationOutcome.Rejected("The cluster is reserved by another operation; try again when it finishes.");
            }

            ownership.Reservation = reservation;
        }

        foreach (var (instanceId, name) in affected.DistinctBy(a => a.Id).OrderBy(a => a.Id))
        {
            var lease = locks.TryAcquire(instanceId);
            if (lease is null)
            {
                ownership.Dispose();
                return OperationOutcome.Rejected($"An operation is in progress for {name}; try again when it finishes.");
            }

            ownership.Add(lease);
        }

        return OperationOutcome.Success;
    }

    /// <summary>Under the locks: no journal other than the one being recovered may reference an affected instance or the cluster, and every affected instance must be stopped (Crashed counts as stopped: it has no process, B4).</summary>
    private OperationOutcome CheckReady(int? clusterId, IReadOnlyList<(int Id, string Name)> affected, string? ownJournal = null)
    {
        foreach (var (id, name) in affected)
        {
            if (journals.FindForInstance(id) is { } journal && journal.OperationId != ownJournal)
            {
                return OperationOutcome.Rejected(journal.RefusalReason(name));
            }
        }

        if (clusterId is { } cluster && journals.FindForCluster(cluster) is { } clusterJournal && clusterJournal.OperationId != ownJournal)
        {
            return OperationOutcome.Rejected(clusterJournal.RefusalReason("the cluster"));
        }

        var running = affected
            .Where(a => processManager.GetRuntime(a.Id).State is not (InstanceState.Stopped or InstanceState.Crashed))
            .Select(a => $"{a.Name} ({processManager.GetRuntime(a.Id).State})")
            .ToList();
        return running.Count == 0
            ? OperationOutcome.Success
            : OperationOutcome.Rejected($"Stop {string.Join(", ", running)} first; a restore needs every affected instance stopped.");
    }

    private async Task<List<(int Id, string Name)>> NamesAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var names = await db.Instances.AsNoTracking()
            .Where(i => instanceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Name })
            .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);
        return instanceIds.Distinct().Order().Select(id => (id, names.TryGetValue(id, out var name) ? name : $"instance {id}")).ToList();
    }

    // ---- files -----------------------------------------------------------------------------------------

    private string NewSafetyDirectory(string slug)
    {
        var root = layout.InstanceRestoreSafetyDirectory(slug);
        Directory.CreateDirectory(root);
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var seq = 1; ; seq++)
        {
            var candidate = Path.Combine(root, $"{stamp}-{seq}");
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    /// <summary>The directory, or any directory below it, that is a junction or symbolic link; null when the tree is plain.</summary>
    private static string? FindReparsePoint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var info = new DirectoryInfo(directory);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return info.FullName;
        }

        foreach (var child in info.EnumerateDirectories())
        {
            if (FindReparsePoint(child.FullName) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Copies a directory tree; a missing source copies nothing (the destination is not created either).</summary>
    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>Deletes the files a backup selects plus the game's own rollback copies; other files stay. Creates the directory when missing.</summary>
    private static void DeleteSelectedWorldFiles(string worldDirectory, string mapKey)
    {
        Directory.CreateDirectory(worldDirectory);
        foreach (var file in Directory.EnumerateFiles(worldDirectory))
        {
            var name = Path.GetFileName(file);
            if (BackupInventory.IsWorldFileSelected(name, mapKey) || BackupInventory.IsExcluded(name))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Removes everything inside the directory, leaving it in place; creates it when missing.</summary>
    private static void ClearDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }

        foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
        {
            // A reparse point is deleted as a link, never followed; the pre-checks refuse them anyway.
            Directory.Delete(child.FullName, recursive: !child.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }

    private static void Extract(ZipArchive archive, RestoreArchiveRules.CheckResult check, string worldDirectory, string? clusterDirectory)
    {
        var byName = archive.Entries.ToDictionary(e => Normalize(e.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, manifest) in check.Payload)
        {
            var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
            var folder = entry.Name[..slash];
            var rest = entry.Name[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
            string destination;
            if (string.Equals(folder, BackupInventory.WorldFolder, StringComparison.Ordinal))
            {
                destination = Path.Combine(worldDirectory, rest);
            }
            else if (clusterDirectory is not null)
            {
                destination = Path.Combine(clusterDirectory, rest);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            else
            {
                continue;
            }

            using var input = byName[entry.Name].Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
            input.CopyTo(output);
            if (output.Length != manifest.Length)
            {
                throw new InvalidDataException($"{entry.Name} extracted to {output.Length} bytes, expected {manifest.Length}.");
            }
        }
    }

    /// <summary>Clears each replaced directory and copies the safety copy back, so the exact previous file set returns.</summary>
    private void RollBack(RestoreJournal journal)
    {
        var worldDirectory = layout.InstanceWorldDirectory(journal.InstanceSlug, journal.MapKey);
        ClearDirectory(worldDirectory);
        CopyDirectory(Path.Combine(journal.SafetyCopyPath, WorldCopy), worldDirectory);
        if (journal.ClusterSlug is { } clusterSlug)
        {
            var clusterDirectory = layout.ClusterDirectory(clusterSlug);
            ClearDirectory(clusterDirectory);
            CopyDirectory(Path.Combine(journal.SafetyCopyPath, ClusterCopy), clusterDirectory);
        }
    }

    /// <summary>
    /// The bookkeeping after the files are known good: remove the journal, then prune. A failure here is never a reason
    /// to change the outcome or roll back; it is logged and returned as a note for the record and the console. A journal
    /// that could not be removed keeps its interlock, and the note says to discard it rather than recover from it.
    /// </summary>
    private string? Finish(RestoreJournal journal)
    {
        string? note = null;
        try
        {
            journals.Delete(journal.OperationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Restore journal {OperationId} could not be removed after the files were put in place.", journal.OperationId);
            note = $" The files are in place, but journal {journal.OperationId} could not be removed ({ex.Message}); discard it, do not recover from it.";
        }

        try
        {
            PruneSafetyCopies(journal.InstanceSlug);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Safety copies of instance {Slug} could not be pruned.", journal.InstanceSlug);
        }

        return note;
    }

    /// <summary>Keeps the newest <see cref="SafetyCopiesKept"/> copies per instance plus any a journal references.</summary>
    private void PruneSafetyCopies(string slug)
    {
        var root = layout.InstanceRestoreSafetyDirectory(slug);
        if (!Directory.Exists(root))
        {
            return;
        }

        var referenced = journals.List().Select(j => Path.GetFullPath(j.SafetyCopyPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = new DirectoryInfo(root).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc)
            .ThenByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => d.FullName)
            .Skip(SafetyCopiesKept)
            .Where(d => !referenced.Contains(Path.GetFullPath(d)));
        foreach (var directory in stale)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Safety copy {Directory} could not be pruned.", directory);
            }
        }
    }

    // ---- records ---------------------------------------------------------------------------------------

    private static RestoreRecord Record(int instanceId, DateTimeOffset createdAt, string fileName, bool includedCluster, RestoreOutcome outcome, string? reason) =>
        new()
        {
            InstanceId = instanceId,
            CreatedAt = createdAt,
            SourceFileName = fileName,
            IncludedCluster = includedCluster,
            Outcome = outcome,
            Reason = reason is { Length: > 1000 } ? reason[..1000] : reason,
        };

    private async Task PersistAsync(RestoreRecord record)
    {
        try
        {
            await using (var db = await contextFactory.CreateDbContextAsync(CancellationToken.None))
            {
                db.RestoreRecords.Add(record);
                await db.SaveChangesAsync(CancellationToken.None);

                var excess = await db.RestoreRecords
                    .Where(r => r.InstanceId == record.InstanceId)
                    .OrderByDescending(r => r.Id)
                    .Skip(RecordCap)
                    .ToListAsync(CancellationToken.None);
                if (excess.Count > 0)
                {
                    db.RestoreRecords.RemoveRange(excess);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "The restore record for instance {InstanceId} could not be saved.", record.InstanceId);
            return;
        }

        try
        {
            Restored?.Invoke(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A restore subscriber threw for instance {InstanceId}.", record.InstanceId);
        }
    }

    private void Info(int instanceId, string text, ConsoleLineKind kind = ConsoleLineKind.Info) =>
        console.Append(ConsoleChannels.Instance(instanceId), new ConsoleLine(timeProvider.GetUtcNow(), text, kind));

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private sealed record RestorePlan(
        Instance Target,
        string MapKey,
        Cluster? Cluster,
        string FileName,
        IReadOnlyList<int> AffectedIds,
        string WorldDirectory,
        string? ClusterDirectory,
        ZipArchive Archive,
        RestoreArchiveRules.CheckResult Check);

    /// <summary>
    /// The cluster reservation and instance leases one operation holds; disposal releases the leases first. A pending
    /// crash of each owned instance is dismissed when its lease is taken and again just before it is released (B4), so a
    /// restore always supersedes an automatic restart, even one whose exit was still in cleanup.
    /// </summary>
    private sealed class Ownership(IProcessManager processManager) : IDisposable
    {
        private readonly List<IInstanceLease> _leases = [];

        public IDisposable? Reservation { get; set; }

        public void Add(IInstanceLease lease)
        {
            processManager.DismissCrash(lease.InstanceId);
            _leases.Add(lease);
        }

        public void Dispose()
        {
            for (var i = _leases.Count - 1; i >= 0; i--)
            {
                processManager.DismissCrash(_leases[i].InstanceId);
                _leases[i].Dispose();
            }

            _leases.Clear();
            Reservation?.Dispose();
            Reservation = null;
        }
    }
}
