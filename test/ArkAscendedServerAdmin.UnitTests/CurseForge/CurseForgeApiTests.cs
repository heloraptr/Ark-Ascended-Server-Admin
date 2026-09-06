using ArkAscendedServerAdmin.CurseForge;

namespace ArkAscendedServerAdmin.UnitTests.CurseForge;

public class CurseForgeApiTests
{
    private const int GameId = 83374;

    [Fact]
    public async Task GetModAsync_ReturnsDeserializedMod()
    {
        var handler = new StubHttpMessageHandler()
            .Respond("/v1/mods/929713", """{"data":{"id":929713,"name":"Awesome Spyglass","logo":{"thumbnailUrl":"https://img/thumb.png"}}}""");
        var api = CreateApi(handler);

        var mod = await api.GetModAsync(929713, TestContext.Current.CancellationToken);

        Assert.Equal(929713, mod.Id);
        Assert.Equal("Awesome Spyglass", mod.Name);
        Assert.Equal("https://img/thumb.png", mod.Logo.ThumbnailUrl);
    }

    [Fact]
    public async Task SearchModsAsync_FollowsPaginationUntilTotalCountReached()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino",
                """{"data":[{"id":1,"name":"One"},{"id":2,"name":"Two"}],"pagination":{"index":0,"pageSize":50,"resultCount":2,"totalCount":3}}""")
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino&index=50",
                """{"data":[{"id":3,"name":"Three"}],"pagination":{"index":50,"pageSize":50,"resultCount":1,"totalCount":3}}""");
        var api = CreateApi(handler);

        var mods = await api.SearchModsAsync("dino", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], mods.Select(m => m.Id));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchModsAsync_EscapesSearchTermAndAppendsCategory()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=structures%20plus&categoryId=7",
                """{"data":[],"pagination":{"index":0,"pageSize":50,"resultCount":0,"totalCount":0}}""");
        var api = CreateApi(handler);

        var mods = await api.SearchModsAsync("structures plus", categoryId: 7, TestContext.Current.CancellationToken);

        Assert.Empty(mods);
        Assert.Single(handler.Requests);
    }

    private static CurseForgeApi CreateApi(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.curseforge.test") };
        return new CurseForgeApi(client, new ApiOptions { ArkGameId = GameId });
    }
}
