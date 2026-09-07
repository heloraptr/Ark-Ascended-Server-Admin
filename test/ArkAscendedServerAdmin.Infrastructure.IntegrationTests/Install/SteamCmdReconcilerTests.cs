using System.Diagnostics;
using ArkAscendedServerAdmin.Infrastructure.Install;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Install;

/// <summary>
/// Uses a copy of <c>cmd.exe</c> named <c>steamcmd.exe</c> inside the temp DataRoot's SteamCMD directory
/// as the "surviving SteamCMD" the reconciler must wait for and bound.
/// </summary>
public class SteamCmdReconcilerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NoSurvivor_ReturnsQuickly()
    {
        using var root = new TempDataRoot();
        var reconciler = new SteamCmdReconciler(root.Layout, TimeProvider.System, NullLogger<SteamCmdReconciler>.Instance, TimeSpan.FromMinutes(2));

        var stopwatch = Stopwatch.StartNew();
        await reconciler.ReconcileAsync(Ct);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task SurvivorExceedingTheBound_IsKilled()
    {
        using var root = new TempDataRoot();
        using var survivor = StartFakeSteamCmd(root, seconds: 120);
        var reconciler = new SteamCmdReconciler(root.Layout, TimeProvider.System, NullLogger<SteamCmdReconciler>.Instance, TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        await reconciler.ReconcileAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(survivor.HasExited, "the survivor should have been killed");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task SurvivorThatExitsOnItsOwn_IsWaitedFor_NotKilled()
    {
        using var root = new TempDataRoot();
        using var survivor = StartFakeSteamCmd(root, seconds: 2);
        var reconciler = new SteamCmdReconciler(root.Layout, TimeProvider.System, NullLogger<SteamCmdReconciler>.Instance, TimeSpan.FromMinutes(2));

        await reconciler.ReconcileAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(survivor.HasExited);
        Assert.Equal(0, survivor.ExitCode);
    }

    private static Process StartFakeSteamCmd(TempDataRoot root, int seconds)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), root.Layout.SteamCmdExecutable);
        var startInfo = new ProcessStartInfo(root.Layout.SteamCmdExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root.Layout.SteamCmd,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"ping -n {seconds} 127.0.0.1 >nul & exit /b 0");
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the fake steamcmd.exe.");

        // A just-started process is not always enumerable by name (or its main module readable) yet.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var visible = Process.GetProcessesByName("steamcmd");
            try
            {
                if (visible.Any(candidate => candidate.Id == process.Id && candidate.MainModule?.FileName is not null))
                {
                    return process;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not ready yet.
            }
            finally
            {
                foreach (var candidate in visible)
                {
                    candidate.Dispose();
                }
            }

            Thread.Sleep(20);
        }

        throw new InvalidOperationException("The fake steamcmd.exe never became visible.");
    }
}
