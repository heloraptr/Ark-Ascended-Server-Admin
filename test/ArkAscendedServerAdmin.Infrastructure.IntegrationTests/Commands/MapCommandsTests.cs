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
    public async Task List_PutsOfficialMapsFirst_ThenByName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        Assert.True((await host.Maps.SaveAsync(new Map { Key = "Zeta_WP", Name = "Zeta" }, ct)).Succeeded);
        Assert.True((await host.Maps.SaveAsync(new Map { Key = "Aardvark_WP", Name = "Aardvark" }, ct)).Succeeded);

        var maps = await host.Maps.ListAsync(ct);

        var custom = maps.Where(m => !m.IsOfficial).ToList();
        Assert.Equal(["Aardvark", "Zeta"], custom.Select(m => m.Name));
        Assert.True(maps.TakeWhile(m => m.IsOfficial).Count() == maps.Count - 2, "official maps come before custom ones");
        Assert.Contains(maps, m => m.Key == "TheIsland_WP" && m.IsOfficial);
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

        var longKey = await host.Maps.SaveAsync(new Map { Key = new string('k', 101), Name = "N" }, ct);
        var longName = await host.Maps.SaveAsync(new Map { Key = "Fine_WP", Name = new string('n', 101) }, ct);
        var duplicate = await host.Maps.SaveAsync(new Map { Key = "theisland_wp", Name = "Dupe" }, ct);

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

        var inserted = await host.Maps.SaveAsync(new Map { Key = " Custom_WP ", Name = " Custom " }, ct);
        Assert.True(inserted.Succeeded, inserted.Error);
        Assert.Equal(("Custom_WP", "Custom", false), (inserted.Value!.Key, inserted.Value.Name, inserted.Value.IsOfficial));

        var updated = await host.Maps.SaveAsync(new Map { Id = inserted.Value.Id, Key = "Custom2_WP", Name = "Custom Two" }, ct);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Maps.SaveAsync(new Map { Id = 9999, Key = "Ghost_WP", Name = "Ghost" }, ct));

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
        var custom = (await host.Maps.SaveAsync(new Map { Key = "Custom_WP", Name = "Custom" }, ct)).Value!;
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
