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

    [Fact]
    public async Task CancellationDuringHeavyOutput_DoesNotDeadlock()
    {
        var script = WriteScript("""
            @echo off
            :loop
            echo  Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952) %random%
            goto loop
            """);
        var lines = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _launcher.RunAsync(Launch(script), _ => Interlocked.Increment(ref lines), _ => { }, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
        Assert.True(lines > 10, $"only {lines} lines arrived");
    }

    [Fact]
    public async Task ACallbackThatThrows_DoesNotStopTheDrain_AndSurfacesAfterExit()
    {
        var script = WriteScript("""
            @echo off
            for /l %%i in (1,1,2000) do echo line %%i
            exit /b 3
            """);
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _launcher.RunAsync(
                Launch(script),
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("subscriber failed");
                },
                _ => { },
                Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Equal("subscriber failed", thrown.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AMissingExecutable_FailsPromptly_WithoutTheFallbackNotice()
    {
        // CreateProcessW fails after the pseudo console exists and before any reader runs.
        var missing = new SteamCmdLaunch(Path.Combine(_directory, "missing.exe"), ["+quit"], _directory);
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => _launcher.RunAsync(missing, _ => { }, errors.Add, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AnotherStartFailure_CleansUpPromptly_AndFallsBackToThePipeLauncher_WithOneNotice()
    {
        // The file exists but is not a program, so CreateProcessW fails (bad exe format) after the pseudo
        // console exists; the pipe launcher then fails the same way, which proves it was tried.
        var notAProgram = Path.Combine(_directory, "not-a-program.exe");
        File.WriteAllText(notAProgram, "not a program");
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => _launcher.RunAsync(new SteamCmdLaunch(notAProgram, ["+quit"], _directory), _ => { }, errors.Add, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.Equal([PseudoConsoleSteamCmdLauncher.FallbackNotice], errors);
    }

    [Fact]
    public async Task LinesAroundBlankLines_ArriveAloneAndInOrder_PastTheBottomOfTheConsole()
    {
        // ConPTY skips blank rows with a cursor move; 300 numbered lines scroll well past its 50 rows.
        var script = WriteScript("""
            @echo off
            for /l %%i in (1,1,300) do (
            echo line %%i
            echo.
            )
            exit /b 0
            """);
        var lines = new List<string>();

        var exitCode = await _launcher.RunAsync(Launch(script), lines.Add, _ => { }, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(Enumerable.Range(1, 300).Select(i => $"line {i}"), lines.Where(line => line.Length > 0));
    }

    [Fact]
    public async Task AnUnexpectedReadFailure_IsReportedOnce_AndTheRealExitCodeComesBack()
    {
        var script = WriteScript("""
            @echo off
            echo first
            ping -n 3 127.0.0.1 >nul
            for /l %%i in (1,1,2000) do echo line %%i
            exit /b 5
            """);
        var launcher = new PseudoConsoleSteamCmdLauncher(
            NullLogger<PseudoConsoleSteamCmdLauncher>.Instance,
            inner => new FaultyStream(inner, failOnRead: 2, endAsBrokenPipe: false));
        var lines = new List<string>();
        var errors = new List<string>();

        var exitCode = await launcher.RunAsync(Launch(script), lines.Add, errors.Add, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(5, exitCode);
        Assert.Equal([PseudoConsoleSteamCmdLauncher.OutputLostNotice], errors);
        Assert.DoesNotContain("line 2000", lines);
    }

    [Fact]
    public async Task ABrokenPipeAtTheEnd_IsANormalEndOfOutput()
    {
        var script = WriteScript("""
            @echo off
            echo only line
            exit /b 0
            """);
        var launcher = new PseudoConsoleSteamCmdLauncher(
            NullLogger<PseudoConsoleSteamCmdLauncher>.Instance,
            inner => new FaultyStream(inner, failOnRead: 0, endAsBrokenPipe: true));
        var lines = new List<string>();
        var errors = new List<string>();

        var exitCode = await launcher.RunAsync(Launch(script), lines.Add, errors.Add, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, exitCode);
        Assert.Empty(errors);
        Assert.Equal(["only line"], lines);
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

    /// <summary>Wraps the pseudo console's output: throws on one read, or reports end of stream as a broken pipe.</summary>
    private sealed class FaultyStream(Stream inner, int failOnRead, bool endAsBrokenPipe) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++_reads == failOnRead)
            {
                throw new IOException("simulated read failure");
            }

            var read = inner.Read(buffer, offset, count);
            return read == 0 && endAsBrokenPipe
                ? throw new IOException("The pipe has been ended.", unchecked((int)0x8007006D))
                : read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
