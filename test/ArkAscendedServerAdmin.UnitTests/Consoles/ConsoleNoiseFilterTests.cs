using ArkAscendedServerAdmin.Consoles;

namespace ArkAscendedServerAdmin.UnitTests.Consoles;

public sealed class ConsoleNoiseFilterTests
{
    // Captured 2026-09-13 from a live server's shutdown.
    private static readonly string[] Shutdown =
    [
        "[2026.09.13-19.48.56:511][890]Closing by request",
        "[2026.09.13-19.48.57:979][891]2026.09.13_19.48.57: Saving world...",
        "[2026.09.13-19.49.12:791][891]LogSentrySdk: Verbose: sending envelope",
        "[2026.09.13-19.49.12:791][891]LogSentrySdk: Verbose: sending request using winhttp to \"https://o339527.ingest.us.sentry.io:443/api/4511900435021824/envelope/\":",
        "x-sentry-auth:Sentry sentry_key=a50f4ac6dd01c37c500a2d9de6a6b690, sentry_version=7, sentry_client=sentry.native.unreal/0.12.0",
        "content-type:application/x-sentry-envelope",
        "[2026.09.13-19.49.13:079][891]LogSentrySdk: Verbose: received response:",
        "HTTP/1.1 200 ",
        "Server: nginx",
        "[2026.09.13-19.49.13:079][891]LogSentrySdk: Verbose: background worker thread shut down",
        "[2026.09.13-19.49.14:000][892]Log file closed, 09/13/26 15:49:14",
    ];

    [Fact]
    public void Drops_sentry_lines_and_their_unstamped_continuations()
    {
        var filter = new ConsoleNoiseFilter();

        var shown = Shutdown.Where(filter.ShouldShow).ToList();

        Assert.Equal(
            [
                "[2026.09.13-19.48.56:511][890]Closing by request",
                "[2026.09.13-19.48.57:979][891]2026.09.13_19.48.57: Saving world...",
                "[2026.09.13-19.49.14:000][892]Log file closed, 09/13/26 15:49:14",
            ],
            shown);
    }

    [Fact]
    public void Keeps_unstamped_lines_that_follow_an_ordinary_line()
    {
        var filter = new ConsoleNoiseFilter();

        Assert.True(filter.ShouldShow("[2026.09.13-18.31.43:980][  0]LogInit: Command Line: -log"));
        Assert.True(filter.ShouldShow("continued on the next line"));
        Assert.True(filter.ShouldShow(string.Empty));
    }

    [Fact]
    public void An_unstamped_first_line_is_shown()
    {
        Assert.True(new ConsoleNoiseFilter().ShouldShow("x-sentry-auth: orphaned by the backfill window"));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleNoiseFilter().ShouldShow(null!));
    }
}
