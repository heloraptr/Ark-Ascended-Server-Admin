using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Maintenance;

/// <summary>Instance delete (plan step 30) against the real database and a fake junction layout.</summary>
public class InstanceDeleteServiceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Delete_RemovesRowsAndDirectories()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        var outcome = await f.Service.DeleteAsync(f.Instance.Id, keepWorldData: false, ct);
        Assert.True(outcome.Succeeded, outcome.Error);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        await using var db = root.CreateDbContext();
        Assert.False(await db.Instances.AnyAsync(ct));
        Assert.False(await db.InstanceMods.AnyAsync(ct));
        Assert.False(await db.ExtraOverrides.AnyAsync(ct));
        Assert.False(await db.IniDocuments.AnyAsync(ct));
        Assert.False(await db.BackupRecords.AnyAsync(ct));
        Assert.True(await db.ModLibrary.AnyAsync(ct)); // the library entry itself survives
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory("alpha")));
        Assert.False(Directory.Exists(root.Layout.InstanceBackupDirectory("alpha")));
        Assert.Empty(Directory.GetDirectories(root.Layout.Archive));
        Assert.Equal(["alpha"], f.Layout.RemovedJunctions);
        f.Firewall.Received(1).RemoveInstanceRules(f.Instance.Id);
        Assert.Empty(f.Processes.Stops);
        Assert.Empty(f.Locks.Holders);
        Assert.Contains(f.Console.Snapshot(ConsoleChannels.Instance(f.Instance.Id)), l => l.Kind == ConsoleLineKind.Info && l.Text.Contains("deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_WithKeepWorldData_ArchivesSavedAndKeepsBackups()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        Assert.True((await f.Service.DeleteAsync(f.Instance.Id, keepWorldData: true, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        var archives = Directory.GetDirectories(root.Layout.Archive);
        var archive = Assert.Single(archives);
        Assert.StartsWith("alpha-", Path.GetFileName(archive), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(archive, "alpha", "TheIsland_WP", "TheIsland_WP.ark")));
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory("alpha")));
        Assert.True(File.Exists(Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), "old.zip")));
        await using var db = root.CreateDbContext();
        Assert.False(await db.Instances.AnyAsync(ct));
    }

    [Fact]
    public async Task Delete_StopsALiveInstanceWithVerifiedExitFirst()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);

        Assert.True((await f.Service.DeleteAsync(f.Instance.Id, keepWorldData: false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        var stop = Assert.Single(f.Processes.Stops);
        Assert.True(stop.Options.RequireVerifiedExit);
        await using var db = root.CreateDbContext();
        Assert.False(await db.Instances.AnyAsync(ct));
    }

    [Fact]
    public async Task Delete_AbortsWhenTheExitCannotBeVerified()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Instance.Id, InstanceState.Running);
        f.Processes.StopOutcomes[f.Instance.Id] = OperationOutcome.Rejected("exit not verified");

        Assert.True((await f.Service.DeleteAsync(f.Instance.Id, keepWorldData: false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        await using var db = root.CreateDbContext();
        Assert.True(await db.Instances.AnyAsync(ct));
        Assert.True(Directory.Exists(root.Layout.InstanceDirectory("alpha")));
        f.Firewall.DidNotReceive().RemoveInstanceRules(Arg.Any<int>());
        Assert.Empty(f.Layout.RemovedJunctions);
        Assert.Empty(f.Locks.Holders);
        Assert.Contains(f.Console.Snapshot(ConsoleChannels.Instance(f.Instance.Id)), l => l.Kind == ConsoleLineKind.Error && l.Text.Contains("exit not verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_IsRejectedWhileAnotherOperationHoldsTheLock()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        using (f.Locks.TryAcquire(f.Instance.Id))
        {
            var outcome = await f.Service.DeleteAsync(f.Instance.Id, keepWorldData: false, ct);

            Assert.False(outcome.Succeeded);
            Assert.Contains("in progress", outcome.Error, StringComparison.Ordinal);
        }

        await using var db = root.CreateDbContext();
        Assert.True(await db.Instances.AnyAsync(ct));
    }

    [Fact]
    public async Task Delete_OfAnUnknownInstance_IsRejected()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        var outcome = await f.Service.DeleteAsync(999, keepWorldData: false, ct);

        Assert.False(outcome.Succeeded);
    }

    private sealed class Fixture
    {
        public required InstanceDeleteService Service { get; init; }

        public required Instance Instance { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FakeInstanceLocks Locks { get; init; }

        public required FakeInstanceLayoutService Layout { get; init; }

        public required IFirewallRules Firewall { get; init; }

        public required FakeConsoleService Console { get; init; }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, CancellationToken ct)
        {
            await root.InitializeAsync(ct);
            var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);

            await using (var db = root.CreateDbContext())
            {
                db.ModLibrary.Add(new ModLibraryEntry { Id = 42, Name = "Mod", AddedAt = DateTimeOffset.UnixEpoch });
                db.InstanceMods.Add(new InstanceMod { InstanceId = instance.Id, ModId = 42, Order = 0 });
                db.ExtraOverrides.Add(new ExtraOverride { InstanceId = instance.Id, File = IniFile.Game, Section = "S", Key = "K", Value = "V" });
                db.IniDocuments.Add(new IniDocument { InstanceId = instance.Id, File = IniFile.GameUserSettings, Text = string.Empty, Sha256 = "e3b0", UpdatedAt = DateTimeOffset.UnixEpoch });
                db.BackupRecords.Add(new BackupRecord { InstanceId = instance.Id, CreatedAt = DateTimeOffset.UnixEpoch, Outcome = BackupOutcome.Success, FileName = "old.zip", SizeBytes = 3 });
                await db.SaveChangesAsync(ct);
            }

            var world = root.Layout.InstanceWorldDirectory("alpha", "TheIsland_WP");
            Directory.CreateDirectory(world);
            await File.WriteAllBytesAsync(Path.Combine(world, "TheIsland_WP.ark"), [1, 2, 3], ct);
            Directory.CreateDirectory(root.Layout.InstanceBackupDirectory("alpha"));
            await File.WriteAllBytesAsync(Path.Combine(root.Layout.InstanceBackupDirectory("alpha"), "old.zip"), [1, 2, 3], ct);

            var clock = new FastTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
            var locks = new FakeInstanceLocks();
            var processes = new FakeProcessManager();
            processes.Set(instance.Id, InstanceState.Stopped);
            var firewall = Substitute.For<IFirewallRules>();
            var layout = new FakeInstanceLayoutService(root, clock);
            var console = new FakeConsoleService();
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(CancellationToken.None);

            var service = new InstanceDeleteService(root, root.Layout, locks, processes, firewall, layout, console, lifetime, clock, NullLogger<InstanceDeleteService>.Instance);

            return new Fixture
            {
                Service = service,
                Instance = instance,
                Processes = processes,
                Locks = locks,
                Layout = layout,
                Firewall = firewall,
                Console = console,
            };
        }
    }
}
