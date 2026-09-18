using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class ClusterCommandsTests
{
    private static async Task<(CommandTestHost Host, int MapId)> StartAsync(CancellationToken ct)
    {
        var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        return (host, await host.MapIdAsync(ct));
    }

    private static InstanceDraft Member(int mapId, int clusterId, string name, int gamePort, int rconPort) =>
        new() { Name = name, MapId = mapId, ClusterId = clusterId, SessionName = name, GamePort = gamePort, RconPort = rconPort };

    /// <summary>B0: a restore reserves a cluster; creating a member or deleting the cluster is refused for as long as it holds.</summary>
    [Fact]
    public async Task ReservedCluster_RefusesNewMembersAndDeletion_UntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var cluster = await host.Clusters.CreateAsync("Held", ConfigSourceKind.Blank, null, ct);

            using (host.Locks.TryReserveCluster(cluster.Value))
            {
                var create = await host.Instances.CreateAsync(Member(mapId, cluster.Value, "Late", 7777, 27020), ct);
                var delete = await host.Clusters.DeleteAsync(cluster.Value, ct);

                Assert.Contains("reserved by a restore", create.Error, StringComparison.Ordinal);
                Assert.Contains("reserved by a restore", delete.Error, StringComparison.Ordinal);
            }

            Assert.True((await host.Instances.CreateAsync(Member(mapId, cluster.Value, "Late", 7777, 27020), ct)).Succeeded);
        }
    }

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            host.Guard.Deny = true;

            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.ListAsync(ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.GetAsync(1, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.CreateAsync("X", ConfigSourceKind.Blank, null, ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.SaveAsync(1, new ClusterEdit("X", "x", string.Empty), ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.SaveLaunchFlagsAsync(1, new LaunchFlags(), ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.SetModsAsync(1, [], ct));
            await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Clusters.DeleteAsync(1, ct));

            await using var db = host.Db();
            Assert.Empty(await db.Clusters.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task Create_WritesTheRow_TheDirectory_AndTheSeededIniSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var result = await host.Clusters.CreateAsync("  The Survivors ", ConfigSourceKind.GameDefaults, null, ct);

            Assert.True(result.Succeeded, result.Error);
            var cluster = await host.ClusterAsync(result.Value, ct);
            Assert.Equal("The Survivors", cluster.Name);
            Assert.Equal("the-survivors", cluster.Slug);
            Assert.Equal("the-survivors", cluster.ClusterKey);
            Assert.Equal(CommandTestHost.Now, cluster.CreatedAt);
            Assert.True(Directory.Exists(host.Root.Layout.ClusterDirectory("the-survivors")));

            var owner = IniOwner.ForCluster(result.Value);
            Assert.Equal(IniTemplates.DefaultGameIni, (await host.IniStore.LoadAsync(owner, IniFile.Game, ct)).Text);
            Assert.Equal(IniTemplates.DefaultGameUserSettings, (await host.IniStore.LoadAsync(owner, IniFile.GameUserSettings, ct)).Text);
            await using var db = host.Db();
            Assert.Equal(2, await db.IniDocuments.CountAsync(d => d.ClusterId == result.Value, ct));
        }
    }

    [Fact]
    public async Task Create_RefusesBlankAndDuplicateNames_CaseInsensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            Assert.True((await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct)).Succeeded);

            var blank = await host.Clusters.CreateAsync("  ", ConfigSourceKind.Blank, null, ct);
            var duplicate = await host.Clusters.CreateAsync("ALPHA", ConfigSourceKind.Blank, null, ct);
            var tooLong = await host.Clusters.CreateAsync(new string('a', 101), ConfigSourceKind.Blank, null, ct);

            Assert.Equal("Cluster name is required.", blank.Error);
            Assert.Equal("A cluster named 'ALPHA' already exists.", duplicate.Error);
            Assert.Equal("Cluster name must be 100 characters or fewer.", tooLong.Error);
        }
    }

    [Fact]
    public async Task Create_SkipsSlugsTakenByInstancesAndArchives()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            Assert.True((await host.Instances.CreateAsync(new InstanceDraft { Name = "Shared", MapId = mapId, SessionName = "s", GamePort = 7777, RconPort = 27020 }, ct)).Succeeded);
            Directory.CreateDirectory(Path.Combine(host.Root.Layout.Archive, "old-20260101-000000"));

            var shared = await host.Clusters.CreateAsync("Shared", ConfigSourceKind.Blank, null, ct);
            var old = await host.Clusters.CreateAsync("Old", ConfigSourceKind.Blank, null, ct);

            Assert.True(shared.Succeeded, shared.Error);
            Assert.True(old.Succeeded, old.Error);
            Assert.NotEqual("shared", (await host.ClusterAsync(shared.Value, ct)).Slug);
            Assert.NotEqual("old", (await host.ClusterAsync(old.Value, ct)).Slug);
        }
    }

    [Fact]
    public async Task Create_WhenTheCopySourceIsMissing_RemovesTheRowAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var result = await host.Clusters.CreateAsync("Copy", ConfigSourceKind.CopyFromInstance, 999, ct);

            Assert.False(result.Succeeded);
            Assert.StartsWith("The cluster folder could not be prepared:", result.Error, StringComparison.Ordinal);
            await using var db = host.Db();
            Assert.Empty(await db.Clusters.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task ListAndGet_CountMembersAndMods_AndOrderThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            await host.AddLibraryModAsync(1, "One", ct);
            await host.AddLibraryModAsync(2, "Two", ct);
            var zulu = await host.Clusters.CreateAsync("Zulu", ConfigSourceKind.Blank, null, ct);
            var alpha = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);
            Assert.True((await host.Clusters.SetModsAsync(alpha.Value, [2, 1], ct)).Succeeded);
            var bravo = await host.Instances.CreateAsync(Member(mapId, alpha.Value, "Bravo", 7777, 27020), ct);
            var able = await host.Instances.CreateAsync(Member(mapId, alpha.Value, "Able", 7779, 27021), ct);
            await using (var db = host.Db())
            {
                db.BackupRecords.Add(new BackupRecord { InstanceId = able.Value, Outcome = BackupOutcome.Success, FileName = "able.zip" });
                await db.SaveChangesAsync(ct);
            }

            var list = await host.Clusters.ListAsync(ct);
            var detail = await host.Clusters.GetAsync(alpha.Value, ct);

            Assert.Equal(["Alpha", "Zulu"], list.Select(c => c.Name));
            Assert.Equal((2, 2), (list[0].InstanceCount, list[0].ModCount));
            Assert.Equal((0, 0), (list[1].InstanceCount, list[1].ModCount));
            Assert.Equal(zulu.Value, list[1].Id);

            Assert.NotNull(detail);
            Assert.Equal([2, 1], detail.Mods.Select(m => m.Mod.Id));
            Assert.Equal(["Able", "Bravo"], detail.Instances.Select(i => i.Name));
            Assert.Equal("able.zip", detail.Instances[0].LastBackup?.FileName);
            Assert.Null(detail.Instances[1].LastBackup);
            Assert.Equal(bravo.Value, detail.Instances[1].Id);
            Assert.Null(await host.Clusters.GetAsync(999, ct));
        }
    }

    [Fact]
    public async Task Save_UpdatesNameKeyAndWhitelist()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);

            var result = await host.Clusters.SaveAsync(created.Value, new ClusterEdit(" Renamed ", " mykey ", "x\n\n y "), ct);

            Assert.True(result.Succeeded, result.Error);
            var cluster = await host.ClusterAsync(created.Value, ct);
            Assert.Equal("Renamed", cluster.Name);
            Assert.Equal("alpha", cluster.Slug);
            Assert.Equal("mykey", cluster.ClusterKey);
            Assert.Equal("x\r\ny", cluster.AdminWhitelist);
        }
    }

    [Theory]
    [InlineData("", "Cluster id is required")]
    [InlineData("   ", "Cluster id is required")]
    [InlineData("has space", "Cluster id must not contain spaces.")]
    [InlineData("a?b", "ClusterKey must not contain '?'.")]
    [InlineData("a=b", "ClusterKey must not contain '='.")]
    public async Task Save_ValidatesTheClusterKey(string key, string expectedPrefix)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);

            var result = await host.Clusters.SaveAsync(created.Value, new ClusterEdit("Alpha", key, string.Empty), ct);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.StartsWith(expectedPrefix, StringComparison.Ordinal));
            Assert.Equal("alpha", (await host.ClusterAsync(created.Value, ct)).ClusterKey);
        }
    }

    [Fact]
    public async Task Save_RefusesAKeyLongerThan64_AndAnotherClustersName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var alpha = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);
            var beta = await host.Clusters.CreateAsync("Beta", ConfigSourceKind.Blank, null, ct);

            var longKey = await host.Clusters.SaveAsync(alpha.Value, new ClusterEdit("Alpha", new string('k', 65), string.Empty), ct);
            var stolen = await host.Clusters.SaveAsync(beta.Value, new ClusterEdit("alpha", "beta", string.Empty), ct);
            var own = await host.Clusters.SaveAsync(beta.Value, new ClusterEdit("BETA", "beta", string.Empty), ct);
            var gone = await host.Clusters.SaveAsync(999, new ClusterEdit("X", "x", string.Empty), ct);

            Assert.Equal("Cluster id must be 64 characters or fewer.", longKey.Error);
            Assert.Equal("A cluster named 'alpha' already exists.", stolen.Error);
            Assert.True(own.Succeeded, own.Error);
            Assert.Equal("The cluster no longer exists.", gone.Error);
        }
    }

    [Fact]
    public async Task SaveLaunchFlags_ValidatesAndCopiesTheFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);

            var rejected = await host.Clusters.SaveLaunchFlagsAsync(created.Value, new LaunchFlags { AdditionalArgs = "-mods=1" }, ct);
            var accepted = await host.Clusters.SaveLaunchFlagsAsync(created.Value, new LaunchFlags { NoWildBabies = true, ServerPlatform = "ALL" }, ct);
            var gone = await host.Clusters.SaveLaunchFlagsAsync(999, new LaunchFlags(), ct);

            Assert.False(rejected.Succeeded);
            Assert.True(accepted.Succeeded, accepted.Error);
            Assert.Equal("The cluster no longer exists.", gone.Error);
            var flags = (await host.ClusterAsync(created.Value, ct)).LaunchFlags;
            Assert.True(flags.NoWildBabies);
            Assert.Equal("ALL", flags.ServerPlatform);
        }
    }

    [Fact]
    public async Task SetMods_ReplacesInOrder_AndRefusesUnknownMods()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            await host.AddLibraryModAsync(1, "One", ct);
            await host.AddLibraryModAsync(2, "Two", ct);
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);

            Assert.True((await host.Clusters.SetModsAsync(created.Value, [1], ct)).Succeeded);
            var replaced = await host.Clusters.SetModsAsync(created.Value, [2, 1, 2], ct);
            var unknown = await host.Clusters.SetModsAsync(created.Value, [3], ct);
            var gone = await host.Clusters.SetModsAsync(999, [1], ct);

            Assert.True(replaced.Succeeded, replaced.Error);
            Assert.Equal("One of the chosen mods is no longer in the library.", unknown.Error);
            Assert.Equal("The cluster no longer exists.", gone.Error);
            var cluster = await host.ClusterAsync(created.Value, ct);
            Assert.Equal([2, 1], cluster.Mods.OrderBy(m => m.Order).Select(m => m.ModId));
        }
    }

    [Fact]
    public async Task Delete_IsRefusedWhileInstancesBelongToTheCluster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.Blank, null, ct);
            Assert.True((await host.Instances.CreateAsync(Member(mapId, created.Value, "Member", 7777, 27020), ct)).Succeeded);

            var result = await host.Clusters.DeleteAsync(created.Value, ct);

            Assert.Equal("Move or delete its instances first: Member.", result.Error);
            Assert.NotNull(await host.Clusters.GetAsync(created.Value, ct));
        }
    }

    [Fact]
    public async Task Delete_RemovesTheRowItsModsAndItsMirrorRows_AndLeavesTheDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            await host.AddLibraryModAsync(1, "One", ct);
            var created = await host.Clusters.CreateAsync("Alpha", ConfigSourceKind.GameDefaults, null, ct);
            Assert.True((await host.Clusters.SetModsAsync(created.Value, [1], ct)).Succeeded);

            var result = await host.Clusters.DeleteAsync(created.Value, ct);
            var again = await host.Clusters.DeleteAsync(created.Value, ct);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("The cluster no longer exists.", again.Error);
            await using var db = host.Db();
            Assert.Empty(await db.Clusters.ToListAsync(ct));
            Assert.Empty(await db.ClusterMods.ToListAsync(ct));
            Assert.Empty(await db.IniDocuments.ToListAsync(ct));
            Assert.Single(await db.ModLibrary.ToListAsync(ct));
            Assert.True(Directory.Exists(host.Root.Layout.ClusterDirectory("alpha")));
        }
    }
}
