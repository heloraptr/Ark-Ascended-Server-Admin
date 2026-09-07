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

        Assert.Equal(updated, await store.GetAsync(ct));
        Assert.Equal(updated, await new AppSettingsStore(root).GetAsync(ct));
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
}
