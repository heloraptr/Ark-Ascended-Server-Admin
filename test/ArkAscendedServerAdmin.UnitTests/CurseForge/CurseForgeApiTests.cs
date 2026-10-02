using ArkAscendedServerAdmin.CurseForge;

namespace ArkAscendedServerAdmin.UnitTests.CurseForge;

public class CurseForgeApiTests
{
    private const int GameId = 83374;

    [Fact]
    public async Task GetModAsync_ReturnsDeserializedMod()
    {
        var handler = new StubHttpMessageHandler()
            .Respond("/v1/mods/929713", """{"data":{"id":929713,"name":"Awesome Spyglass","slug":"awesome-spyglass","links":{"websiteUrl":"https://www.curseforge.com/ark-survival-ascended/mods/awesome-spyglass"},"logo":{"thumbnailUrl":"https://img/thumb.png"}}}""");
        var api = CreateApi(handler);

        var mod = await api.GetModAsync(929713, TestContext.Current.CancellationToken);

        Assert.Equal(929713, mod.Id);
        Assert.Equal("Awesome Spyglass", mod.Name);
        Assert.Equal("https://img/thumb.png", mod.Logo?.ThumbnailUrl);
        Assert.Equal("https://www.curseforge.com/ark-survival-ascended/mods/awesome-spyglass", mod.Links.WebsiteUrl);
    }

    [Fact]
    public async Task GetModAsync_NullLogo_DeserializesAsNoLogo()
    {
        // CurseForge sends "logo": null for a mod without an image; that is "no thumbnail", not a failure.
        var handler = new StubHttpMessageHandler()
            .Respond("/v1/mods/929713", """{"data":{"id":929713,"name":"Awesome Spyglass","logo":null}}""");
        var api = CreateApi(handler);

        var mod = await api.GetModAsync(929713, TestContext.Current.CancellationToken);

        Assert.Equal("Awesome Spyglass", mod.Name);
        Assert.Null(mod.Logo);
    }

    [Fact]
    public async Task SearchModsAsync_UnderTheCap_ReturnsEveryMatch()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino", Page(1, 50, total: 53))
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino&index=50", Page(51, 3, total: 53));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("dino", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(1, 53), search.Mods.Select(m => m.Id));
        Assert.Equal(53, search.TotalCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchModsAsync_FullPageHoldingEveryMatch_DoesNotAskForMore()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino", Page(1, 50, total: 50));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("dino", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(50, search.Mods.Count);
        Assert.Equal(50, search.TotalCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchModsAsync_OverTheCap_StopsAfterTwoPages_AndReportsTheRealTotal()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=a", Page(1, 50, total: 2340))
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=a&index=50", Page(51, 50, total: 2340))
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=a&index=100", Page(101, 50, total: 2340));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("a", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CurseForgeApi.MaxSearchResults, search.Mods.Count);
        Assert.Equal(Enumerable.Range(1, 100), search.Mods.Select(m => m.Id));
        Assert.Equal(2340, search.TotalCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchModsAsync_APageLargerThanAsked_IsTrimmedToTheCap()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=a", Page(1, 120, total: 500));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("a", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CurseForgeApi.MaxSearchResults, search.Mods.Count);
        Assert.Equal(500, search.TotalCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchModsAsync_EmptyPageWithALargerTotal_StopsInsteadOfLooping()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino", Page(1, 0, total: 400));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("dino", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(search.Mods);
        Assert.Equal(400, search.TotalCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchModsAsync_ShortPage_StopsPaging()
    {
        // CurseForge says there are more, but a page under the page size means it has nothing further to send.
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=dino", Page(1, 2, total: 3));
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("dino", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], search.Mods.Select(m => m.Id));
        Assert.Equal(3, search.TotalCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchModsAsync_EscapesSearchTermAndAppendsCategory()
    {
        var handler = new StubHttpMessageHandler()
            .Respond($"/v1/mods/search?gameId={GameId}&searchFilter=structures%20plus&categoryId=7",
                """{"data":[],"pagination":{"index":0,"pageSize":50,"resultCount":0,"totalCount":0}}""");
        var api = CreateApi(handler);

        var search = await api.SearchModsAsync("structures plus", categoryId: 7, TestContext.Current.CancellationToken);

        Assert.Empty(search.Mods);
        Assert.Equal(0, search.TotalCount);
        Assert.Single(handler.Requests);
    }

    /// <summary>A search page of <paramref name="count"/> mods numbered from <paramref name="firstId"/>, reporting <paramref name="total"/> matches.</summary>
    private static string Page(int firstId, int count, int total)
    {
        var mods = string.Join(",", Enumerable.Range(firstId, count).Select(id => $"{{\"id\":{id},\"name\":\"Mod {id}\"}}"));
        return $"{{\"data\":[{mods}],\"pagination\":{{\"index\":{firstId - 1},\"pageSize\":50,\"resultCount\":{count},\"totalCount\":{total}}}}}";
    }

    private static CurseForgeApi CreateApi(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.curseforge.test") };
        return new CurseForgeApi(client, new ApiOptions { ArkGameId = GameId });
    }
}
