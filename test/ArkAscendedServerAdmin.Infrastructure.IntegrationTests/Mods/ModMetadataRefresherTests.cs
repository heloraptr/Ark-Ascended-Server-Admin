using System.Text.Json;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;
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

    /// <summary>CurseForge sends <c>"logo": null</c> for a mod without an image; the rest still updates and the thumbnail is cleared.</summary>
    [Fact]
    public async Task Refresh_ModWithoutALogo_SavesTheOtherFieldsAndClearsTheThumbnail()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        await using (var db = root.CreateDbContext())
        {
            (await db.ModLibrary.SingleAsync(ct)).ThumbnailUrl = "https://cdn/old.png";
            await db.SaveChangesAsync(ct);
        }

        var mod = ApiMod(1, "Renamed");
        mod.Logo = null;
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>()).Returns([mod]);

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.Value);
        await using var check = root.CreateDbContext();
        var entry = await check.ModLibrary.SingleAsync(ct);
        Assert.Equal(("Renamed", "About it", (string?)null), (entry.Name, entry.Summary, entry.ThumbnailUrl));
    }

    [Fact]
    public async Task Refresh_WhenTheResponseIsMalformed_FailsAndLeavesTheLibraryAlone()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct, withApiKey: true);
        f.CurseForge.GetModsAsync(Arg.Any<IEnumerable<int>>(), false, Arg.Any<CancellationToken>())
            .Returns<Task<List<Mod>>>(_ => throw new JsonException("Unexpected end of data."));

        var result = await f.Refresher.RefreshAsync(ct);

        Assert.False(result.Succeeded);
        Assert.Equal("CurseForge could not be reached: Unexpected end of data.", result.Error);
        await using var db = root.CreateDbContext();
        Assert.Equal("Before", (await db.ModLibrary.SingleAsync(ct)).Name);
    }

    /// <summary>
    /// An exception type the poll never expected (here from the settings read) is logged and tomorrow's pass tries
    /// again; it must not escape <c>ExecuteAsync</c>, where it would stop the whole service.
    /// </summary>
    [Fact]
    public async Task PollLoop_PassThatThrows_IsLoggedAndTriedAgainOneIntervalLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var wait = TimeSpan.FromSeconds(10);
        var clock = new ManualTimeProvider(_now);
        var attempts = new AttemptLog();
        var settings = Substitute.For<IAppSettingsStore>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            attempts.Record() == 1
                ? Task.FromException<AppSettings>(new NotSupportedException("The first pass fails."))
                : Task.FromResult(new AppSettings())); // no API key: later passes skip without a scope
        var log = new RecordingLogger<ModMetadataPoll>();
        var poll = new ModMetadataPoll(Substitute.For<IServiceScopeFactory>(), settings, clock, log);

        await poll.StartAsync(ct);
        await clock.WaitForPendingTimerAsync().WaitAsync(wait, ct);
        clock.Advance(ModMetadataPoll.Interval);
        await attempts.WhenAttempt(1).WaitAsync(wait, ct);

        // Parked on the next delay, so the failure has been handled and nothing else can run until the clock moves.
        await clock.WaitForPendingTimerAsync().WaitAsync(wait, ct);
        var error = Assert.Single(log.Errors);
        Assert.IsType<NotSupportedException>(error.Exception);
        Assert.Equal("Mod metadata poll failed; it will try again tomorrow.", error.Message);

        clock.Advance(ModMetadataPoll.Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(1, attempts.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await attempts.WhenAttempt(2).WaitAsync(wait, ct);

        await clock.WaitForPendingTimerAsync().WaitAsync(wait, ct);
        await poll.StopAsync(ct);
        Assert.True(poll.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(log.Errors);
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
