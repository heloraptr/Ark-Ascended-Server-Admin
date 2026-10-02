using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Backups;

/// <summary>
/// The best-effort live backup job (plan step 28): <c>saveworld</c> → quiescence wait → inventory → snapshot
/// by copy (SHA-256, <c>FileShare.Read</c>) → re-inventory and compare → manifest → zip → full verification →
/// atomic rename → record → prune. Runs under the instance lock (a scheduled backup waits behind a running
/// operation). Every outcome is persisted as a <see cref="BackupRecord"/>. The two <c>protected virtual</c>
/// hooks exist so tests can tamper between steps; they are no-ops in production.
/// </summary>
public class BackupService(
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    IAppSettingsStore settingsStore,
    IInstanceLocks locks,
    IProcessManager processManager,
    IRconClient rconClient,
    IGeneratedConfigWriter configWriter,
    IConsoleService console,
    RestoreService restore,
    TimeProvider timeProvider,
    ILogger<BackupService> logger) : IBackupService
{
    /// <summary>Reason recorded when the instance has a process but RCON cannot be trusted (plan step 22).</summary>
    public const string RconUnreachableReason = "RCON unreachable";

    public const string NotRunningReason = "instance not running";

    public const string WorldFileInUseReason = "world file in use";

    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const int LockViolationHResult = unchecked((int)0x80070021);

    private const int SnapshotAttempts = 3;

    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _stableFor = TimeSpan.FromSeconds(1);

    public async Task<BackupRecord> BackupNowAsync(int instanceId, bool isManual, CancellationToken cancellationToken)
    {
        using var lease = await locks.AcquireAsync(instanceId, cancellationToken);

        Instance instance;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            instance = await db.Instances.AsNoTracking()
                .Include(i => i.Map)
                .Include(i => i.Cluster)
                .SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken)
                ?? throw new ArgumentException($"Instance {instanceId} does not exist.", nameof(instanceId));
        }

        var settings = await settingsStore.GetAsync(cancellationToken);
        var startedAt = timeProvider.GetUtcNow();
        BackupRecord record;
        try
        {
            record = await RunAsync(instance, settings, isManual, startedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "Backup of instance {InstanceId} failed.", instanceId);
            record = Outcome(instance, isManual, startedAt, BackupOutcome.Failed, ex.Message);
        }

        await PersistAsync(record, cancellationToken);
        Announce(instance, record);

        if (record.Outcome == BackupOutcome.Success)
        {
            await PruneAsync(instance, settings, cancellationToken);
        }

        await PruneUnsuccessfulAsync(instance, cancellationToken);
        return record;
    }

    /// <summary>Test seam: runs after the snapshot copy and before the re-inventory.</summary>
    protected virtual Task OnSnapshotCopiedAsync(string snapshotDirectory, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Test seam: runs after the temp zip is written and before it is verified.</summary>
    protected virtual Task OnArchiveWrittenAsync(string tempArchivePath, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<BackupRecord> RunAsync(Instance instance, AppSettings settings, bool isManual, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var mapKey = instance.Map?.Key ?? throw new InvalidOperationException($"Instance {instance.Id} has no map.");
        var runtime = processManager.GetRuntime(instance.Id);
        if (runtime.State != InstanceState.Running)
        {
            var reason = runtime.State is InstanceState.Unreachable or InstanceState.StartingUnconfirmed ? RconUnreachableReason : NotRunningReason;
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, $"{reason} ({runtime.State})");
        }

        var gameUserSettings = await configWriter.ReadGeneratedGameUserSettingsAsync(instance.Slug, cancellationToken);
        if (gameUserSettings is null)
        {
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, $"{RconUnreachableReason}: the generated GameUserSettings.ini is missing");
        }

        var endpoint = RconCredentials.TryRead(gameUserSettings, out var problem);
        if (endpoint is null)
        {
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, $"{RconUnreachableReason}: {problem}");
        }

        try
        {
            var reply = await rconClient.ExecuteAsync(endpoint, RconCommands.SaveWorld, TimeSpan.FromSeconds(settings.RconCommandTimeoutSeconds), cancellationToken);
            if (!reply.Contains(RconCommands.SaveWorldReply, StringComparison.OrdinalIgnoreCase))
            {
                Info(instance, $"saveworld replied '{reply.Trim()}' instead of '{RconCommands.SaveWorldReply}'; continuing with the world file as it is.", ConsoleLineKind.Warning);
            }
        }
        catch (RconException ex)
        {
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, $"saveworld failed ({ex.Failure}): {ex.Message}");
        }

        var worldDirectory = layout.InstanceWorldDirectory(instance.Slug, mapKey);
        var worldFile = Path.Combine(worldDirectory, $"{mapKey}.ark");
        if (!File.Exists(worldFile))
        {
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, $"world file missing: {worldFile}");
        }

        var quiescence = TimeSpan.FromSeconds(settings.BackupQuiescenceSeconds);
        await WaitForQuiescenceAsync(worldFile, quiescence, cancellationToken);

        var clusterDirectory = instance.Cluster is { } cluster ? layout.ClusterDirectory(cluster.Slug) : null;
        var backupDirectory = layout.InstanceBackupDirectory(instance.Slug);
        Directory.CreateDirectory(backupDirectory);

        var snapshot = await SnapshotAsync(instance, mapKey, worldDirectory, clusterDirectory, backupDirectory, quiescence, cancellationToken);
        if (snapshot.Skipped is { } skipped)
        {
            return Outcome(instance, isManual, startedAt, BackupOutcome.Skipped, skipped);
        }

        var manifest = new BackupManifest(instance.Slug, mapKey, timeProvider.GetUtcNow(), snapshot.Entries, instance.Cluster?.Slug, clusterDirectory is not null);
        await File.WriteAllTextAsync(Path.Combine(snapshot.Directory, BackupManifest.FileName), manifest.ToJson(), cancellationToken);

        var tempArchive = Path.Combine(backupDirectory, $".tmp-{Guid.NewGuid():N}.zip");
        try
        {
            // Compressing and re-hashing a world file is long synchronous work. On the thread pool it leaves the
            // caller's context free: a manual backup starts on a Blazor circuit, which would otherwise freeze.
            await Task.Run(() => ZipFile.CreateFromDirectory(snapshot.Directory, tempArchive, CompressionLevel.Optimal, includeBaseDirectory: false), cancellationToken);
            DeleteDirectory(snapshot.Directory);

            await OnArchiveWrittenAsync(tempArchive, cancellationToken);

            var verification = await Task.Run(() => Verify(tempArchive, manifest), cancellationToken);
            if (verification is not null)
            {
                File.Delete(tempArchive);
                return Outcome(instance, isManual, startedAt, BackupOutcome.Failed, $"verification: {verification}");
            }

            var fileName = NextArchiveName(backupDirectory);
            File.Move(tempArchive, Path.Combine(backupDirectory, fileName));
            var size = new FileInfo(Path.Combine(backupDirectory, fileName)).Length;
            var record = Outcome(instance, isManual, startedAt, BackupOutcome.Success, null);
            record.FileName = fileName;
            record.SizeBytes = size;
            return record;
        }
        catch
        {
            TryDelete(tempArchive);
            DeleteDirectory(snapshot.Directory);
            throw;
        }
    }

    private async Task<SnapshotResult> SnapshotAsync(
        Instance instance,
        string mapKey,
        string worldDirectory,
        string? clusterDirectory,
        string backupDirectory,
        TimeSpan quiescence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SnapshotAttempts; attempt++)
        {
            var inventoryAttempts = 0;
            while (true)
            {
                inventoryAttempts++;
                var before = Inventory(mapKey, worldDirectory, clusterDirectory);
                var snapshotDirectory = Path.Combine(backupDirectory, $".snap-{Guid.NewGuid():N}");
                Directory.CreateDirectory(snapshotDirectory);

                List<BackupManifestEntry> copied;
                try
                {
                    copied = await CopyAllAsync(before, worldDirectory, clusterDirectory, snapshotDirectory, cancellationToken);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    DeleteDirectory(snapshotDirectory);
                    Info(instance, $"A world file is held by a writer ({ex.Message}); waiting {quiescence.TotalSeconds:0} s before attempt {attempt + 1} of {SnapshotAttempts}.", ConsoleLineKind.Warning);
                    if (attempt == SnapshotAttempts)
                    {
                        return SnapshotResult.Skip($"{WorldFileInUseReason} after {SnapshotAttempts} attempts: {ex.Message}");
                    }

                    await Task.Delay(quiescence, timeProvider, cancellationToken);
                    break;
                }
                catch
                {
                    DeleteDirectory(snapshotDirectory);
                    throw;
                }

                await OnSnapshotCopiedAsync(snapshotDirectory, cancellationToken);

                var after = Inventory(mapKey, worldDirectory, clusterDirectory);
                var difference = BackupInventory.Compare(before, after);
                if (difference.IsEmpty)
                {
                    return SnapshotResult.Ok(snapshotDirectory, copied);
                }

                DeleteDirectory(snapshotDirectory);
                if (inventoryAttempts >= 2)
                {
                    return SnapshotResult.Skip($"files changed during the snapshot twice ({difference})");
                }

                Info(instance, $"Files changed during the snapshot ({difference}); retrying the inventory once.", ConsoleLineKind.Warning);
            }
        }

        return SnapshotResult.Skip(WorldFileInUseReason);
    }

    private static IReadOnlyList<BackupFileEntry> Inventory(string mapKey, string worldDirectory, string? clusterDirectory)
    {
        var world = Directory.Exists(worldDirectory)
            ? new DirectoryInfo(worldDirectory).EnumerateFiles().Select(f => new BackupFileEntry(f.Name, f.Length, f.LastWriteTimeUtc))
            : [];
        var cluster = clusterDirectory is not null && Directory.Exists(clusterDirectory)
            ? new DirectoryInfo(clusterDirectory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => new BackupFileEntry(Path.GetRelativePath(clusterDirectory, f.FullName), f.Length, f.LastWriteTimeUtc))
            : [];
        return BackupInventory.Select(mapKey, world, cluster);
    }

    private static async Task<List<BackupManifestEntry>> CopyAllAsync(
        IReadOnlyList<BackupFileEntry> entries,
        string worldDirectory,
        string? clusterDirectory,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        var copied = new List<BackupManifestEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var source = ResolveSource(entry.RelativePath, worldDirectory, clusterDirectory);
            var destination = Path.Combine(snapshotDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var hash = SHA256.Create();
            long length;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                length = 0;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    length += read;
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            copied.Add(new BackupManifestEntry(entry.RelativePath, length, Convert.ToHexStringLower(hash.Hash!)));
        }

        return copied;
    }

    private static string ResolveSource(string relativePath, string worldDirectory, string? clusterDirectory)
    {
        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        var folder = relativePath[..slash];
        var rest = relativePath[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
        return folder switch
        {
            BackupInventory.WorldFolder => Path.Combine(worldDirectory, rest),
            BackupInventory.ClusterFolder when clusterDirectory is not null => Path.Combine(clusterDirectory, rest),
            _ => throw new InvalidOperationException($"Unexpected archive path '{relativePath}'."),
        };
    }

    /// <summary>
    /// Verifies that every manifest entry is present in the archive with the recorded length and hash and
    /// that the world file is among them (<see cref="ZipArchive"/> does not validate CRCs on read). Returns
    /// the first problem, or null when the archive is recoverable.
    /// </summary>
    private static string? Verify(string archivePath, BackupManifest manifest)
    {
        if (!manifest.ContainsWorldFile)
        {
            return $"the manifest does not list {BackupInventory.WorldFilePath(manifest.MapKey)}";
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

        if (!entries.TryGetValue(BackupManifest.FileName, out var manifestEntry))
        {
            return "the archive has no manifest.json";
        }

        using (var manifestStream = new StreamReader(manifestEntry.Open()))
        {
            var stored = BackupManifest.FromJson(manifestStream.ReadToEnd());
            if (stored is null || stored.Files.Count != manifest.Files.Count)
            {
                return "the archived manifest.json does not match the snapshot manifest";
            }
        }

        var buffer = new byte[81920];
        foreach (var file in manifest.Files)
        {
            if (!entries.TryGetValue(file.Path, out var zipEntry))
            {
                return $"{file.Path} is missing from the archive";
            }

            using var hash = SHA256.Create();
            using var stream = zipEntry.Open();
            long length = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.TransformBlock(buffer, 0, read, null, 0);
                length += read;
            }

            hash.TransformFinalBlock([], 0, 0);
            if (length != file.Length)
            {
                return $"{file.Path} extracted to {length} bytes, expected {file.Length}";
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.Hash!), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"{file.Path} SHA-256 does not match the manifest";
            }
        }

        return null;
    }

    /// <summary>
    /// Polls the world file until it opens with <c>FileShare.Read</c> and its length and last-write time have
    /// been stable for a second, or the quiescence window elapses (the copy step's sharing-violation retries
    /// then decide). The reply to <c>saveworld</c> precedes the write, so the wait starts unsettled.
    /// </summary>
    private async Task WaitForQuiescenceAsync(string worldFile, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + window;
        var last = Probe(worldFile);
        var stableSince = timeProvider.GetUtcNow();

        while (true)
        {
            await Task.Delay(_pollInterval, timeProvider, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var current = Probe(worldFile);
            if (current.Length != last.Length || current.LastWriteUtc != last.LastWriteUtc)
            {
                last = current;
                stableSince = now;
            }

            if (current.Openable && now - stableSince >= _stableFor)
            {
                return;
            }

            if (now >= deadline)
            {
                logger.LogWarning("World file {Path} did not settle within {Window}; continuing to the snapshot.", worldFile, window);
                return;
            }
        }
    }

    private static FileProbe Probe(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new FileProbe(false, -1, DateTime.MinValue);
        }

        bool openable;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            openable = true;
        }
        catch (IOException)
        {
            openable = false;
        }

        return new FileProbe(openable, info.Length, info.LastWriteTimeUtc);
    }

    private string NextArchiveName(string backupDirectory)
    {
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var seq = 1; ; seq++)
        {
            var candidate = $"{stamp}-{seq}.zip";
            if (!File.Exists(Path.Combine(backupDirectory, candidate)))
            {
                return candidate;
            }
        }
    }

    public event Action<BackupRecord>? Recorded;

    // ---- restore (B2) is the sibling service; these forward so the module has one contract ----------

    public Task<RestoreInspection> InspectRestoreAsync(int instanceId, string fileName, CancellationToken cancellationToken) =>
        restore.InspectAsync(instanceId, fileName, cancellationToken);

    public Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken) =>
        restore.RestoreAsync(instanceId, fileName, includeCluster, cancellationToken);

    public Task<OperationOutcome> RecoverAsync(string operationId, CancellationToken cancellationToken) =>
        restore.RecoverAsync(operationId, cancellationToken);

    public Task<OperationOutcome> DiscardJournalAsync(string operationId, CancellationToken cancellationToken) =>
        restore.DiscardJournalAsync(operationId, cancellationToken);

    public event Action<RestoreRecord>? Restored
    {
        add => restore.Restored += value;
        remove => restore.Restored -= value;
    }

    private async Task PersistAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.BackupRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            Recorded?.Invoke(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A backup subscriber threw for instance {InstanceId}.", record.InstanceId);
        }
    }

    private async Task PruneAsync(Instance instance, AppSettings settings, CancellationToken cancellationToken)
    {
        var retention = instance.BackupRetention ?? settings.DefaultBackupRetention;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.BackupRecords.Where(r => r.InstanceId == instance.Id).ToListAsync(cancellationToken);
        var prune = BackupRetention.SelectForPruning(records, retention);
        if (prune.Count == 0)
        {
            return;
        }

        var directory = layout.InstanceBackupDirectory(instance.Slug);
        var pruned = 0;
        foreach (var record in prune)
        {
            if (record.FileName is not null && !TryDelete(Path.Combine(directory, record.FileName)))
            {
                // The archive is still on disk (held open by a scan, a preview or a restore). The row stays
                // with it, so the archive remains visible and counted, and the next prune tries again.
                logger.LogWarning("Could not delete backup {FileName} of instance {InstanceId}; its record is kept and the next prune will try again.", record.FileName, instance.Id);
                continue;
            }

            db.BackupRecords.Remove(record);
            pruned++;
        }

        if (pruned == 0)
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
        Info(instance, $"Pruned {pruned} backup(s) beyond the retention of {retention}.");
    }

    /// <summary>
    /// Caps the instance's skipped and failed rows at <see cref="BackupRetention.UnsuccessfulRecordsKept"/> after every
    /// backup, so an instance that keeps failing cannot fill the table. Those rows hold no archive, so nothing is
    /// deleted from disk and the console is left alone.
    /// </summary>
    private async Task PruneUnsuccessfulAsync(Instance instance, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.BackupRecords
            .Where(r => r.InstanceId == instance.Id && r.Outcome != BackupOutcome.Success)
            .ToListAsync(cancellationToken);
        var prune = BackupRetention.SelectUnsuccessfulForPruning(records);
        if (prune.Count == 0)
        {
            return;
        }

        db.BackupRecords.RemoveRange(prune);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogDebug(
            "Pruned {Count} skipped or failed backup record(s) of instance {InstanceId}.",
            prune.Count,
            instance.Id);
    }

    private BackupRecord Outcome(Instance instance, bool isManual, DateTimeOffset createdAt, BackupOutcome outcome, string? reason) =>
        new()
        {
            InstanceId = instance.Id,
            CreatedAt = createdAt,
            Outcome = outcome,
            Reason = reason,
            IsManual = isManual,
        };

    private void Announce(Instance instance, BackupRecord record)
    {
        switch (record.Outcome)
        {
            case BackupOutcome.Success:
                Info(instance, $"Backup written: {record.FileName} ({record.SizeBytes:N0} bytes).");
                break;
            case BackupOutcome.Skipped:
                Info(instance, $"Backup skipped: {record.Reason}.", ConsoleLineKind.Warning);
                break;
            default:
                Info(instance, $"Backup failed: {record.Reason}.", ConsoleLineKind.Error);
                break;
        }
    }

    private void Info(Instance instance, string text, ConsoleLineKind kind = ConsoleLineKind.Info) =>
        console.Append(ConsoleChannels.Instance(instance.Id), new ConsoleLine(timeProvider.GetUtcNow(), text, kind));

    private static bool IsSharingViolation(IOException ex) => ex.HResult is SharingViolationHResult or LockViolationHResult;

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover .snap-* directory is harmless; the next backup creates a fresh one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the file if it can; true when the file is gone afterward, whatever the reason.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return !File.Exists(path);
    }

    private readonly record struct FileProbe(bool Openable, long Length, DateTime LastWriteUtc);

    private sealed record SnapshotResult(string Directory, IReadOnlyList<BackupManifestEntry> Entries, string? Skipped)
    {
        public static SnapshotResult Ok(string directory, IReadOnlyList<BackupManifestEntry> entries) => new(directory, entries, null);

        public static SnapshotResult Skip(string reason) => new(string.Empty, [], reason);
    }
}
