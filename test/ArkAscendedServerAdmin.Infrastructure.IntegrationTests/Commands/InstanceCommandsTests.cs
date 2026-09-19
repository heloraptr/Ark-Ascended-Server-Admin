using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class InstanceCommandsTests
{
    private static InstanceDraft Draft(int mapId, string name = "My Island", int gamePort = 7777, int rconPort = 27020) =>
        new()
        {
            Name = name,
            MapId = mapId,
            SessionName = $"{name} session",
            MaxPlayers = 20,
            GamePort = gamePort,
            RconPort = rconPort,
        };

    private static async Task<(CommandTestHost Host, int MapId)> StartAsync(CancellationToken ct)
    {
        var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        return (host, await host.MapIdAsync(ct));
    }

    // ---- authorization ------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            host.Guard.Deny = true;

            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.GetDashboardAsync(ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.GetAsync(1, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.StartAsync(1, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.StopAsync(1, false, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.StartManyAsync([1], ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.BackupNowAsync(1, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.DeleteAsync(1, new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false), ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.SendRconAsync(1, "saveworld", ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.CreateAsync(Draft(mapId), ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Instances.PreviewLaunchAsync(1, ct));

            Assert.Empty(host.ProcessManager.Starts);
            await using var db = host.Db();
            Assert.Empty(await db.Instances.ToListAsync(ct));
        }
    }

    // ---- create -------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_Standalone_WritesTheRowTheLayoutAndTheGameDefaultIniSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var draft = Draft(mapId) with
            {
                AdminWhitelist = "  0002abc \r\n\r\n0002def\n",
                LaunchFlags = new LaunchFlags { ServerPlatform = " PC ", AdditionalArgs = " -NoTransferFromFiltering " },
                Mods = [5, 3, 5],
            };
            await host.AddLibraryModAsync(3, "Three", ct);
            await host.AddLibraryModAsync(5, "Five", ct);

            var result = await host.Instances.CreateAsync(draft, ct);

            Assert.True(result.Succeeded, result.Error);
            var instance = await host.InstanceAsync(result.Value, ct);
            Assert.Equal("My Island", instance.Name);
            Assert.Equal("my-island", instance.Slug);
            Assert.Null(instance.ClusterId);
            Assert.Equal("0002abc\r\n0002def", instance.AdminWhitelist);
            Assert.Equal("PC", instance.LaunchFlags.ServerPlatform);
            Assert.Equal("-NoTransferFromFiltering", instance.LaunchFlags.AdditionalArgs);
            Assert.Equal(CommandTestHost.Now, instance.CreatedAt);
            Assert.Equal([5, 3], instance.Mods.OrderBy(m => m.Order).Select(m => m.ModId));

            Assert.True(host.LayoutService.IsComplete("my-island"));

            var gameUserSettings = await host.IniStore.LoadAsync(IniOwner.ForInstance(instance.Id), IniFile.GameUserSettings, ct);
            Assert.Equal(IniTemplates.DefaultGameUserSettings, gameUserSettings.Text);
            Assert.False(gameUserSettings.MirrorStale);
            var game = await host.IniStore.LoadAsync(IniOwner.ForInstance(instance.Id), IniFile.Game, ct);
            Assert.Equal(IniTemplates.DefaultGameIni, game.Text);
            Assert.True(File.Exists(Path.Combine(host.Root.Layout.InstanceConfigSourceDirectory("my-island"), "Game.ini")));
        }
    }

    [Fact]
    public async Task Create_WithAdminPassword_WritesItIntoTheSeededGameUserSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var defaults = await host.Instances.CreateAsync(Draft(mapId, "Defaults") with { AdminPassword = " hunter2 " }, ct);
            var blank = await host.Instances.CreateAsync(Draft(mapId, "Blank", 7779, 27021) with { ConfigSource = ConfigSourceKind.Blank, AdminPassword = "s3cret" }, ct);
            var copied = await host.Instances.CreateAsync(
                Draft(mapId, "Copied", 7781, 27022) with { ConfigSource = ConfigSourceKind.CopyFromInstance, ConfigSourceId = defaults.Value, AdminPassword = "different" }, ct);
            var kept = await host.Instances.CreateAsync(
                Draft(mapId, "Kept", 7783, 27023) with { ConfigSource = ConfigSourceKind.CopyFromInstance, ConfigSourceId = defaults.Value }, ct);
            var rejected = await host.Instances.CreateAsync(Draft(mapId, "Rejected", 7785, 27024) with { AdminPassword = "two\nlines" }, ct);

            Assert.True(defaults.Succeeded, defaults.Error);
            Assert.True(blank.Succeeded, blank.Error);
            Assert.True(copied.Succeeded, copied.Error);
            Assert.True(kept.Succeeded, kept.Error);
            Assert.False(rejected.Succeeded);
            Assert.Contains(rejected.Errors, e => e.Contains("Admin password", StringComparison.Ordinal) && e.Contains("line breaks", StringComparison.Ordinal));

            Assert.Equal("hunter2", await PasswordAsync(host, defaults.Value, ct));
            Assert.Equal("s3cret", await PasswordAsync(host, blank.Value, ct));
            Assert.Equal("different", await PasswordAsync(host, copied.Value, ct));
            Assert.Equal("hunter2", await PasswordAsync(host, kept.Value, ct));

            // The template's slot is replaced in place, not appended a second time.
            var text = (await host.IniStore.LoadAsync(IniOwner.ForInstance(defaults.Value), IniFile.GameUserSettings, ct)).Text;
            Assert.Equal(1, text.Split("ServerAdminPassword=").Length - 1);
            Assert.Null((await host.Instances.PreviewLaunchAsync(defaults.Value, ct)).Value!.Problem);
        }

        static async Task<string?> PasswordAsync(CommandTestHost host, int instanceId, CancellationToken ct) =>
            IniText.Parse((await host.IniStore.LoadAsync(IniOwner.ForInstance(instanceId), IniFile.GameUserSettings, ct)).Text)
                .Get(IniGenerator.ServerSettingsSection, "ServerAdminPassword");
    }

    [Fact]
    public async Task Create_Blank_SeedsEmptyFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var result = await host.Instances.CreateAsync(Draft(mapId) with { ConfigSource = ConfigSourceKind.Blank }, ct);

            Assert.True(result.Succeeded, result.Error);
            var game = await host.IniStore.LoadAsync(IniOwner.ForInstance(result.Value), IniFile.Game, ct);
            Assert.Equal(string.Empty, game.Text);
            Assert.False(game.MirrorStale, "an empty source file is still mirrored");
        }
    }

    [Fact]
    public async Task Create_CopyFromInstanceAndCluster_CopiesTheSourceText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            const string customGame = "[/Script/ShooterGame.ShooterGameMode]\r\nTamingSpeedMultiplier=5\r\n";
            var source = await host.Instances.CreateAsync(Draft(mapId, "Source"), ct);
            var current = await host.IniStore.LoadAsync(IniOwner.ForInstance(source.Value), IniFile.Game, ct);
            Assert.True((await host.IniStore.SaveAsync(IniOwner.ForInstance(source.Value), IniFile.Game, customGame, current.Sha256, ct)).Succeeded);

            var cluster = await host.Clusters.CreateAsync("Origin", ConfigSourceKind.CopyFromInstance, source.Value, ct);
            Assert.True(cluster.Succeeded, cluster.Error);

            var fromInstance = await host.Instances.CreateAsync(
                Draft(mapId, "Copy A", 7779, 27021) with { ConfigSource = ConfigSourceKind.CopyFromInstance, ConfigSourceId = source.Value }, ct);
            var fromCluster = await host.Instances.CreateAsync(
                Draft(mapId, "Copy B", 7781, 27022) with { ConfigSource = ConfigSourceKind.CopyFromCluster, ConfigSourceId = cluster.Value }, ct);

            Assert.True(fromInstance.Succeeded, fromInstance.Error);
            Assert.True(fromCluster.Succeeded, fromCluster.Error);
            Assert.Equal(customGame, (await host.IniStore.LoadAsync(IniOwner.ForInstance(fromInstance.Value), IniFile.Game, ct)).Text);
            Assert.Equal(customGame, (await host.IniStore.LoadAsync(IniOwner.ForCluster(cluster.Value), IniFile.Game, ct)).Text);
            Assert.Equal(customGame, (await host.IniStore.LoadAsync(IniOwner.ForInstance(fromCluster.Value), IniFile.Game, ct)).Text);
        }
    }

    [Fact]
    public async Task Create_Clustered_DoesNotSeedAnInstanceIniSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var cluster = await host.Clusters.CreateAsync("Survivors", ConfigSourceKind.GameDefaults, null, ct);

            var result = await host.Instances.CreateAsync(Draft(mapId) with { ClusterId = cluster.Value }, ct);

            Assert.True(result.Succeeded, result.Error);
            var instance = await host.InstanceAsync(result.Value, ct);
            Assert.Equal(cluster.Value, instance.ClusterId);
            Assert.True(host.LayoutService.IsComplete(instance.Slug));
            await using var db = host.Db();
            Assert.Empty(await db.IniDocuments.Where(d => d.InstanceId == instance.Id).ToListAsync(ct));
            Assert.False(Directory.Exists(host.Root.Layout.InstanceConfigSourceDirectory(instance.Slug)));
        }
    }

    [Fact]
    public async Task Create_ReportsEveryValidationProblemAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            Assert.True((await host.Instances.CreateAsync(Draft(mapId, "Taken"), ct)).Succeeded);
            Assert.True((await host.Instances.CreateAsync(Draft(mapId, "Other", 7779, 27021), ct)).Succeeded);

            // The port allocator skips owners with the candidate's own name (so an instance can re-check itself),
            // which is why the port clash here is with "Other" while the name clash is with "Taken".
            var draft = new InstanceDraft
            {
                Name = "  taken ",
                MapId = 0,
                ClusterId = 999,
                SessionName = "bad?name",
                MaxPlayers = 0,
                GamePort = 7779,
                RconPort = 27021,
                Mods = [404],
                LaunchFlags = new LaunchFlags { ServerPlatform = "a=b", ActiveEvent = "x?y", AdditionalArgs = "-port=1" },
                BackupIntervalMinutes = 0,
                BackupRetention = 5000,
            };

            var result = await host.Instances.CreateAsync(draft, ct);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("already exists", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("SessionName must not contain", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("Max players must be between", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("ServerPlatform must not contain", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("ActiveEvent must not contain", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Contains("-port=1", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("Backup interval must be", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.StartsWith("Backups to keep must be", StringComparison.Ordinal));
            Assert.Contains("Choose a map.", result.Errors);
            Assert.Contains("The chosen cluster no longer exists.", result.Errors);
            Assert.Contains("One of the chosen mods is no longer in the library.", result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("7779", StringComparison.Ordinal) && e.Contains("Other", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Contains("27021", StringComparison.Ordinal));

            await using var db = host.Db();
            Assert.Equal(2, await db.Instances.CountAsync(ct));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RequiresAName(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var result = await host.Instances.CreateAsync(Draft(mapId) with { Name = name }, ct);

            Assert.False(result.Succeeded);
            Assert.Contains("Instance name is required.", result.Errors);
        }
    }

    [Fact]
    public async Task Create_RefusesThePortTheWebUiListensOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var result = await host.Instances.CreateAsync(Draft(mapId, gamePort: CommandTestHost.WebPort, rconPort: 27020), ct);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains(CommandTestHost.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Create_SkipsSlugsReservedByArchivedWorldsAndClusters()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            Directory.CreateDirectory(Path.Combine(host.Root.Layout.Archive, "my-island-20260101-000000"));
            Assert.True((await host.Clusters.CreateAsync("Shared", ConfigSourceKind.Blank, null, ct)).Succeeded);

            var archived = await host.Instances.CreateAsync(Draft(mapId), ct);
            var clusterNamed = await host.Instances.CreateAsync(Draft(mapId, "Shared", 7779, 27021), ct);

            Assert.True(archived.Succeeded, archived.Error);
            Assert.True(clusterNamed.Succeeded, clusterNamed.Error);
            var first = await host.InstanceAsync(archived.Value, ct);
            var second = await host.InstanceAsync(clusterNamed.Value, ct);
            Assert.NotEqual("my-island", first.Slug);
            Assert.StartsWith("my-island", first.Slug, StringComparison.Ordinal);
            Assert.NotEqual("shared", second.Slug);
            Assert.StartsWith("shared", second.Slug, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Create_WhenProvisioningFails_RemovesTheRowAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            // A file where the instance directory must go makes the layout's CreateDirectory throw.
            File.WriteAllText(host.Root.Layout.InstanceDirectory("broken"), "in the way");

            var result = await host.Instances.CreateAsync(Draft(mapId, "Broken"), ct);

            Assert.False(result.Succeeded);
            Assert.StartsWith("The instance folder could not be prepared:", result.Error, StringComparison.Ordinal);
            await using var db = host.Db();
            Assert.Empty(await db.Instances.ToListAsync(ct));
        }
    }

    // ---- read side ----------------------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_SortsByNameAndCarriesTheNewestBackup()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var zulu = await host.Clusters.CreateAsync("Zulu", ConfigSourceKind.Blank, null, ct);
            var alpha = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);
            var beta = await host.Instances.CreateAsync(Draft(mapId, "Beta") with { ClusterId = zulu.Value }, ct);
            var able = await host.Instances.CreateAsync(Draft(mapId, "Able", 7779, 27021), ct);
            await using (var db = host.Db())
            {
                db.BackupRecords.Add(new BackupRecord { InstanceId = beta.Value, CreatedAt = CommandTestHost.Now, Outcome = BackupOutcome.Failed, Reason = "old" });
                db.BackupRecords.Add(new BackupRecord { InstanceId = beta.Value, CreatedAt = CommandTestHost.Now, Outcome = BackupOutcome.Success, FileName = "new.zip" });
                await db.SaveChangesAsync(ct);
            }

            var dashboard = await host.Instances.GetDashboardAsync(ct);

            Assert.Equal(["Alpha", "Zulu"], dashboard.Clusters.Select(c => c.Name));
            Assert.Equal(alpha.Value, dashboard.Clusters[0].Id);
            Assert.Equal(["Able", "Beta"], dashboard.Instances.Select(i => i.Name));
            Assert.Equal(able.Value, dashboard.Instances[0].Id);
            Assert.Null(dashboard.Instances[0].LastBackup);
            Assert.Equal("new.zip", dashboard.Instances[1].LastBackup?.FileName);
            Assert.Equal("TheIsland_WP", dashboard.Instances[1].MapKey);
            Assert.Equal(zulu.Value, dashboard.Instances[1].ClusterId);
        }
    }

    [Fact]
    public async Task Get_LoadsClusterModsInClusterOrderAndInstanceModsInInstanceOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            foreach (var id in new[] { 1, 2, 3, 4 })
            {
                await host.AddLibraryModAsync(id, $"Mod {id}", ct);
            }

            var cluster = await host.Clusters.CreateAsync("Survivors", ConfigSourceKind.Blank, null, ct);
            Assert.True((await host.Clusters.SetModsAsync(cluster.Value, [4, 1], ct)).Succeeded);
            var created = await host.Instances.CreateAsync(Draft(mapId) with { ClusterId = cluster.Value, Mods = [3, 2] }, ct);

            var detail = await host.Instances.GetAsync(created.Value, ct);

            Assert.NotNull(detail);
            Assert.Equal([4, 1], detail.ClusterMods.Select(m => m.Mod.Id));
            Assert.Equal([3, 2], detail.InstanceMods.Select(m => m.Mod.Id));
            Assert.Equal("Survivors", detail.Instance.Cluster?.Name);
            Assert.Equal("TheIsland_WP", detail.Instance.Map?.Key);
            Assert.Null(await host.Instances.GetAsync(999, ct));
        }
    }

    [Fact]
    public async Task DisabledMods_StayListed_ButLeaveTheCommandLineAndTheCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            foreach (var id in new[] { 1, 2, 3, 4 })
            {
                await host.AddLibraryModAsync(id, $"Mod {id}", ct);
            }

            var cluster = await host.Clusters.CreateAsync("Survivors", ConfigSourceKind.Blank, null, ct);
            Assert.True((await host.Clusters.SetModsAsync(cluster.Value, [new ModSelection(4, Enabled: false), 1], ct)).Succeeded);
            var created = await host.Instances.CreateAsync(Draft(mapId) with { ClusterId = cluster.Value, Mods = [3, new ModSelection(2, Enabled: false)] }, ct);
            Assert.True(created.Succeeded, created.Error);

            var detail = await host.Instances.GetAsync(created.Value, ct);
            var preview = await host.Instances.PreviewLaunchAsync(created.Value, ct);
            var dashboard = await host.Instances.GetDashboardAsync(ct);
            var clusters = await host.Clusters.ListAsync(ct);

            Assert.Equal([(4, false), (1, true)], detail!.ClusterMods.Select(m => (m.Mod.Id, m.Enabled)));
            Assert.Equal([(3, true), (2, false)], detail.InstanceMods.Select(m => (m.Mod.Id, m.Enabled)));
            Assert.Contains("-mods=1,3", preview.Value!.CommandLine);
            Assert.DoesNotContain("4", preview.Value.CommandLine.Split(' ').Single(a => a.StartsWith("-mods=", StringComparison.Ordinal)));
            Assert.Equal(2, dashboard.Instances.Single().ModCount);
            Assert.Equal(1, clusters.Single().ModCount);

            // Re-enabling is a plain save of the same list; the row keeps its place.
            Assert.True((await host.Instances.SetModsAsync(created.Value, [3, 2], ct)).Succeeded);
            Assert.Equal([(3, true), (2, true)], (await host.Instances.GetAsync(created.Value, ct))!.InstanceMods.Select(m => (m.Mod.Id, m.Enabled)));
            Assert.Equal(3, (await host.Instances.GetDashboardAsync(ct)).Instances.Single().ModCount);
            Assert.Contains("-mods=1,3,2", (await host.Instances.PreviewLaunchAsync(created.Value, ct)).Value!.CommandLine);
        }
    }

    [Fact]
    public async Task GetBackups_ReturnsNewestFirstForThatInstanceOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var one = await host.Instances.CreateAsync(Draft(mapId, "One"), ct);
            var two = await host.Instances.CreateAsync(Draft(mapId, "Two", 7779, 27021), ct);
            await using (var db = host.Db())
            {
                db.BackupRecords.Add(new BackupRecord { InstanceId = one.Value, CreatedAt = CommandTestHost.Now, Outcome = BackupOutcome.Success, FileName = "first.zip" });
                db.BackupRecords.Add(new BackupRecord { InstanceId = two.Value, CreatedAt = CommandTestHost.Now, Outcome = BackupOutcome.Success, FileName = "other.zip" });
                db.BackupRecords.Add(new BackupRecord { InstanceId = one.Value, CreatedAt = CommandTestHost.Now, Outcome = BackupOutcome.Skipped, Reason = "second" });
                await db.SaveChangesAsync(ct);
            }

            var backups = await host.Instances.GetBackupsAsync(one.Value, ct);

            Assert.Equal([BackupOutcome.Skipped, BackupOutcome.Success], backups.Select(b => b.Outcome));
            Assert.All(backups, b => Assert.Equal(one.Value, b.InstanceId));
        }
    }

    // ---- lifecycle ----------------------------------------------------------------------------------

    [Fact]
    public async Task StartStopAndSkipCountdown_PassThroughToTheProcessManager()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);

            var start = await host.Instances.StartAsync(created.Value, ct);
            var stop = await host.Instances.StopAsync(created.Value, skipCountdown: true, ct);
            var skipped = await host.Instances.SkipCountdownAsync(created.Value, ct);

            Assert.True(start.Succeeded);
            Assert.True(stop.Succeeded);
            Assert.False(skipped);
            var launch = Assert.Single(host.ProcessManager.Starts);
            Assert.Equal((created.Value, LaunchKind.User, false), launch);
            var halt = Assert.Single(host.ProcessManager.Stops);
            Assert.Equal(created.Value, halt.InstanceId);
            Assert.True(halt.Options.SkipCountdown);
            Assert.False(halt.Options.RequireVerifiedExit);
        }
    }

    [Fact]
    public async Task StartMany_CollectsEveryOutcome_NamesTheRows_AndTurnsExceptionsIntoRejections()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var ok = await host.Instances.CreateAsync(Draft(mapId, "Ok"), ct);
            var busy = await host.Instances.CreateAsync(Draft(mapId, "Busy", 7779, 27021), ct);
            host.ProcessManager.StartOutcomes[busy.Value] = OperationOutcome.Rejected("Operation in progress.");
            var throwing = Substitute.For<IProcessManager>();
            throwing.StartAsync(Arg.Any<int>(), Arg.Any<LaunchKind>(), Arg.Any<CancellationToken>())
                .Returns<Task<OperationOutcome>>(call => call.ArgAt<int>(0) == 999
                    ? throw new InvalidOperationException("Instance 999 does not exist.")
                    : host.ProcessManager.StartAsync(call.ArgAt<int>(0), call.ArgAt<LaunchKind>(1), call.ArgAt<CancellationToken>(2)));
            var facade = new Server.Commands.InstanceCommands(
                host.Guard, host.Root, host.Root.Layout, host.Host, host.Settings, throwing, host.Locks, host.Journals, host.Backups, host.DeleteService, host.LayoutService,
                host.IniStore, host.GeneratedConfig, host.Rcon, host.Console, host.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Server.Commands.InstanceCommands>.Instance);

            var outcomes = await facade.StartManyAsync([ok.Value, busy.Value, 999], ct);

            Assert.Equal(3, outcomes.Count);
            Assert.Equal(("Ok", true, null), (outcomes[0].Name, outcomes[0].Outcome.Succeeded, outcomes[0].Outcome.Error));
            Assert.Equal(("Busy", false, "Operation in progress."), (outcomes[1].Name, outcomes[1].Outcome.Succeeded, outcomes[1].Outcome.Error));
            Assert.Equal(("#999", false, "Instance 999 does not exist."), (outcomes[2].Name, outcomes[2].Outcome.Succeeded, outcomes[2].Outcome.Error));
            Assert.Equal(InstanceState.Running, host.ProcessManager.GetRuntime(ok.Value).State);
            Assert.Equal(InstanceState.Stopped, host.ProcessManager.GetRuntime(busy.Value).State);
        }
    }

    [Fact]
    public async Task StopMany_StopsEachWithTheDefaultOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var one = await host.Instances.CreateAsync(Draft(mapId, "One"), ct);
            var two = await host.Instances.CreateAsync(Draft(mapId, "Two", 7779, 27021), ct);
            host.ProcessManager.Set(one.Value, InstanceState.Running);
            host.ProcessManager.Set(two.Value, InstanceState.Running);

            var outcomes = await host.Instances.StopManyAsync([one.Value, two.Value], ct);

            Assert.All(outcomes, o => Assert.True(o.Outcome.Succeeded));
            Assert.Equal([one.Value, two.Value], host.ProcessManager.Stops.Select(s => s.InstanceId));
            Assert.All(host.ProcessManager.Stops, s => Assert.Equal(new StopOptions(), s.Options));
        }
    }

    [Fact]
    public async Task BackupNow_ReturnsTheRecord_AndTurnsARefusalIntoAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);
            var record = new BackupRecord { InstanceId = created.Value, Outcome = BackupOutcome.Success, FileName = "manual.zip", IsManual = true };
            host.Backups.BackupNowAsync(created.Value, true, Arg.Any<CancellationToken>()).Returns(record);
            host.Backups.BackupNowAsync(999, true, Arg.Any<CancellationToken>()).Returns<BackupRecord>(_ => throw new InvalidOperationException("Instance 999 does not exist."));

            var ok = await host.Instances.BackupNowAsync(created.Value, ct);
            var refused = await host.Instances.BackupNowAsync(999, ct);

            Assert.True(ok.Succeeded);
            Assert.Same(record, ok.Value);
            Assert.False(refused.Succeeded);
            Assert.Equal("Instance 999 does not exist.", refused.Error);
        }
    }

    [Fact]
    public async Task Delete_RunsTheDeleteServiceWithTheKeepFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);
            host.DeleteService.DeleteAsync(created.Value, Arg.Any<InstanceDeleteOptions>(), Arg.Any<CancellationToken>()).Returns(OperationOutcome.Success);

            var options = new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false);
            var outcome = await host.Instances.DeleteAsync(created.Value, options, ct);

            Assert.True(outcome.Succeeded);
            await host.DeleteService.Received(1).DeleteAsync(created.Value, options, Arg.Any<CancellationToken>());
        }
    }

    // ---- console ------------------------------------------------------------------------------------

    [Fact]
    public async Task SendRcon_RefusesBlankCommands_StoppedInstances_AndMissingGeneratedSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);

            var blank = await host.Instances.SendRconAsync(created.Value, "   ", ct);
            var stopped = await host.Instances.SendRconAsync(created.Value, "saveworld", ct);
            host.ProcessManager.Set(created.Value, InstanceState.Running);
            var missing = await host.Instances.SendRconAsync(created.Value, "saveworld", ct);
            await host.WriteGeneratedSettingsAsync("my-island", password: null, rconPort: 27020, ct);
            var noPassword = await host.Instances.SendRconAsync(created.Value, "saveworld", ct);

            Assert.Equal("Type a command first.", blank.Error);
            Assert.Equal("The instance is not running, so there is nothing to send the command to.", stopped.Error);
            Assert.Equal("The generated GameUserSettings.ini is missing, so the RCON password is unknown.", missing.Error);
            Assert.StartsWith("ServerAdminPassword under [ServerSettings] is missing or empty", noPassword.Error, StringComparison.Ordinal);
            Assert.Empty(host.Rcon.Calls);
            Assert.Empty(host.Console.Snapshot(ConsoleChannels.Instance(created.Value)));
        }
    }

    [Fact]
    public async Task SendRcon_EchoesTheCommandAndEveryReplyLineIntoTheConsole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);
            host.ProcessManager.Set(created.Value, InstanceState.Running);
            await host.WriteGeneratedSettingsAsync("my-island", "hunter2", 27020, ct);
            host.Rcon.Replies["ListPlayers"] = "0. One, 0002aaaa\r\n1. Two, 0002bbbb\r\n";
            host.Rcon.Replies["broadcast hi"] = string.Empty;

            var listed = await host.Instances.SendRconAsync(created.Value, "  ListPlayers ", ct);
            var silent = await host.Instances.SendRconAsync(created.Value, "broadcast hi", ct);

            Assert.True(listed.Succeeded);
            Assert.Equal("0. One, 0002aaaa\r\n1. Two, 0002bbbb", listed.Value);
            Assert.Equal("(no reply)", silent.Value);
            var call = host.Rcon.Calls[0];
            Assert.Equal(new RconEndpoint(27020, "hunter2"), call.Endpoint);
            Assert.Equal("ListPlayers", call.Command);
            Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);

            var lines = host.Console.Snapshot(ConsoleChannels.Instance(created.Value));
            Assert.Equal(
                [("> ListPlayers", ConsoleLineKind.Info), ("0. One, 0002aaaa", ConsoleLineKind.Output), ("1. Two, 0002bbbb", ConsoleLineKind.Output), ("> broadcast hi", ConsoleLineKind.Info), ("(no reply)", ConsoleLineKind.Output)],
                lines.Select(l => (l.Text, l.Kind)));
            Assert.All(lines, l => Assert.Equal(CommandTestHost.Now, l.At));
        }
    }

    [Fact]
    public async Task SendRcon_ReportsAnRconFailureInTheResultAndTheConsole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);
            host.ProcessManager.Set(created.Value, InstanceState.Running);
            await host.WriteGeneratedSettingsAsync("my-island", "hunter2", 27020, ct);
            host.Rcon.FailuresByPort[27020] = new RconException(RconFailure.Connect, "Connection refused.");

            var result = await host.Instances.SendRconAsync(created.Value, "saveworld", ct);

            Assert.False(result.Succeeded);
            Assert.Equal("RCON connect failure: Connection refused.", result.Error);
            var lines = host.Console.Snapshot(ConsoleChannels.Instance(created.Value));
            Assert.Equal(2, lines.Count);
            Assert.Equal(("RCON Connect: Connection refused.", ConsoleLineKind.Error), (lines[1].Text, lines[1].Kind));
        }
    }

    // ---- ports --------------------------------------------------------------------------------------

    [Fact]
    public async Task SuggestPorts_StepsPastEveryDefinedInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var fresh = await host.Instances.SuggestPortsAsync(ct);
            Assert.True((await host.Instances.CreateAsync(Draft(mapId, "One", fresh.GamePort, fresh.RconPort), ct)).Succeeded);
            var next = await host.Instances.SuggestPortsAsync(ct);

            Assert.Equal(new PortSuggestion(7777, 27020), fresh);
            Assert.Equal(new PortSuggestion(7779, 27021), next);
        }
    }

    [Fact]
    public async Task CheckPorts_FlagsOtherInstancesAndTheWebPort_ButNotTheInstanceItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            Assert.True((await host.Instances.CreateAsync(Draft(mapId, "One"), ct)).Succeeded);

            var self = await host.Instances.CheckPortsAsync("One", 7777, 27020, ct);
            var clash = await host.Instances.CheckPortsAsync("Two", 7778, 27020, ct);
            var web = await host.Instances.CheckPortsAsync("Two", 7790, CommandTestHost.WebPort, ct);
            var free = await host.Instances.CheckPortsAsync("Two", 7790, 27030, ct);

            Assert.Empty(self);
            Assert.Equal(2, clash.Count);
            Assert.All(clash, c => Assert.Contains("One", c.Reason, StringComparison.Ordinal));
            Assert.Single(web);
            Assert.Equal(CommandTestHost.WebPort, web[0].Port);
            Assert.Empty(free);
        }
    }

    // ---- edit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_UpdatesTheEditableFields_AndNormalizesTheWhitelist()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);

            var result = await host.Instances.SaveAsync(created.Value, new InstanceEdit(" Renamed ", " New session ", 50, 7800, 27050, " a \r\n\r\n b\n", 15, 3), ct);

            Assert.True(result.Succeeded, result.Error);
            var instance = await host.InstanceAsync(created.Value, ct);
            Assert.Equal("Renamed", instance.Name);
            Assert.Equal("my-island", instance.Slug);
            Assert.Equal("New session", instance.SessionName);
            Assert.Equal((50, 7800, 27050), (instance.MaxPlayers, instance.GamePort, instance.RconPort));
            Assert.Equal("a\r\nb", instance.AdminWhitelist);
            Assert.Equal((15, 3), (instance.BackupIntervalMinutes, instance.BackupRetention));
        }
    }

    [Fact]
    public async Task Save_RefusesAnotherInstancesNameOrPorts_ButAllowsKeepingItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var one = await host.Instances.CreateAsync(Draft(mapId, "One"), ct);
            var two = await host.Instances.CreateAsync(Draft(mapId, "Two", 7779, 27021), ct);

            var keep = await host.Instances.SaveAsync(two.Value, new InstanceEdit("Two", "Two session", 20, 7779, 27021, string.Empty, null, null), ct);
            var stealName = await host.Instances.SaveAsync(two.Value, new InstanceEdit("ONE", "Two session", 20, 7779, 27021, string.Empty, null, null), ct);
            var stealPorts = await host.Instances.SaveAsync(two.Value, new InstanceEdit("Two", "Two session", 20, 7777, 27020, string.Empty, null, null), ct);
            var gone = await host.Instances.SaveAsync(999, new InstanceEdit("X", "X", 20, 7790, 27030, string.Empty, null, null), ct);

            Assert.True(keep.Succeeded, keep.Error);
            Assert.Equal("An instance named 'ONE' already exists.", stealName.Error);
            Assert.False(stealPorts.Succeeded);
            Assert.Contains(stealPorts.Errors, e => e.Contains("7777", StringComparison.Ordinal) && e.Contains("One", StringComparison.Ordinal));
            Assert.Contains(stealPorts.Errors, e => e.Contains("27020", StringComparison.Ordinal));
            Assert.Equal("The instance no longer exists.", gone.Error);
            Assert.Equal("Two", (await host.InstanceAsync(two.Value, ct)).Name);
            Assert.Equal("One", (await host.InstanceAsync(one.Value, ct)).Name);
        }
    }

    [Fact]
    public async Task SaveLaunchFlags_ValidatesBeforeTouchingTheRow_AndTrimsTypedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Instances.CreateAsync(Draft(mapId), ct);

            var rejected = await host.Instances.SaveLaunchFlagsAsync(created.Value, new LaunchFlags { AdditionalArgs = "-clusterid=x", ServerPlatform = "PC?XSX" }, ct);
            var accepted = await host.Instances.SaveLaunchFlagsAsync(created.Value, new LaunchFlags { NoBattlEye = false, ServerPlatform = " PC+XSX ", ActiveEvent = "  ", AdditionalArgs = "-NoTransferFromFiltering -crossplay" }, ct);
            var gone = await host.Instances.SaveLaunchFlagsAsync(999, new LaunchFlags(), ct);

            Assert.False(rejected.Succeeded);
            Assert.Equal(2, rejected.Errors.Count);
            Assert.True(accepted.Succeeded, accepted.Error);
            Assert.Equal("The instance no longer exists.", gone.Error);
            var flags = (await host.InstanceAsync(created.Value, ct)).LaunchFlags;
            Assert.False(flags.NoBattlEye);
            Assert.Equal("PC+XSX", flags.ServerPlatform);
            Assert.Null(flags.ActiveEvent);
            Assert.Equal("-NoTransferFromFiltering -crossplay", flags.AdditionalArgs);
        }
    }

    [Fact]
    public async Task SetMods_ReplacesTheListInOrder_DropsDuplicates_AndRefusesUnknownMods()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            foreach (var id in new[] { 1, 2, 3 })
            {
                await host.AddLibraryModAsync(id, $"Mod {id}", ct);
            }

            var created = await host.Instances.CreateAsync(Draft(mapId) with { Mods = [1] }, ct);

            var replaced = await host.Instances.SetModsAsync(created.Value, [3, 2, 3], ct);
            var unknown = await host.Instances.SetModsAsync(created.Value, [2, 404], ct);
            var cleared = await host.Instances.SetModsAsync(created.Value, [], ct);

            Assert.True(replaced.Succeeded, replaced.Error);
            Assert.Equal("One of the chosen mods is no longer in the library.", unknown.Error);
            Assert.True(cleared.Succeeded);
            await using var db = host.Db();
            Assert.Empty(await db.InstanceMods.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task MapMod_LoadsFirst_ShowsInTheDetail_AndIsRefusedInTheLists()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        await host.AddLibraryModAsync(2, "Two", ct);
        var map = await host.Maps.SaveAsync(new Map { Key = "Custom_WP", Name = "Custom", ModId = 777 }, ct);
        Assert.True(map.Succeeded, map.Error);

        var withMapMod = await host.Instances.CreateAsync(new InstanceDraft { Name = "Bad", MapId = map.Value!.Id, SessionName = "b", GamePort = 7777, RconPort = 27020, Mods = [777] }, ct);
        var created = await host.Instances.CreateAsync(new InstanceDraft { Name = "One", MapId = map.Value.Id, SessionName = "1", GamePort = 7777, RconPort = 27020, Mods = [2] }, ct);
        Assert.True(created.Succeeded, created.Error);
        var refused = await host.Instances.SetModsAsync(created.Value, [777, 2], ct);
        var cluster = await host.Clusters.CreateAsync("Main", ConfigSourceKind.GameDefaults, null, ct);
        var clusterRefused = await host.Clusters.SetModsAsync(cluster.Value, [777], ct);
        var detail = await host.Instances.GetAsync(created.Value, ct);
        var preview = await host.Instances.PreviewLaunchAsync(created.Value, ct);

        Assert.Contains("Map mods load automatically with their map and cannot be listed here: 777 (Custom).", withMapMod.Errors);
        Assert.Equal("Map mods load automatically with their map and cannot be listed here: 777 (Custom).", refused.Error);
        Assert.Equal("Map mods load automatically with their map and cannot be listed here: 777 (Custom).", clusterRefused.Error);
        Assert.Equal(777, detail!.MapMod?.Id);
        Assert.Equal([2], detail.InstanceMods.Select(m => m.Mod.Id));
        Assert.Contains("-mods=777,2", preview.Value!.CommandLine);
    }

    [Fact]
    public async Task SetMods_KeepsTheOrderItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            foreach (var id in new[] { 1, 2, 3 })
            {
                await host.AddLibraryModAsync(id, $"Mod {id}", ct);
            }

            var created = await host.Instances.CreateAsync(Draft(mapId), ct);

            Assert.True((await host.Instances.SetModsAsync(created.Value, [3, 1, 2], ct)).Succeeded);

            var instance = await host.InstanceAsync(created.Value, ct);
            Assert.Equal([3, 1, 2], instance.Mods.OrderBy(m => m.Order).Select(m => m.ModId));
        }
    }

    // ---- preview ------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewLaunch_FlagsTheMissingAdminPassword_ThenClearsOnceItIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            await host.AddLibraryModAsync(7, "Seven", ct);
            var created = await host.Instances.CreateAsync(Draft(mapId) with { Mods = [7], LaunchFlags = new LaunchFlags { AdditionalArgs = "-crossplay" } }, ct);

            var before = await host.Instances.PreviewLaunchAsync(created.Value, ct);
            var owner = IniOwner.ForInstance(created.Value);
            var current = await host.IniStore.LoadAsync(owner, IniFile.GameUserSettings, ct);
            var withPassword = current.Text.Replace("ServerAdminPassword=\r\n", "ServerAdminPassword=hunter2\r\nSessionName=ignored\r\n", StringComparison.Ordinal);
            Assert.True((await host.IniStore.SaveAsync(owner, IniFile.GameUserSettings, withPassword, current.Sha256, ct)).Succeeded);
            var after = await host.Instances.PreviewLaunchAsync(created.Value, ct);

            Assert.True(before.Succeeded);
            Assert.Contains("ServerAdminPassword", before.Value!.Problem, StringComparison.Ordinal);
            Assert.Contains("TheIsland_WP?listen?AltSaveDirectoryName=my-island", before.Value.CommandLine, StringComparison.Ordinal);
            Assert.Contains("-port=7777", before.Value.CommandLine, StringComparison.Ordinal);
            Assert.Contains("-mods=7", before.Value.CommandLine, StringComparison.Ordinal);
            Assert.Contains("-crossplay", before.Value.CommandLine, StringComparison.Ordinal);

            Assert.Null(after.Value!.Problem);
            Assert.Contains(after.Value.Warnings, w => w.Contains("SessionName", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task PreviewLaunch_ForAClusteredInstance_UsesTheClusterSourceAndClusterOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            await host.AddLibraryModAsync(1, "Cluster mod", ct);
            await host.AddLibraryModAsync(2, "Instance mod", ct);
            var cluster = await host.Clusters.CreateAsync("Survivors", ConfigSourceKind.Blank, null, ct);
            Assert.True((await host.Clusters.SetModsAsync(cluster.Value, [1], ct)).Succeeded);
            var owner = IniOwner.ForCluster(cluster.Value);
            var current = await host.IniStore.LoadAsync(owner, IniFile.GameUserSettings, ct);
            Assert.True((await host.IniStore.SaveAsync(owner, IniFile.GameUserSettings, "[ServerSettings]\r\nServerAdminPassword=secret\r\n", current.Sha256, ct)).Succeeded);
            var created = await host.Instances.CreateAsync(Draft(mapId) with { ClusterId = cluster.Value, Mods = [2] }, ct);

            var preview = await host.Instances.PreviewLaunchAsync(created.Value, ct);
            var gone = await host.Instances.PreviewLaunchAsync(999, ct);

            Assert.True(preview.Succeeded);
            Assert.Null(preview.Value!.Problem);
            Assert.Contains("-clusterid=survivors", preview.Value.CommandLine, StringComparison.Ordinal);
            Assert.Contains(host.Root.Layout.ClusterDirectory("survivors"), preview.Value.CommandLine, StringComparison.Ordinal);
            Assert.Contains("-mods=1,2", preview.Value.CommandLine, StringComparison.Ordinal);
            Assert.Equal("The instance no longer exists.", gone.Error);
        }
    }
}
