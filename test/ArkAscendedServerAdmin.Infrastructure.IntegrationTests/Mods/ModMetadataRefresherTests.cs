using ArkAscendedServerAdmin.CurseForge.Models.Mods;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Mods;
using ArkAscendedServerAdmin.Mods;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Mods;

/// <summary>
/// B8: the metadata refresh outside the command facade, and the daily poll that drives it with no
/// interactive user. Both run against the real library table and the fake CurseForge client.
/// </summary>
public class ModMetadataRefresherTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTime _modified = new(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Refresh_WithoutAnApiKey_FailsAndNeverCallsTheApi()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: false);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.Equal(ModMetadata.NoApiKeyMessage, result.Error);
        await f.CurseForge.DidNotReceiveWithAnyArgs().GetModsAsync(default!, default, ct);
    }

    [Fact]
    public async Task Refresh_WritesTheNewMetadataAndCountsTheChangedRows()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([ApiMod(1, "Renamed")]);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.Value);
        await using var db = root.CreateDbContext();
        var entry = await db.ModLibrary.SingleAsync(ct);
        Assert.Equal(("Renamed", new DateTimeOffset(_modified)), (entry.Name, entry.DateModified));
    }

    [Fact]
    public async Task Refresh_WhenTheApiFails_ExplainsItAndLeavesTheLibraryAlone()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>())
            .Returns<Task<List<Mod>>>(_ => throw new HttpRequestException("no route", null, System.Net.HttpStatusCode.Forbidden));

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.Equal("CurseForge rejected the API key. Check it on the Settings page.", result.Error);
        await using var db = root.CreateDbContext();
        Assert.Equal("Before", (await db.ModLibrary.SingleAsync(ct)).Name);
    }

    [Fact]
    public async Task Refresh_WritesThePageLinkAlongWithAChange()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([ApiMod(1, "Renamed", Page)]);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.Equal(1, result.Value);
        await using var db = root.CreateDbContext();
        Assert.Equal(Page, (await db.ModLibrary.SingleAsync(ct)).WebsiteUrl);
    }

    [Fact]
    public async Task Refresh_StoresNoLinkThatIsNotAnHttpsCurseForgeUrl()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        await using (var db = root.CreateDbContext())
        {
            (await db.ModLibrary.SingleAsync(ct)).WebsiteUrl = Page;
            await db.SaveChangesAsync(ct);
        }

        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([ApiMod(1, "Renamed", "https://evil.example/mods/1")]);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.Equal(1, result.Value);
        await using var check = root.CreateDbContext();
        Assert.Null((await check.ModLibrary.SingleAsync(ct)).WebsiteUrl);
    }

    /// <summary>
    /// A row whose other fields are current is left alone even when CurseForge now reports a page link:
    /// the link is never a reason to write by itself (older rows are back-filled out of band), and the
    /// refresh count, like the launch badge, keeps reflecting real metadata changes only.
    /// </summary>
    [Fact]
    public async Task Refresh_ALinkAloneIsNotAChange()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        await MakeCurrentAsync(root, ct);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([ApiMod(1, "Current", Page)]);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.Equal(0, result.Value);
        await using var db = root.CreateDbContext();
        var entry = await db.ModLibrary.SingleAsync(ct);
        Assert.Equal(((string?)null, new DateTimeOffset(_modified)), (entry.WebsiteUrl, entry.DateModified));
    }

    /// <summary>Brings the seeded row level with <see cref="ApiMod"/> so only the link can differ.</summary>
    private static async Task MakeCurrentAsync(TempDataRoot root, CancellationToken ct)
    {
        await using var db = root.CreateDbContext();
        var entry = await db.ModLibrary.SingleAsync(ct);
        entry.Name = "Current";
        entry.Summary = "About it";
        entry.ThumbnailUrl = "https://cdn/thumb.png";
        entry.DateModified = new DateTimeOffset(_modified);
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Poll_WithoutAnApiKey_SkipsWithoutTouchingTheApi()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: false);

        await f.Poll.RefreshOnceAsync(ct);

        await f.CurseForge.DidNotReceiveWithAnyArgs().GetModsAsync(default!, default, ct);
        await using var db = root.CreateDbContext();
        Assert.Equal("Before", (await db.ModLibrary.SingleAsync(ct)).Name);
    }

    [Fact]
    public async Task Poll_WithAnApiKey_RefreshesTheLibrary()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([ApiMod(1, "Polled")]);

        await f.Poll.RefreshOnceAsync(ct);

        await f.CurseForge.Received(1).GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>());
        await using var db = root.CreateDbContext();
        var entry = await db.ModLibrary.SingleAsync(ct);
        Assert.Equal(("Polled", new DateTimeOffset(_modified)), (entry.Name, entry.DateModified));
    }

    [Fact]
    public async Task Poll_WhenTheRefreshFails_ReportsItWithoutThrowing()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>())
            .Returns<Task<List<Mod>>>(_ => throw new HttpRequestException("down"));

        await f.Poll.RefreshOnceAsync(ct);

        await using var db = root.CreateDbContext();
        Assert.Equal("Before", (await db.ModLibrary.SingleAsync(ct)).Name);
    }

    private const string Page = "https://www.curseforge.com/ark-survival-ascended/mods/current";

    private static Mod ApiMod(int id, string name, string website = "") =>
        new()
        {
            Id = id,
            Name = name,
            Links = new ModLinks { WebsiteUrl = website },
            Summary = "About it",
            Logo = new ModAsset { ThumbnailUrl = "https://cdn/thumb.png" },
            Authors = [new Author { Name = "Author" }],
            DateModified = _modified,
            DownloadCount = 10,
        };

    private sealed class Fixture
    {
        public required ICurseForgeApi CurseForge { get; init; }

        public required ModMetadataRefresher Refresher { get; init; }

        public required ModMetadataPoll Poll { get; init; }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, CancellationToken ct, bool withApiKey)
        {
            await root.InitializeAsync(ct);
            await using (var db = root.CreateDbContext())
            {
                db.ModLibrary.Add(new ModLibraryEntry { Id = 1, Name = "Before", AddedAt = _now });
                await db.SaveChangesAsync(ct);
            }

            var settings = new AppSettingsStore(root);
            if (withApiKey)
            {
                await settings.SaveAsync((await settings.GetAsync(ct)) with { CurseForgeApiKey = "cf-key" }, ct);
            }

            var curseForge = Substitute.For<ICurseForgeApi>();
            var refresher = new ModMetadataRefresher(root, settings, curseForge, NullLogger<ModMetadataRefresher>.Instance);

            // The poll resolves a refresher per pass from its own scope, exactly as it does in the host.
            var services = new ServiceCollection();
            services.AddSingleton<IModMetadataRefresher>(refresher);
            var provider = services.BuildServiceProvider();
            var poll = new ModMetadataPoll(
                provider.GetRequiredService<IServiceScopeFactory>(),
                settings,
                new FastTimeProvider(_now),
                NullLogger<ModMetadataPoll>.Instance);

            return new Fixture { CurseForge = curseForge, Refresher = refresher, Poll = poll };
        }
    }
}
