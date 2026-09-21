using System.IO.Compression;
using System.Security.Cryptography;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Backups;

/// <summary>Plan step 34: the backup round trip (the restoration smoke test) and its negative cases.</summary>
public class BackupServiceTests
{
    private const string MapKey = "TheIsland_WP";

    private const string GameUserSettings = "[ServerSettings]\r\nServerAdminPassword=secret\r\nRCONPort=27020\r\n";

    [Fact]
    public async Task RoundTrip_ArchivesTheSelectedFiles_AndExtractsByteIdentical()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: true, ct);

        Assert.Equal(BackupOutcome.Success, record.Outcome);
        Assert.True(record.IsManual);
        Assert.NotNull(record.FileName);
        Assert.Matches(@"^\d{8}-\d{6}-1\.zip$", record.FileName);
        var archive = Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), record.FileName);
        Assert.True(File.Exists(archive));
        Assert.Equal(new FileInfo(archive).Length, record.SizeBytes);
        Assert.Equal(RconCommands.SaveWorld, fixture.Rcon.Commands.Single().Command);
        Assert.Equal(27020, fixture.Rcon.Commands.Single().Endpoint.Port);
        Assert.Equal("secret", fixture.Rcon.Commands.Single().Endpoint.Password);
        Assert.Empty(Directory.GetDirectories(root.Layout.InstanceBackupDirectory("alpha"), ".snap-*"));
        Assert.Empty(Directory.GetFiles(root.Layout.InstanceBackupDirectory("alpha"), ".tmp-*"));

        // Manifest matches the sources.
        using (var zip = ZipFile.OpenRead(archive))
        {
            using var reader = new StreamReader(zip.GetEntry(BackupManifest.FileName)!.Open());
            var manifest = BackupManifest.FromJson(await reader.ReadToEndAsync(ct));
            Assert.NotNull(manifest);
            Assert.Equal("alpha", manifest.InstanceSlug);
            Assert.Equal(MapKey, manifest.MapKey);
            Assert.True(manifest.ContainsWorldFile);
            Assert.Equal(
                ["Cluster/Config/Game.ini", "Cluster/transfers/12345.dat", "World/111.arkprofile", "World/222.arktribe", $"World/{MapKey}.ark"],
                manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
            foreach (var entry in manifest.Files)
            {
                var source = fixture.SourcePath(entry.Path);
                Assert.Equal(new FileInfo(source).Length, entry.Length);
                Assert.Equal(Sha256(await File.ReadAllBytesAsync(source, ct)), entry.Sha256);
            }
        }

        // Extract to a fresh directory and byte-compare with the source.
        var restore = Path.Combine(root.Layout.Root, "restore");
        ZipFile.ExtractToDirectory(archive, restore);
        foreach (var relative in new[] { $"World/{MapKey}.ark", "World/111.arkprofile", "World/222.arktribe", "Cluster/transfers/12345.dat", "Cluster/Config/Game.ini" })
        {
            var extracted = Path.Combine(restore, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(extracted), relative);
            Assert.Equal(await File.ReadAllBytesAsync(fixture.SourcePath(relative), ct), await File.ReadAllBytesAsync(extracted, ct));
        }

        Assert.Empty(Directory.GetFiles(restore, "*.arkrbf", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(restore, "*_AntiCorruptionBackup.bak", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(restore, "notes.txt", SearchOption.AllDirectories));

        await using var db = root.CreateDbContext();
        var stored = await db.BackupRecords.SingleAsync(ct);
        Assert.Equal(BackupOutcome.Success, stored.Outcome);
        Assert.Equal(record.FileName, stored.FileName);
        Assert.Contains(fixture.Console.Snapshot(ArkAscendedServerAdmin.Consoles.ConsoleChannels.Instance(fixture.Instance.Id)), l => l.Text.Contains("Backup written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Standalone_DoesNotArchiveTheClusterDirectory()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Success, record.Outcome);
        using var zip = ZipFile.OpenRead(Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), record.FileName!));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("Cluster/", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, e => e.FullName == $"World/{MapKey}.ark");
    }

    [Fact]
    public async Task TruncatedEntry_FailsVerification()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);
        fixture.Service.OnArchiveWritten = path => Rewrite(path, "World/222.arktribe", bytes => bytes[..(bytes.Length - 1)]);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.StartsWith("verification", record.Reason, StringComparison.Ordinal);
        Assert.Contains("222.arktribe", record.Reason, StringComparison.Ordinal);
        Assert.Null(record.FileName);
        Assert.Empty(Directory.GetFiles(root.Layout.InstanceBackupDirectory("alpha")));
    }

    [Fact]
    public async Task CorruptedEntryWithTheSameLength_FailsVerification()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);
        fixture.Service.OnArchiveWritten = path => Rewrite(path, $"World/{MapKey}.ark", bytes =>
        {
            var corrupted = (byte[])bytes.Clone();
            corrupted[corrupted.Length / 2] ^= 0xFF;
            return corrupted;
        });

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Contains("SHA-256", record.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root.Layout.InstanceBackupDirectory("alpha")));
    }

    [Fact]
    public async Task MissingEntry_FailsVerification()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);
        fixture.Service.OnArchiveWritten = path =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            zip.GetEntry("World/111.arkprofile")!.Delete();
        };

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Contains("111.arkprofile is missing", record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileAddedBetweenInventoriesEveryTime_IsSkippedAfterOneRetry()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);
        var passes = 0;
        fixture.Service.OnSnapshotCopied = _ =>
        {
            passes++;
            File.WriteAllBytes(Path.Combine(fixture.WorldDirectory, $"new{passes}.arkprofile"), [1, 2, 3]);
        };

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.Contains("changed during the snapshot", record.Reason, StringComparison.Ordinal);
        Assert.Contains("added World/new2.arkprofile", record.Reason, StringComparison.Ordinal);
        Assert.Equal(2, passes);
        Assert.Empty(Directory.GetFileSystemEntries(root.Layout.InstanceBackupDirectory("alpha")));
    }

    [Fact]
    public async Task FileAddedBetweenInventoriesOnce_IsRetriedAndIncluded()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: true, ct);
        var passes = 0;
        fixture.Service.OnSnapshotCopied = _ =>
        {
            if (passes++ == 0)
            {
                File.WriteAllBytes(Path.Combine(fixture.WorldDirectory, "late.arktribe"), [9, 9, 9]);
            }
        };

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Success, record.Outcome);
        Assert.Equal(2, passes);
        using var zip = ZipFile.OpenRead(Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), record.FileName!));
        Assert.Contains(zip.Entries, e => e.FullName == "World/late.arktribe");
    }

    [Fact]
    public async Task LockedWorldFile_IsSkippedAsInUseAfterThreeAttempts()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);
        var attempts = 0;
        fixture.Service.OnSnapshotCopied = _ => attempts++;

        BackupRecord record;
        using (new FileStream(fixture.WorldFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);
        }

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.StartsWith(BackupService.WorldFileInUseReason, record.Reason, StringComparison.Ordinal);
        Assert.Contains("3 attempts", record.Reason, StringComparison.Ordinal);
        Assert.Equal(0, attempts);
        Assert.Empty(Directory.GetFileSystemEntries(root.Layout.InstanceBackupDirectory("alpha")));
        // Quiescence window (10 s) plus three retry waits, all on the fast clock.
        Assert.True(fixture.Clock.GetUtcNow() - Fixture.Start >= TimeSpan.FromSeconds(10 + 2 * 10), fixture.Clock.GetUtcNow().ToString("O"));
    }

    [Theory]
    [InlineData(InstanceState.Stopped, BackupService.NotRunningReason)]
    [InlineData(InstanceState.Starting, BackupService.NotRunningReason)]
    [InlineData(InstanceState.Unreachable, BackupService.RconUnreachableReason)]
    [InlineData(InstanceState.StartingUnconfirmed, BackupService.RconUnreachableReason)]
    public async Task NotRunning_IsRecordedAsSkipped(InstanceState state, string reason)
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);
        fixture.Processes.Set(fixture.Instance.Id, state);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.StartsWith(reason, record.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Rcon.Commands);
        await using var db = root.CreateDbContext();
        Assert.Equal(BackupOutcome.Skipped, (await db.BackupRecords.SingleAsync(ct)).Outcome);
    }

    [Fact]
    public async Task SaveWorldFailure_IsRecordedAsSkipped()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);
        fixture.Rcon.Failure = new RconException(RconFailure.Timeout, "saveworld timed out");

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.Contains("saveworld timed out", record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingGeneratedSettings_IsRecordedAsSkipped()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);
        fixture.ConfigWriter.ReadGeneratedGameUserSettingsAsync("alpha", Arg.Any<CancellationToken>()).Returns((string?)null);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.StartsWith(BackupService.RconUnreachableReason, record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_KeepsTheNewestRetentionArchives_AndDeletesTheRest()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct, i => i.BackupRetention = 2);

        var names = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: i % 2 == 0, ct);
            Assert.Equal(BackupOutcome.Success, record.Outcome);
            names.Add(record.FileName!);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var remaining = Directory.GetFiles(root.Layout.InstanceBackupDirectory("alpha")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(names.Skip(2).Order(StringComparer.Ordinal), remaining);
        await using var db = root.CreateDbContext();
        var rows = await db.BackupRecords.OrderBy(r => r.Id).ToListAsync(ct);
        Assert.Equal(names.Skip(2), rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Backup_CapsTheInstancesSkippedAndFailedRows_AndLeavesTheRestAlone()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct, i => i.BackupRetention = 10);
        var other = await TestSeed.InstanceAsync(root, "beta", clustered: false, ct, i => { i.GamePort = 7787; i.RconPort = 27030; });
        await SeedRecordsAsync(root, fixture.Instance.Id, BackupRetention.UnsuccessfulRecordsKept + 1, 2, ct);
        await SeedRecordsAsync(root, other.Id, BackupRetention.UnsuccessfulRecordsKept + 1, 0, ct);

        var record = await fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: false, ct);

        Assert.Equal(BackupOutcome.Success, record.Outcome);
        await using var db = root.CreateDbContext();
        // SQLite cannot order by a DateTimeOffset, so the rows come back unordered and are sorted here.
        var rows = await db.BackupRecords.Where(r => r.InstanceId == fixture.Instance.Id).ToListAsync(ct);
        var unsuccessful = rows.Where(r => r.Outcome != BackupOutcome.Success).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        Assert.Equal(BackupRetention.UnsuccessfulRecordsKept, unsuccessful.Count);
        Assert.DoesNotContain(unsuccessful, r => r.Reason == "attempt 1");
        Assert.Equal("attempt 2", unsuccessful[0].Reason);
        Assert.Equal(3, rows.Count(r => r.Outcome == BackupOutcome.Success));
        Assert.Equal(BackupRetention.UnsuccessfulRecordsKept + 1, await db.BackupRecords.CountAsync(r => r.InstanceId == other.Id, ct));
    }

    /// <summary>Writes history straight to the table: <paramref name="unsuccessful"/> skipped or failed attempts, then <paramref name="successes"/> archives.</summary>
    private static async Task SeedRecordsAsync(TempDataRoot root, int instanceId, int unsuccessful, int successes, CancellationToken ct)
    {
        await using var db = root.CreateDbContext();
        for (var i = 1; i <= unsuccessful; i++)
        {
            db.BackupRecords.Add(new BackupRecord
            {
                InstanceId = instanceId,
                CreatedAt = Fixture.Start.AddMinutes(-unsuccessful + i - 1),
                Outcome = i % 2 == 0 ? BackupOutcome.Failed : BackupOutcome.Skipped,
                Reason = $"attempt {i}",
            });
        }

        for (var i = 1; i <= successes; i++)
        {
            db.BackupRecords.Add(new BackupRecord
            {
                InstanceId = instanceId,
                CreatedAt = Fixture.Start.AddMinutes(-successes + i - 1),
                Outcome = BackupOutcome.Success,
                FileName = $"old-{i}.zip",
                SizeBytes = 100,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task BackupNow_WaitsForTheInstanceLock()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.CreateAsync(root, clustered: false, ct);

        var held = fixture.Locks.TryAcquire(fixture.Instance.Id);
        Assert.NotNull(held);
        var backup = fixture.Service.BackupNowAsync(fixture.Instance.Id, isManual: true, ct);
        await Task.Delay(100, ct);
        Assert.False(backup.IsCompleted);

        held.Dispose();
        var record = await backup.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(BackupOutcome.Success, record.Outcome);
        Assert.Empty(fixture.Locks.Holders);
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

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Exposes the two protected seams as delegates.</summary>
    private sealed class TamperingBackupService(
        TempDataRoot root,
        IAppSettingsStore settings,
        IInstanceLocks locks,
        IProcessManager processes,
        IRconClient rcon,
        IGeneratedConfigWriter writer,
        ArkAscendedServerAdmin.Consoles.IConsoleService console,
        TimeProvider clock)
        : BackupService(root, root.Layout, settings, locks, processes, rcon, writer, console, new RestoreService(root, root.Layout, locks, processes, new DetachedJobs(clock), new RestoreJournalStore(root.Layout), console, clock, NullLogger<RestoreService>.Instance), clock, NullLogger<BackupService>.Instance)
    {
        public Action<string>? OnSnapshotCopied { get; set; }

        public Action<string>? OnArchiveWritten { get; set; }

        protected override Task OnSnapshotCopiedAsync(string snapshotDirectory, CancellationToken cancellationToken)
        {
            OnSnapshotCopied?.Invoke(snapshotDirectory);
            return Task.CompletedTask;
        }

        protected override Task OnArchiveWrittenAsync(string tempArchivePath, CancellationToken cancellationToken)
        {
            OnArchiveWritten?.Invoke(tempArchivePath);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRconClient : IRconClient
    {
        public List<(RconEndpoint Endpoint, string Command)> Commands { get; } = [];

        public RconException? Failure { get; set; }

        public Task<string> ExecuteAsync(RconEndpoint endpoint, string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add((endpoint, command));
            if (Failure is { } failure)
            {
                throw failure;
            }

            Assert.Equal(TimeSpan.FromSeconds(10), timeout);
            return Task.FromResult(RconCommands.SaveWorldReply);
        }
    }

    private sealed class Fixture
    {
        public static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        public required TempDataRoot Root { get; init; }

        public required Instance Instance { get; init; }

        public required TamperingBackupService Service { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FakeInstanceLocks Locks { get; init; }

        public required RecordingRconClient Rcon { get; init; }

        public required IGeneratedConfigWriter ConfigWriter { get; init; }

        public required FakeConsoleService Console { get; init; }

        public required FastTimeProvider Clock { get; init; }

        public required string WorldDirectory { get; init; }

        public required string? ClusterDirectory { get; init; }

        public string WorldFile => Path.Combine(WorldDirectory, $"{MapKey}.ark");

        public string SourcePath(string archiveRelativePath)
        {
            var slash = archiveRelativePath.IndexOf('/', StringComparison.Ordinal);
            var rest = archiveRelativePath[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
            return archiveRelativePath.StartsWith("World/", StringComparison.Ordinal)
                ? Path.Combine(WorldDirectory, rest)
                : Path.Combine(ClusterDirectory!, rest);
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, bool clustered, CancellationToken ct, Action<Instance>? configure = null)
        {
            await root.InitializeAsync(ct);
            var instance = await TestSeed.InstanceAsync(root, "alpha", clustered, ct, configure);

            var worldDirectory = root.Layout.InstanceWorldDirectory("alpha", MapKey);
            Directory.CreateDirectory(worldDirectory);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}.ark"), Random(200_000, 1), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, "111.arkprofile"), Random(5_000, 2), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, "222.arktribe"), Random(7_000, 3), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}_07.09.2026_12.00.00.arkrbf"), Random(200_000, 4), ct);
            await File.WriteAllBytesAsync(Path.Combine(worldDirectory, $"{MapKey}_AntiCorruptionBackup.bak"), Random(200_000, 5), ct);
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
            processes.Set(instance.Id, InstanceState.Running);
            var rcon = new RecordingRconClient();
            var writer = Substitute.For<IGeneratedConfigWriter>();
            writer.ReadGeneratedGameUserSettingsAsync("alpha", Arg.Any<CancellationToken>()).Returns(GameUserSettings);
            var console = new FakeConsoleService();
            var service = new TamperingBackupService(root, new AppSettingsStore(root), locks, processes, rcon, writer, console, clock);

            return new Fixture
            {
                Root = root,
                Instance = instance,
                Service = service,
                Processes = processes,
                Locks = locks,
                Rcon = rcon,
                ConfigWriter = writer,
                Console = console,
                Clock = clock,
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
