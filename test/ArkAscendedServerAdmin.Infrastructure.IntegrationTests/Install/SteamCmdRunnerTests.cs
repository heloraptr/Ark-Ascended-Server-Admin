using System.Diagnostics;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Install;
using ArkAscendedServerAdmin.Install;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Install;

/// <summary>
/// Drives <see cref="SteamCmdRunner"/> through the launcher seam: a <c>cmd.exe /c</c> script that prints
/// two real progress lines and exits with the code the test asks for. No real SteamCMD, no network.
/// </summary>
public class SteamCmdRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SteamCmdRetryPolicy _fastPolicy = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4), maxAttempts: 3);

    [Fact]
    public async Task ExitZero_WithVerifiedManifest_Succeeds_AndReportsProgress()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher(0);
        var runner = fixture.CreateRunner(launcher, new SteamCmdRetryPolicy());
        var progress = new List<SteamCmdProgress?>();
        runner.Changed += progress.Add;

        var result = await runner.InstallOrUpdateAsync(validate: true, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, result.ExitCode);
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(fixture.Root.Layout.SteamCmdExecutable, launch.FileName);
        Assert.Equal(fixture.Root.Layout.SteamCmd, launch.WorkingDirectory);
        Assert.Equal(["+force_install_dir", fixture.Root.Layout.Server, "+login", "anonymous", "+app_update", "2430930", "validate", "+quit"], launch.Arguments);

        Assert.Equal([15.67, 50.0, null], progress.Select(p => p?.Percent));
        Assert.Null(runner.Current);

        var console = fixture.Console.Snapshot(ConsoleChannels.SteamCmd);
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Output && line.Text.Contains("progress: 50.00", StringComparison.Ordinal));
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Output && line.Text == "Success! App '2430930' fully installed.");
        // A pseudo console has one stream, so stderr arrives as ordinary output.
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Output && line.Text == "stderr noise");
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Info && line.Text.Contains("verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutValidate_OmitsTheValidateArgument()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher(0);
        var runner = fixture.CreateRunner(launcher, new SteamCmdRetryPolicy());

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["+force_install_dir", fixture.Root.Layout.Server, "+login", "anonymous", "+app_update", "2430930", "+quit"], launcher.Launches[0].Arguments);
    }

    [Fact]
    public async Task ExitSeven_RerunsImmediately_WithoutConsumingARetry()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher(7, 0);
        var noRetries = new SteamCmdRetryPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), maxAttempts: 1);
        var runner = fixture.CreateRunner(launcher, noRetries);

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, launcher.Launches.Count);
        var console = fixture.Console.Snapshot(ConsoleChannels.SteamCmd);
        Assert.Contains(console, line => line.Text == "Update complete, launching...");
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Info && line.Text.Contains("exit code 7", StringComparison.Ordinal));
        Assert.DoesNotContain(console, line => line.Text.Contains("retrying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonZeroExit_RetriesWithBackoff_ThenSucceeds()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher(1, 0);
        var runner = fixture.CreateRunner(launcher, _fastPolicy);

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, launcher.Launches.Count);
        var console = fixture.Console.Snapshot(ConsoleChannels.SteamCmd);
        Assert.Single(console, line => line.Kind == ConsoleLineKind.Warning && line.Text.Contains("retrying in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonZeroExit_ExhaustsTheBudget_AndFails()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher(1, 2, 3);
        var runner = fixture.CreateRunner(launcher, _fastPolicy);

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("3 attempt(s)", result.Error, StringComparison.Ordinal);
        Assert.Equal(3, launcher.Launches.Count);
        var console = fixture.Console.Snapshot(ConsoleChannels.SteamCmd);
        Assert.Equal(2, console.Count(line => line.Text.Contains("retrying in", StringComparison.Ordinal)));
        Assert.Contains(console, line => line.Kind == ConsoleLineKind.Error);
    }

    [Fact]
    public async Task ExitZero_WithoutAVerifiedInstall_CountsAsAFailedAttempt()
    {
        using var fixture = new Fixture(installComplete: false);
        var launcher = fixture.ScriptLauncher(0, 0, 0);
        var runner = fixture.CreateRunner(launcher, _fastPolicy);

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("not verified", result.Error, StringComparison.Ordinal);
        Assert.Equal(3, launcher.Launches.Count);
    }

    [Fact]
    public async Task Cancellation_KillsSteamCmd_AndReturnsAFailure()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher("sleep");
        var runner = fixture.CreateRunner(launcher, new SteamCmdRetryPolicy());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        var result = await runner.InstallOrUpdateAsync(validate: false, cts.Token).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.Null(runner.Current);
        Assert.Contains(fixture.Console.Snapshot(ConsoleChannels.SteamCmd), line => line.Kind == ConsoleLineKind.Warning && line.Text.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingSteamCmd_TriesTheDownloadFirst_AndReportsItsFailure()
    {
        using var fixture = new Fixture(installComplete: false, stubSteamCmd: false);
        var launcher = fixture.ScriptLauncher(0);
        var runner = fixture.CreateRunner(launcher, new SteamCmdRetryPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), maxAttempts: 2));

        var result = await runner.InstallOrUpdateAsync(validate: false, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("download failed", result.Error, StringComparison.Ordinal);
        Assert.Empty(launcher.Launches);
        Assert.Equal(2, fixture.Http.Requests);
        Assert.Contains(fixture.Console.Snapshot(ConsoleChannels.SteamCmd), line => line.Kind == ConsoleLineKind.Info && line.Text.Contains("Downloading SteamCMD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondConcurrentRun_IsRefused()
    {
        using var fixture = new Fixture(installComplete: true);
        var launcher = fixture.ScriptLauncher("sleep");
        var runner = fixture.CreateRunner(launcher, new SteamCmdRetryPolicy());
        using var cts = new CancellationTokenSource();

        var first = runner.InstallOrUpdateAsync(validate: false, cts.Token);
        await Task.Delay(300, Ct);
        var second = await runner.InstallOrUpdateAsync(validate: false, Ct);
        cts.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.False(second.Succeeded);
        Assert.Contains("already running", second.Error, StringComparison.Ordinal);
        Assert.Single(launcher.Launches);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _scriptPath;

        public Fixture(bool installComplete, bool stubSteamCmd = true)
        {
            Root = new TempDataRoot();
            if (stubSteamCmd)
            {
                File.WriteAllText(Root.Layout.SteamCmdExecutable, "stub");
            }

            if (installComplete)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Root.Layout.AppManifestPath)!);
                File.WriteAllText(Root.Layout.AppManifestPath, "\"AppState\"\n{\n\t\"appid\"\t\t\"2430930\"\n\t\"StateFlags\"\t\t\"4\"\n}\n");
            }

            _scriptPath = Path.Combine(Root.Layout.Root, "fake-steamcmd.cmd");
            File.WriteAllText(_scriptPath, """
                @echo off
                echo  Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)
                echo  Update state (0x61) downloading, progress: 50.00 (6103159476 / 12206318952)
                >&2 echo stderr noise
                if "%~1"=="0" echo Success! App '2430930' fully installed.
                if "%~1"=="7" echo Update complete, launching...
                if "%~1"=="sleep" ping -n 120 127.0.0.1 >nul
                exit /b %~1
                """);
        }

        public TempDataRoot Root { get; }

        public InMemoryConsoleService Console { get; } = new(NullLogger<InMemoryConsoleService>.Instance);

        public FailingHttp Http { get; } = new();

        public ScriptedLauncher ScriptLauncher(params object[] exitCodes) =>
            new(_scriptPath, new Queue<string>(exitCodes.Select(code => code.ToString()!)));

        public SteamCmdRunner CreateRunner(ISteamCmdProcessLauncher launcher, SteamCmdRetryPolicy policy) =>
            new(Root.Layout, Http, Console, new GameInstallChecker(Root.Layout), launcher, policy, TimeProvider.System, NullLogger<SteamCmdRunner>.Instance);

        public void Dispose() => Root.Dispose();
    }

    /// <summary>Points every launch at <c>cmd.exe /c fake-steamcmd.cmd &lt;code&gt;</c> through the production pseudo-console launcher.</summary>
    private sealed class ScriptedLauncher(string scriptPath, Queue<string> exitCodes) : ISteamCmdProcessLauncher
    {
        private readonly PseudoConsoleSteamCmdLauncher _inner = new(NullLogger<PseudoConsoleSteamCmdLauncher>.Instance);

        public List<SteamCmdLaunch> Launches { get; } = [];

        public Task<int> RunAsync(SteamCmdLaunch launch, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            var code = exitCodes.Count > 0 ? exitCodes.Dequeue() : "0";
            var redirected = new SteamCmdLaunch(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", scriptPath, code], launch.WorkingDirectory);
            return _inner.RunAsync(redirected, onOutput, onError, cancellationToken);
        }
    }

    /// <summary>An <see cref="IHttpClientFactory"/> whose client fails every request without touching the network.</summary>
    private sealed class FailingHttp : IHttpClientFactory
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(SteamCmdRunner.HttpClientName, name);
            return new HttpClient(new Handler(this));
        }

        private sealed class Handler(FailingHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                Assert.Equal(SteamCmdRunner.DownloadUri, request.RequestUri);
                throw new HttpRequestException("no network in tests");
            }
        }
    }
}
