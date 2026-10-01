using System.Diagnostics;
using ArkAscendedServerAdmin.Infrastructure.Install;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Install;

/// <summary>
/// Runs <c>cmd.exe</c> scripts under the real pseudo console: lines must arrive while the child is still
/// running (the reason this launcher exists), free of the VT sequences ConPTY wraps around them, and the
/// child's exit code must come back unchanged.
/// </summary>
public sealed class PseudoConsoleSteamCmdLauncherTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Directory.CreateTempSubdirectory("asa-conpty ").FullName;
    private readonly PseudoConsoleSteamCmdLauncher _launcher = new(NullLogger<PseudoConsoleSteamCmdLauncher>.Instance);

    [Fact]
    public async Task ALineBeforeAPause_ArrivesBeforeTheChildExits()
    {
        var script = WriteScript("""
            @echo off
            echo early line
            ping -n 4 127.0.0.1 >nul
            echo late line
            exit /b 0
            """);
        var stopwatch = Stopwatch.StartNew();
        var arrivals = new List<(string Text, TimeSpan At)>();

        var exitCode = await _launcher.RunAsync(Launch(script), line => arrivals.Add((line, stopwatch.Elapsed)), _ => { }, Ct);
        var exitedAt = stopwatch.Elapsed;

        Assert.Equal(0, exitCode);
        Assert.Equal(["early line", "late line"], arrivals.Select(a => a.Text));
        var early = arrivals[0].At;
        Assert.True(exitedAt - early > TimeSpan.FromSeconds(2), $"early line at {early}, exit at {exitedAt}");
    }

    [Fact]
    public async Task TheExitCode_SurvivesThePseudoConsole()
    {
        var script = WriteScript("""
            @echo off
            echo Update complete, launching...
            exit /b 7
            """);
        var lines = new List<string>();

        var exitCode = await _launcher.RunAsync(Launch(script), lines.Add, _ => { }, Ct);

        Assert.Equal(7, exitCode);
        Assert.Equal(["Update complete, launching..."], lines);
    }

    [Fact]
    public async Task Cancellation_KillsTheChild_AndThrows()
    {
        var script = WriteScript("""
            @echo off
            echo waiting
            ping -n 60 127.0.0.1 >nul
            exit /b 0
            """);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _launcher.RunAsync(Launch(script), _ => { }, _ => { }, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a killed child may hold the folder for a moment.
        }
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_directory, "script.cmd");
        File.WriteAllText(path, body);
        return path;
    }

    private SteamCmdLaunch Launch(string script) =>
        new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", script], _directory);
}
