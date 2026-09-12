using System.Net;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;
using ArkAscendedServerAdmin.Server.Commands;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class ModCommandsTests
{
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static Mod ApiMod(int id, string name, string summary = "About it", string thumbnail = "https://cdn/thumb.png", string author = "Author") =>
        new()
        {
            Id = id,
            Name = name,
            Summary = summary,
            Logo = new ModAsset { ThumbnailUrl = thumbnail },
            Authors = [new Author { Name = author }],
            DateModified = Modified,
            DownloadCount = 1234,
        };

    private static async Task<CommandTestHost> StartAsync(CancellationToken ct, bool withApiKey)
    {
        var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        if (withApiKey)
        {
            await host.Settings.SaveAsync((await host.Settings.GetAsync(ct)) with { CurseForgeApiKey = "cf-key" }, ct);
        }

        return host;
    }

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.ListLibraryAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.GetUsageAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.IsApiKeyConfiguredAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.SearchAsync("x", ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.AddAsync(1, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.AddManualAsync(1, "x", ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.RefreshMetadataAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Mods.RemoveAsync(1, ct));
        await host.CurseForge.DidNotReceiveWithAnyArgs().GetModAsync(default, ct);
    }

    [Fact]
    public async Task WithoutAnApiKey_SearchAddAndRefreshExplainWhy_ButManualAddWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: false);

        Assert.False(await host.Mods.IsApiKeyConfiguredAsync(ct));
        Assert.Equal(ModCommands.NoApiKeyMessage, (await host.Mods.SearchAsync("dino", ct)).Error);
        Assert.Equal(ModCommands.NoApiKeyMessage, (await host.Mods.AddAsync(42, ct)).Error);
        Assert.Equal(ModCommands.NoApiKeyMessage, (await host.Mods.RefreshMetadataAsync(ct)).Error);
        await host.CurseForge.DidNotReceiveWithAnyArgs().SearchModsAsync(default!, default, ct);

        var manual = await host.Mods.AddManualAsync(42, "  Typed Name ", ct);

        Assert.True(manual.Succeeded, manual.Error);
        Assert.Equal((42, "Typed Name", CommandTestHost.Now), (manual.Value!.Id, manual.Value.Name, manual.Value.AddedAt));
        Assert.Null(manual.Value.Summary);
        Assert.Null(manual.Value.DateModified);
        Assert.Equal("Typed Name", Assert.Single(await host.Mods.ListLibraryAsync(ct)).Name);
    }

    [Fact]
    public async Task AddManual_ValidatesIdAndName_AndUpdatesAnExistingEntryWithoutLosingMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: false);
        await using (var db = host.Db())
        {
            db.ModLibrary.Add(new Domain.ModLibraryEntry { Id = 7, Name = "Old", Summary = "Kept", ThumbnailUrl = "https://kept", AddedAt = DateTimeOffset.UnixEpoch });
            await db.SaveChangesAsync(ct);
        }

        var invalid = await host.Mods.AddManualAsync(0, "  ", ct);
        var renamed = await host.Mods.AddManualAsync(7, "New", ct);

        Assert.Equal(2, invalid.Errors.Count);
        Assert.True(renamed.Succeeded, renamed.Error);
        var entry = Assert.Single(await host.Mods.ListLibraryAsync(ct));
        Assert.Equal(("New", "Kept", "https://kept", DateTimeOffset.UnixEpoch), (entry.Name, entry.Summary, entry.ThumbnailUrl, entry.AddedAt));
    }

    [Fact]
    public async Task Search_MarksHitsAlreadyInTheLibrary_AndSkipsTheApiForABlankTerm()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);
        await host.AddLibraryModAsync(2, "Already here", ct);
        host.CurseForge.SearchModsAsync("dino", Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([ApiMod(1, "One", thumbnail: string.Empty), ApiMod(2, "Two")]);

        var blank = await host.Mods.SearchAsync("   ", ct);
        var hits = await host.Mods.SearchAsync(" dino ", ct);

        Assert.True(blank.Succeeded);
        Assert.Empty(blank.Value!);
        Assert.True(hits.Succeeded, hits.Error);
        Assert.Equal(2, hits.Value!.Count);
        Assert.Equal(new ModSearchHit(1, "One", "About it", null, "Author", new DateTimeOffset(Modified), 1234, false), hits.Value[0]);
        Assert.Equal(new ModSearchHit(2, "Two", "About it", "https://cdn/thumb.png", "Author", new DateTimeOffset(Modified), 1234, true), hits.Value[1]);
        await host.CurseForge.Received(1).SearchModsAsync("dino", Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_DescribesApiFailures_DistinguishingARejectedKey()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);
        host.CurseForge.SearchModsAsync("forbidden", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<Mod>>>(_ => throw new HttpRequestException("403", null, HttpStatusCode.Forbidden));
        host.CurseForge.SearchModsAsync("down", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<Mod>>>(_ => throw new HttpRequestException("No such host is known."));

        var forbidden = await host.Mods.SearchAsync("forbidden", ct);
        var down = await host.Mods.SearchAsync("down", ct);

        Assert.Equal("CurseForge rejected the API key. Check it on the Settings page.", forbidden.Error);
        Assert.Equal("CurseForge could not be reached: No such host is known.", down.Error);
    }

    [Fact]
    public async Task Add_FetchesMetadataThroughTheApi_AndRejectsNonPositiveIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);
        host.CurseForge.GetModAsync(42, Arg.Any<CancellationToken>()).Returns(ApiMod(42, "Fetched", "Summary"));
        host.CurseForge.GetModAsync(43, Arg.Any<CancellationToken>()).Returns<Task<Mod>>(_ => throw new HttpRequestException("timeout"));

        var invalid = await host.Mods.AddAsync(-1, ct);
        var added = await host.Mods.AddAsync(42, ct);
        var failed = await host.Mods.AddAsync(43, ct);

        Assert.Equal("A CurseForge mod id is a positive number.", invalid.Error);
        Assert.True(added.Succeeded, added.Error);
        Assert.Equal(("Fetched", "Summary", "https://cdn/thumb.png", new DateTimeOffset(Modified), CommandTestHost.Now), (added.Value!.Name, added.Value.Summary, added.Value.ThumbnailUrl, added.Value.DateModified, added.Value.AddedAt));
        Assert.Equal("CurseForge could not be reached: timeout", failed.Error);
        Assert.Single(await host.Mods.ListLibraryAsync(ct));
    }

    [Fact]
    public async Task RefreshMetadata_CountsOnlyEntriesThatChanged_AndIgnoresUnknownIdsInTheReply()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);
        await using (var db = host.Db())
        {
            db.ModLibrary.Add(new Domain.ModLibraryEntry { Id = 1, Name = "Same", Summary = "About it", ThumbnailUrl = "https://cdn/thumb.png", DateModified = new DateTimeOffset(Modified), AddedAt = CommandTestHost.Now });
            db.ModLibrary.Add(new Domain.ModLibraryEntry { Id = 2, Name = "Old name", AddedAt = CommandTestHost.Now });
            await db.SaveChangesAsync(ct);
        }

        host.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>())
            .Returns([ApiMod(1, "Same"), ApiMod(2, "New name"), ApiMod(3, "Not in library")]);

        var result = await host.Mods.RefreshMetadataAsync(ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.Value);
        var library = await host.Mods.ListLibraryAsync(ct);
        Assert.Equal(2, library.Count);
        var refreshed = library.Single(m => m.Id == 2);
        Assert.Equal(("New name", "About it", new DateTimeOffset(Modified)), (refreshed.Name, refreshed.Summary, refreshed.DateModified));
        await host.CurseForge.Received(1).GetModsAsync(Arg.Is<IEnumerable<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { 1, 2 })), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshMetadata_WithAnEmptyLibrary_DoesNotCallTheApi()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: true);

        var result = await host.Mods.RefreshMetadataAsync(ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value);
        await host.CurseForge.DidNotReceiveWithAnyArgs().GetModsAsync(default!, default, ct);
    }

    [Fact]
    public async Task Usage_ListsClustersAndInstances_AndRemoveIsRefusedWhileReferenced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await StartAsync(ct, withApiKey: false);
        await host.AddLibraryModAsync(1, "Shared", ct);
        await host.AddLibraryModAsync(2, "Loose", ct);
        var cluster = await host.Clusters.CreateAsync("Survivors", ConfigSourceKind.Blank, null, ct);
        Assert.True((await host.Clusters.SetModsAsync(cluster.Value, [1], ct)).Succeeded);
        var mapId = await host.MapIdAsync(ct);
        Assert.True((await host.Instances.CreateAsync(new InstanceDraft { Name = "Zed", MapId = mapId, SessionName = "z", GamePort = 7777, RconPort = 27020, ModIds = [1] }, ct)).Succeeded);
        Assert.True((await host.Instances.CreateAsync(new InstanceDraft { Name = "Able", MapId = mapId, SessionName = "a", GamePort = 7779, RconPort = 27021, ModIds = [1] }, ct)).Succeeded);

        var usage = await host.Mods.GetUsageAsync(ct);
        var refused = await host.Mods.RemoveAsync(1, ct);
        var removed = await host.Mods.RemoveAsync(2, ct);
        var unknown = await host.Mods.RemoveAsync(3, ct);

        Assert.Equal(["Survivors"], usage[1].Clusters);
        Assert.Equal(["Able", "Zed"], usage[1].Instances);
        Assert.True(usage[1].IsReferenced);
        Assert.False(usage.ContainsKey(2));
        Assert.Equal("Remove it from cluster Survivors, instance Able, instance Zed first.", refused.Error);
        Assert.True(removed.Succeeded);
        Assert.True(unknown.Succeeded);
        Assert.Equal([1], (await host.Mods.ListLibraryAsync(ct)).Select(m => m.Id));
    }
}
