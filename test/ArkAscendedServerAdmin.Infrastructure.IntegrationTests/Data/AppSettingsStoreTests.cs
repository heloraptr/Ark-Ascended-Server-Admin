using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Infrastructure.Data;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Data;

public class AppSettingsStoreTests
{
    [Fact]
    public async Task Get_ReturnsSeededDefaults()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);

        Assert.Equal(new AppSettings(), await store.GetAsync(ct));
    }

    [Fact]
    public async Task Save_PersistsAndIsVisibleToAFreshStore()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);
        var updated = new AppSettings { StaggerDelaySeconds = 5, CurseForgeApiKey = "key-123", SteamCmdValidate = true };

        await store.SaveAsync(updated, ct);

        var expected = updated with { Version = 1 };
        Assert.Equal(expected, await store.GetAsync(ct));
        Assert.Equal(expected, await new AppSettingsStore(root).GetAsync(ct));
    }

    [Fact]
    public async Task Save_RejectsInvalidSettingsWithoutWriting()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppSettings { GamePortStart = -1 }, ct));

        Assert.Equal(new AppSettings(), await new AppSettingsStore(root).GetAsync(ct));
    }

    [Fact]
    public async Task Save_WithAStaleVersion_IsRefusedWithoutWriting()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);
        var loadedByTabA = await store.GetAsync(ct);
        await store.SaveAsync(loadedByTabA with { StaggerDelaySeconds = 5 }, ct);

        var conflict = await Assert.ThrowsAsync<AppSettingsConflictException>(() => store.SaveAsync(loadedByTabA with { StaggerDelaySeconds = 9 }, ct));

        Assert.Equal((0L, 1L), (conflict.ExpectedVersion, conflict.ActualVersion));
        Assert.Equal(5, (await new AppSettingsStore(root).GetAsync(ct)).StaggerDelaySeconds);
    }

    [Fact]
    public async Task Save_OfAnUnchangedSnapshot_DoesNotBumpTheVersion()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);
        var loaded = await store.GetAsync(ct);

        await store.SaveAsync(loaded, ct);
        await store.SaveAsync(loaded, ct);

        Assert.Equal(0, (await new AppSettingsStore(root).GetAsync(ct)).Version);
    }

    [Fact]
    public async Task Update_WritesOnlyWhenTheMutationChangesSomething_AndSerializesWriters()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);

        var unchanged = await store.UpdateAsync(s => s, ct);
        var changed = await store.UpdateAsync(s => s with { CurseForgeApiKey = "key" }, ct);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.UpdateAsync(s => s with { StaggerDelaySeconds = s.StaggerDelaySeconds + 1 }, ct)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(s => s with { GamePortStart = -1 }, ct));

        Assert.Equal(0, unchanged.Version);
        Assert.Equal(("key", 1L), (changed.CurseForgeApiKey, changed.Version));
        var final = await new AppSettingsStore(root).GetAsync(ct);
        Assert.Equal((40, 11L, "key"), (final.StaggerDelaySeconds, final.Version, final.CurseForgeApiKey));
    }

    [Fact]
    public async Task Update_IgnoresAVersionSetByTheMutation()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);

        var result = await store.UpdateAsync(s => s with { Version = 99, SteamCmdValidate = true }, ct);

        Assert.Equal(1, result.Version);
    }
}
