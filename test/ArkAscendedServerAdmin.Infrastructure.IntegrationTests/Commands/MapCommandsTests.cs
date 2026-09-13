using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class MapCommandsTests
{
    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maps.ListAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maps.GetUsageAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maps.SaveAsync(new Map { Key = "X", Name = "X" }, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Maps.DeleteAsync(1, ct));
    }

    [Fact]
    public async Task List_OrdersStoryThenNonCanonThenCustom_ByReleaseDateThenName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        Assert.True((await host.Maps.SaveAsync(new Map { Key = "Zeta_WP", Name = "Zeta", ModId = 9001 }, ct)).Succeeded);
        Assert.True((await host.Maps.SaveAsync(new Map { Key = "Aardvark_WP", Name = "Aardvark", ModId = 9002 }, ct)).Succeeded);
        Assert.True((await host.Maps.SaveAsync(new Map { Key = "Dated_WP", Name = "Dated", ReleaseDate = new DateOnly(2025, 1, 1), ModId = 9003 }, ct)).Succeeded);

        var maps = await host.Maps.ListAsync(ct);

        var story = maps.TakeWhile(m => m is { IsOfficial: true, IsStory: true }).Select(m => m.Key).ToList();
        var nonCanon = maps.Skip(story.Count).TakeWhile(m => m is { IsOfficial: true, IsStory: false }).Select(m => m.Key).ToList();
        var custom = maps.Skip(story.Count + nonCanon.Count).ToList();
        Assert.Equal(["TheIsland_WP", "ScorchedEarth_WP", "Aberration_WP", "Extinction_WP", "LostColony_WP", "Genesis_WP"], story);
        Assert.Equal(["TheCenter_WP", "BobsMissions_WP", "Astraeos_WP", "Ragnarok_WP", "Valguero_WP"], nonCanon);
        Assert.All(custom, m => Assert.False(m.IsOfficial));
        Assert.Equal(["Dated", "Aardvark", "Zeta"], custom.Select(m => m.Name));
    }

    [Fact]
    public async Task Save_RefusesStoryOnACustomMap_AndDropsStoryWhenOfficialIsCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var refused = await host.Maps.SaveAsync(new Map { Key = "Story_WP", Name = "Story", IsStory = true, ModId = 9004 }, ct);
        Assert.Contains("Only an official map can be a story map.", refused.Errors);

        var official = await host.Maps.SaveAsync(new Map { Key = "Mine_WP", Name = "Mine", IsOfficial = true, IsStory = true, ReleaseDate = new DateOnly(2026, 1, 2) }, ct);
        Assert.True(official.Succeeded, official.Error);
        Assert.Equal((true, true, new DateOnly(2026, 1, 2)), (official.Value!.IsOfficial, official.Value.IsStory, official.Value.ReleaseDate));

        var cleared = await host.Maps.SaveAsync(new Map { Id = official.Value.Id, Key = "Mine_WP", Name = "Mine", IsOfficial = false, IsStory = false, ModId = 9005 }, ct);
        Assert.True(cleared.Succeeded, cleared.Error);
        var row = (await host.Maps.ListAsync(ct)).Single(m => m.Id == official.Value.Id);
        Assert.Equal((false, false, (DateOnly?)null, 9005), (row.IsOfficial, row.IsStory, row.ReleaseDate, row.ModId));
    }

    [Fact]
    public async Task Save_CustomMapNeedsItsModId_AndPutsTheModInTheLibrary()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var missing = await host.Maps.SaveAsync(new Map { Key = "Custom_WP", Name = "Custom" }, ct);
        var officialWithMod = await host.Maps.SaveAsync(new Map { Key = "Off_WP", Name = "Off", IsOfficial = true, ModId = 5 }, ct);
        var saved = await host.Maps.SaveAsync(new Map { Key = "Custom_WP", Name = "Custom", ModId = 4242 }, ct);

        Assert.Equal("A custom map needs the CurseForge project id of the mod that ships it.", missing.Error);
        Assert.Equal("An official map has no map mod; clear the mod id or untick Official.", officialWithMod.Error);
        Assert.True(saved.Succeeded, saved.Error);
        Assert.Equal(4242, saved.Value!.ModId);
        // No API key in the test host, so the entry is a manual one named after the map.
        var entry = Assert.Single(await host.Mods.ListLibraryAsync(ct), m => m.Id == 4242);
        Assert.Equal("Custom", entry.Name);
        Assert.Equal(["Custom"], (await host.Mods.GetUsageAsync(ct))[4242].Maps);
        Assert.Equal(new Dictionary<int, string> { [4242] = "Custom" }, await host.Mods.GetMapModsAsync(ct));
        Assert.Equal("Remove it from map Custom first.", (await host.Mods.RemoveAsync(4242, ct)).Error);
    }

    [Theory]
    [InlineData("", "Name", "Map key is required")]
    [InlineData("Has Space", "Name", "Map key must not contain spaces.")]
    [InlineData("Key?WP", "Name", "Map key must not contain '?'.")]
    [InlineData("Key_WP", "", "Map name is required.")]
    public async Task Save_ValidatesKeyAndName(string key, string name, string expectedPrefix)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var result = await host.Maps.SaveAsync(new Map { Key = key, Name = name }, ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_RefusesLongValuesAndDuplicateKeys_CaseInsensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var longKey = await host.Maps.SaveAsync(new Map { Key = new string('k', 101), Name = "N", ModId = 9006 }, ct);
        var longName = await host.Maps.SaveAsync(new Map { Key = "Fine_WP", Name = new string('n', 101), ModId = 9006 }, ct);
        var duplicate = await host.Maps.SaveAsync(new Map { Key = "theisland_wp", Name = "Dupe", ModId = 9006 }, ct);

        Assert.Equal("Map key must be 100 characters or fewer.", longKey.Error);
        Assert.Equal("Map name must be 100 characters or fewer.", longName.Error);
        Assert.Equal("A map with key 'theisland_wp' already exists.", duplicate.Error);
    }

    [Fact]
    public async Task Save_InsertsAsCustom_ThenUpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);

        var inserted = await host.Maps.SaveAsync(new Map { Key = " Custom_WP ", Name = " Custom ", ModId = 9007 }, ct);
        Assert.True(inserted.Succeeded, inserted.Error);
        Assert.Equal(("Custom_WP", "Custom", false), (inserted.Value!.Key, inserted.Value.Name, inserted.Value.IsOfficial));

        var updated = await host.Maps.SaveAsync(new Map { Id = inserted.Value.Id, Key = "Custom2_WP", Name = "Custom Two", ModId = 9007 }, ct);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Maps.SaveAsync(new Map { Id = 9999, Key = "Ghost_WP", Name = "Ghost", ModId = 9007 }, ct));

        Assert.True(updated.Succeeded, updated.Error);
        Assert.Equal(inserted.Value.Id, updated.Value!.Id);
        Assert.Equal("The map was deleted while you were editing it.", missing.Message);
        var row = (await host.Maps.ListAsync(ct)).Single(m => m.Id == inserted.Value.Id);
        Assert.Equal(("Custom2_WP", "Custom Two"), (row.Key, row.Name));
    }

    [Fact]
    public async Task Usage_CountsInstancesPerMap_AndDeleteIsRefusedWhileUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await host.MapIdAsync(ct);
        var custom = (await host.Maps.SaveAsync(new Map { Key = "Custom_WP", Name = "Custom", ModId = 9008 }, ct)).Value!;
        Assert.True((await host.Instances.CreateAsync(new InstanceDraft { Name = "One", MapId = island, SessionName = "1", GamePort = 7777, RconPort = 27020 }, ct)).Succeeded);
        Assert.True((await host.Instances.CreateAsync(new InstanceDraft { Name = "Two", MapId = island, SessionName = "2", GamePort = 7779, RconPort = 27021 }, ct)).Succeeded);

        var usage = await host.Maps.GetUsageAsync(ct);
        var refused = await host.Maps.DeleteAsync(island, ct);
        var deleted = await host.Maps.DeleteAsync(custom.Id, ct);
        var again = await host.Maps.DeleteAsync(custom.Id, ct);

        Assert.Equal(2, usage[island]);
        Assert.False(usage.ContainsKey(custom.Id));
        Assert.Equal("Instances still use this map: One, Two.", refused.Error);
        Assert.True(deleted.Succeeded);
        Assert.True(again.Succeeded);
        Assert.DoesNotContain(await host.Maps.ListAsync(ct), m => m.Id == custom.Id);
    }
}
