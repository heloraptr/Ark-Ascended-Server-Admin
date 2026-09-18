using System.IO.Compression;
using System.Text.Json.Nodes;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Backups;

/// <summary>B2: restore from a real archive the backup service wrote, with rollback, journals, recovery, and the caps.</summary>
public class RestoreServiceTests
{
    private const string MapKey = "TheIsland_WP";

    private const string GameUserSettings = "[ServerSettings]\r\nServerAdminPassword=secret\r\nRCONPort=27020\r\n";

    private static readonly byte[] _changed = [9, 9, 9];

    [Fact]
    public async Task Restore_WorldOnly_ReplacesTheSelectedSet_KeepsOtherFiles_AndLeavesASafetyCopy()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: true, ct);
        var file = await f.BackupAsync(ct);
        var original = f.SnapshotWorld();
        var clusterFile = Path.Combine(f.ClusterDirectory!, "transfers", "12345.dat");

        await File.WriteAllBytesAsync(f.WorldFile, _changed, ct);
        await File.WriteAllBytesAsync(Path.Combine(f.WorldDirectory, "333.arkprofile"), [1], ct);
        await File.WriteAllTextAsync(Path.Combine(f.WorldDirectory, "notes.txt"), "changed", ct);
        await File.WriteAllBytesAsync(clusterFile, [7], ct);
        RestoreRecord? announced = null;
        f.Backups.Restored += r => announced = r;

        var outcome = await f.Restore.RestoreAsync(f.Instance.Id, file, includeCluster: false, ct);

        Assert.True(outcome.Succeeded, outcome.Error);
        foreach (var (name, bytes) in original)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(f.WorldDirectory, name), ct));
        }

        Assert.False(File.Exists(Path.Combine(f.WorldDirectory, "333.arkprofile")), "a profile absent from the archive must go");
        Assert.Empty(Directory.GetFiles(f.WorldDirectory, "*.arkrbf"));
        Assert.Empty(Directory.GetFiles(f.WorldDirectory, "*_AntiCorruptionBackup.bak"));
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(f.WorldDirectory, "notes.txt"), ct));
        Assert.Equal([7], await File.ReadAllBytesAsync(clusterFile, ct));

        var safety = Assert.Single(Directory.GetDirectories(root.Layout.InstanceRestoreSafetyDirectory("alpha")));
        Assert.Equal(_changed, await File.ReadAllBytesAsync(Path.Combine(safety, "World", $"{MapKey}.ark"), ct));
        Assert.True(File.Exists(Path.Combine(safety, "World", "333.arkprofile")));
        Assert.True(File.Exists(Path.Combine(safety, "World", $"{MapKey}_AntiCorruptionBackup.bak")));
        Assert.False(Directory.Exists(Path.Combine(safety, "Cluster")));
        Assert.Empty(f.Journals.List());
        Assert.Empty(f.Locks.Holders);
        Assert.Empty(f.Locks.ReservedClusters);
        Assert.Empty(f.Jobs.ActiveNames);

        await using var db = root.CreateDbContext();
        var record = await db.RestoreRecords.SingleAsync(ct);
        Assert.Equal(RestoreOutcome.Success, record.Outcome);
        Assert.Equal(file, record.SourceFileName);
        Assert.False(record.IncludedCluster);
        Assert.Equal(record.Id, announced?.Id);
        Assert.Contains(f.Console.Snapshot(ConsoleChannels.Instance(f.Instance.Id)), l => l.Text.StartsWith("Restored ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_WithCluster_NeedsEverySiblingStopped_ThenReplacesTheClusterDirectory()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: true, ct);
        var file = await f.BackupAsync(ct);
        var clusterDirectory = f.ClusterDirectory!;
        var gameIni = Path.Combine(clusterDirectory, "Config", "Game.ini");
        var originalIni = await File.ReadAllTextAsync(gameIni, ct);
        await File.WriteAllTextAsync(gameIni, "changed", ct);
        await File.WriteAllBytesAsync(Path.Combine(clusterDirectory, "transfers", "extra.dat"), [1], ct);
        Directory.CreateDirectory(Path.Combine(clusterDirectory, "stale"));

        var inspection = await f.Restore.InspectAsync(f.Instance.Id, file, ct);
        Assert.True(inspection.CanRestore, inspection.Problem);
        Assert.True(inspection.ClusterAvailable, inspection.ClusterUnavailableReason);
        Assert.Equal(["beta"], inspection.SiblingNames);

        f.Processes.Set(f.Sibling!.Id, InstanceState.Running);
        var refused = await f.Restore.RestoreAsync(f.Instance.Id, file, includeCluster: true, ct);
        Assert.False(refused.Succeeded);
        Assert.Contains("beta (Running)", refused.Error, StringComparison.Ordinal);
        Assert.Equal("changed", await File.ReadAllTextAsync(gameIni, ct));
        Assert.Empty(f.Locks.ReservedClusters);
        Assert.Empty(f.Locks.Holders);

        f.Processes.Set(f.Sibling.Id, InstanceState.Stopped);
        var outcome = await f.Restore.RestoreAsync(f.Instance.Id, file, includeCluster: true, ct);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(originalIni, await File.ReadAllTextAsync(gameIni, ct));
        Assert.False(File.Exists(Path.Combine(clusterDirectory, "transfers", "extra.dat")));
        Assert.False(Directory.Exists(Path.Combine(clusterDirectory, "stale")));
        var safety = Assert.Single(Directory.GetDirectories(root.Layout.InstanceRestoreSafetyDirectory("alpha")));
        Assert.True(File.Exists(Path.Combine(safety, "Cluster", "transfers", "extra.dat")));
        Assert.Empty(f.Locks.ReservedClusters);
        Assert.Empty(f.Locks.Holders);

        await using var db = root.CreateDbContext();
        var record = await db.RestoreRecords.SingleAsync(ct);
        Assert.True(record.IncludedCluster);
        Assert.Equal(RestoreOutcome.Success, record.Outcome);
    }

    [Fact]
    public async Task Restore_IsRejected_WhenRunning_Locked_Unknown_OrClusterOnAStandaloneInstance()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: false, ct);
        var file = await f.BackupAsync(ct);

        f.Processes.Set(f.Instance.Id, InstanceState.Running);
        var running = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
        Assert.Contains("Stop alpha (Running) first", running.Error, StringComparison.Ordinal);
        f.Processes.Set(f.Instance.Id, InstanceState.Stopped);

        using (f.Locks.TryAcquire(f.Instance.Id))
        {
            var locked = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
            Assert.Contains("operation is in progress for alpha", locked.Error, StringComparison.Ordinal);
        }

        var unknown = await f.Restore.RestoreAsync(f.Instance.Id, "20260101-000000-1.zip", false, ct);
        Assert.Contains("no successful backup named", unknown.Error, StringComparison.Ordinal);
        var path = await f.Restore.RestoreAsync(f.Instance.Id, "..\\" + file, false, ct);
        Assert.Contains("not a plain file name", path.Error, StringComparison.Ordinal);

        var cluster = await f.Restore.RestoreAsync(f.Instance.Id, file, true, ct);
        Assert.Contains("not in a cluster", cluster.Error, StringComparison.Ordinal);
        var inspection = await f.Restore.InspectAsync(f.Instance.Id, file, ct);
        Assert.True(inspection.CanRestore);
        Assert.False(inspection.ClusterAvailable);
        Assert.Empty(inspection.SiblingNames);

        Assert.False(Directory.Exists(root.Layout.InstanceRestoreSafetyDirectory("alpha")), "a rejected restore touches nothing");
        await using var db = root.CreateDbContext();
        Assert.False(await db.RestoreRecords.AnyAsync(ct));
    }

    [Fact]
    public async Task Restore_IsRejected_WhenTheArchiveIsTampered_OrGrowsAnUnlistedEntry()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: false, ct);
        var file = await f.BackupAsync(ct);
        var archive = Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), file);
        await File.WriteAllBytesAsync(f.WorldFile, _changed, ct);

        Rewrite(archive, "World/111.arkprofile", bytes => { bytes[0] ^= 0xFF; return bytes; });
        var corrupt = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
        Assert.Contains("failed verification: World/111.arkprofile SHA-256", corrupt.Error, StringComparison.Ordinal);

        Rewrite(archive, "World/111.arkprofile", bytes => { bytes[0] ^= 0xFF; return bytes; });
        AddEntry(archive, "World/evil.arkprofile", [1]);
        var unlisted = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
        Assert.Contains("World/evil.arkprofile is in the archive but not in the manifest", unlisted.Error, StringComparison.Ordinal);

        var second = await f.BackupAsync(ct);
        AddEntry(Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), second), "../evil.txt", [1]);
        var traversal = await f.Restore.RestoreAsync(f.Instance.Id, second, false, ct);
        Assert.Contains("'../evil.txt' is not under World/ or Cluster/", traversal.Error, StringComparison.Ordinal);

        Assert.Equal(_changed, await File.ReadAllBytesAsync(f.WorldFile, ct));
        Assert.False(Directory.Exists(root.Layout.InstanceRestoreSafetyDirectory("alpha")));
        Assert.False(File.Exists(Path.Combine(root.Layout.Root, "evil.txt")));
    }

    [Fact]
    public async Task Restore_DisablesClusterData_ForAManifestWithoutClusterFields()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: true, ct);
        var file = await f.BackupAsync(ct);
        var archive = Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), file);
        Rewrite(archive, BackupManifest.FileName, bytes =>
        {
            var json = JsonNode.Parse(bytes)!.AsObject();
            json.Remove("clusterSlug");
            json.Remove("clusterCaptured");
            return System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        });

        var inspection = await f.Restore.InspectAsync(f.Instance.Id, file, ct);
        Assert.True(inspection.CanRestore, inspection.Problem);
        Assert.False(inspection.ClusterAvailable);
        Assert.Contains("no cluster data", inspection.ClusterUnavailableReason, StringComparison.Ordinal);

        var withCluster = await f.Restore.RestoreAsync(f.Instance.Id, file, true, ct);
        Assert.Contains("Cluster data cannot be included", withCluster.Error, StringComparison.Ordinal);
        Assert.True((await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct)).Succeeded);
    }

    [Fact]
    public async Task Restore_RollsBackFromTheSafetyCopy_WhenExtractionFails()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: false, ct);
        var file = await f.BackupAsync(ct);
        await File.WriteAllBytesAsync(f.WorldFile, _changed, ct);
        // A directory where the archive wants to write a file: the extraction fails after the selected set was deleted.
        File.Delete(Path.Combine(f.WorldDirectory, "222.arktribe"));
        Directory.CreateDirectory(Path.Combine(f.WorldDirectory, "222.arktribe"));

        var outcome = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("previous files were put back", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(_changed, await File.ReadAllBytesAsync(f.WorldFile, ct));
        Assert.True(Directory.Exists(Path.Combine(f.WorldDirectory, "222.arktribe")));
        Assert.True(File.Exists(Path.Combine(f.WorldDirectory, "111.arkprofile")));
        Assert.True(File.Exists(Path.Combine(f.WorldDirectory, $"{MapKey}_AntiCorruptionBackup.bak")));
        Assert.Empty(f.Journals.List());
        Assert.Empty(f.Locks.Holders);
        await using var db = root.CreateDbContext();
        Assert.Equal(RestoreOutcome.RolledBack, (await db.RestoreRecords.SingleAsync(ct)).Outcome);
    }

    [Fact]
    public async Task Restore_LeavesAJournal_WhenRollbackFails_AndRecoveryPutsTheFilesBack()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: false, ct);
        var file = await f.BackupAsync(ct);
        await File.WriteAllBytesAsync(f.WorldFile, _changed, ct);
        var profile = Path.Combine(f.WorldDirectory, "111.arkprofile");
        var beforeProfile = await File.ReadAllBytesAsync(profile, ct);

        string operationId;
        // Readable but not deletable: the safety copy succeeds, the delete fails, and so does the rollback's delete.
        using (new FileStream(profile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
            Assert.False(outcome.Succeeded);
            Assert.Contains("the rollback failed too", outcome.Error, StringComparison.Ordinal);

            var journal = Assert.Single(f.Journals.List());
            operationId = journal.OperationId;
            Assert.Equal(RestorePhase.RollbackFailed, journal.Phase);
            Assert.Equal([f.Instance.Id], journal.AffectedInstanceIds);
            Assert.Equal(file, journal.SourceFileName);
            Assert.True(Directory.Exists(journal.SafetyCopyPath));
            Assert.Empty(f.Locks.Holders);

            var again = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
            Assert.Contains("incomplete restore", again.Error, StringComparison.Ordinal);
            var missing = await f.Restore.RecoverAsync("nope", ct);
            Assert.Contains("no restore journal", missing.Error, StringComparison.Ordinal);

            f.Processes.Set(f.Instance.Id, InstanceState.Running);
            var running = await f.Restore.RecoverAsync(operationId, ct);
            Assert.Contains("Stop alpha (Running) first", running.Error, StringComparison.Ordinal);
            f.Processes.Set(f.Instance.Id, InstanceState.Stopped);
        }

        var recovered = await f.Restore.RecoverAsync(operationId, ct);

        Assert.True(recovered.Succeeded, recovered.Error);
        Assert.Empty(f.Journals.List());
        Assert.Equal(_changed, await File.ReadAllBytesAsync(f.WorldFile, ct));
        Assert.Equal(beforeProfile, await File.ReadAllBytesAsync(profile, ct));
        Assert.True(File.Exists(Path.Combine(f.WorldDirectory, "222.arktribe")));
        Assert.Empty(f.Locks.Holders);
        await using var db = root.CreateDbContext();
        Assert.Equal([RestoreOutcome.Failed, RestoreOutcome.RolledBack], (await db.RestoreRecords.OrderBy(r => r.Id).ToListAsync(ct)).Select(r => r.Outcome));

        var afterwards = await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct);
        Assert.True(afterwards.Succeeded, afterwards.Error);
    }

    [Fact]
    public async Task DiscardJournal_RemovesTheJournalAndNothingElse()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: true, ct);
        var file = await f.BackupAsync(ct);
        var journal = new RestoreJournal("alpha-by-hand", DateTimeOffset.UnixEpoch, f.Instance.Id, "alpha", MapKey, f.Instance.ClusterId, "cluster", [f.Instance.Id, f.Sibling!.Id], Path.Combine(root.Layout.InstanceRestoreSafetyDirectory("alpha"), "x"), file, RestorePhase.RollbackFailed);
        f.Journals.Write(journal);
        Directory.CreateDirectory(journal.SafetyCopyPath);

        using (f.Locks.TryAcquire(f.Sibling.Id))
        {
            var busy = await f.Restore.DiscardJournalAsync("alpha-by-hand", ct);
            Assert.Contains("operation is in progress for beta", busy.Error, StringComparison.Ordinal);
            Assert.NotEmpty(f.Journals.List());
        }

        var outcome = await f.Restore.DiscardJournalAsync("alpha-by-hand", ct);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(f.Journals.List());
        Assert.True(Directory.Exists(journal.SafetyCopyPath));
        Assert.Empty(f.Locks.Holders);
        Assert.Empty(f.Locks.ReservedClusters);
        Assert.True((await f.Restore.RestoreAsync(f.Instance.Id, file, true, ct)).Succeeded);
    }

    [Fact]
    public async Task Restore_CapsRecordsAtFifty_AndSafetyCopiesAtThree()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, clustered: false, ct);
        var file = await f.BackupAsync(ct);
        await using (var db = root.CreateDbContext())
        {
            for (var i = 0; i < RestoreService.RecordCap; i++)
            {
                db.RestoreRecords.Add(new RestoreRecord { InstanceId = f.Instance.Id, CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(i), SourceFileName = "old.zip", Outcome = RestoreOutcome.Failed, Reason = $"seed {i}" });
            }

            await db.SaveChangesAsync(ct);
        }

        for (var i = 0; i < RestoreService.SafetyCopiesKept + 1; i++)
        {
            Assert.True((await f.Restore.RestoreAsync(f.Instance.Id, file, false, ct)).Succeeded);
        }

        var copies = Directory.GetDirectories(root.Layout.InstanceRestoreSafetyDirectory("alpha")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(RestoreService.SafetyCopiesKept, copies.Count);
        Assert.DoesNotContain(copies, c => c!.EndsWith("-1", StringComparison.Ordinal));

        await using var check = root.CreateDbContext();
        var records = await check.RestoreRecords.OrderBy(r => r.Id).ToListAsync(ct);
        Assert.Equal(RestoreService.RecordCap, records.Count);
        Assert.Equal("seed 4", records[0].Reason);
        Assert.Equal(RestoreOutcome.Success, records[^1].Outcome);
    }

    private static void Rewrite(string archivePath, string entryName, Func<byte[], byte[]> transform)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = zip.GetEntry(entryName)!;
        byte[] original;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            original = buffer.ToArray();
        }

        entry.Delete();
        var replacement = zip.CreateEntry(entryName);
        using var output = replacement.Open();
        output.Write(transform(original));
    }

    private static void AddEntry(string archivePath, string entryName, byte[] bytes)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        using var output = zip.CreateEntry(entryName).Open();
        output.Write(bytes);
    }

    private sealed class SaveWorldRconClient : IRconClient
    {
        public Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(RconCommands.SaveWorldReply);
    }

    private sealed class Fixture
    {
        public static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        public required TempDataRoot Root { get; init; }

        public required Instance Instance { get; init; }

        public required Instance? Sibling { get; init; }

        public required BackupService Backups { get; init; }

        public required RestoreService Restore { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FakeInstanceLocks Locks { get; init; }

        public required FakeConsoleService Console { get; init; }

        public required RestoreJournalStore Journals { get; init; }

        public required DetachedJobs Jobs { get; init; }

        public required string WorldDirectory { get; init; }

        public required string? ClusterDirectory { get; init; }

        public string WorldFile => Path.Combine(WorldDirectory, $"{MapKey}.ark");

        /// <summary>The selected files by name, for comparing after a restore.</summary>
        public Dictionary<string, byte[]> SnapshotWorld() =>
            Directory.GetFiles(WorldDirectory)
                .Where(p => BackupInventory.IsWorldFileSelected(Path.GetFileName(p), MapKey))
                .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        /// <summary>Takes a real backup while "running", then marks the instance stopped so a restore may proceed.</summary>
        public async Task<string> BackupAsync(CancellationToken ct)
        {
            Processes.Set(Instance.Id, InstanceState.Running);
            var record = await Backups.BackupNowAsync(Instance.Id, isManual: true, ct);
            Assert.Equal(BackupOutcome.Success, record.Outcome);
            Processes.Set(Instance.Id, InstanceState.Stopped);
            return record.FileName!;
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, bool clustered, CancellationToken ct)
        {
            await root.InitializeAsync(ct);
            var instance = await TestSeed.InstanceAsync(root, "alpha", clustered, ct);
            var sibling = clustered ? await TestSeed.InstanceAsync(root, "beta", clustered: true, ct, i => { i.GamePort = 7787; i.RconPort = 27030; }) : null;

            var worldDirectory = root.Layout.InstanceWorldDirectory("alpha", MapKey);
            Directory.CreateDirectory(worldDirectory);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}.ark"), Random(200_000, 1), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, "111.arkprofile"), Random(5_000, 2), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, "222.arktribe"), Random(7_000, 3), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}_07.09.2026_12.00.00.arkrbf"), Random(20_000, 4), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}_AntiCorruptionBackup.bak"), Random(20_000, 5), ct);
            await File.WriteAllTextAsync(Path.Combine(worldDirectory, "notes.txt"), "not selected", ct);

            string? clusterDirectory = null;
            if (clustered)
            {
                clusterDirectory = root.Layout.ClusterDirectory("cluster");
                Directory.CreateDirectory(Path.Combine(clusterDirectory, "transfers"));
                Directory.CreateDirectory(Path.Combine(clusterDirectory, "Config"));
                await File.WriteAllBytesAsync(Path.Combine(clusterDirectory, "transfers", "12345.dat"), Random(3_000, 6), ct);
                await File.WriteAllTextAsync(Path.Combine(clusterDirectory, "Config", "Game.ini"), "[/Script/ShooterGame.ShooterGameMode]\r\n", ct);
            }

            var clock = new FastTimeProvider(Start);
            var locks = new FakeInstanceLocks();
            var processes = new FakeProcessManager();
            processes.Set(instance.Id, InstanceState.Stopped);
            var writer = Substitute.For<IGeneratedConfigWriter>();
            writer.ReadGeneratedGameUserSettingsAsync("alpha", Arg.Any<CancellationToken>()).Returns(GameUserSettings);
            var console = new FakeConsoleService();
            var journals = new RestoreJournalStore(root.Layout);
            var jobs = new DetachedJobs(clock);
            var restore = new RestoreService(root, root.Layout, locks, processes, jobs, journals, console, clock, NullLogger<RestoreService>.Instance);
            var backups = new BackupService(root, root.Layout, new AppSettingsStore(root), locks, processes, new SaveWorldRconClient(), writer, console, restore, clock, NullLogger<BackupService>.Instance);

            return new Fixture
            {
                Root = root,
                Instance = instance,
                Sibling = sibling,
                Backups = backups,
                Restore = restore,
                Processes = processes,
                Locks = locks,
                Console = console,
                Journals = journals,
                Jobs = jobs,
                WorldDirectory = worldDirectory,
                ClusterDirectory = clusterDirectory,
            };
        }

        private static byte[] Random(int length, int seed)
        {
            var bytes = new byte[length];
            new System.Random(seed).NextBytes(bytes);
            return bytes;
        }
    }
}
