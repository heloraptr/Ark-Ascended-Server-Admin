using System.ComponentModel;
using System.Diagnostics;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// Startup step (plan step 11): finds any <c>steamcmd.exe</c> still running out of
/// <c>DataRoot\SteamCMD</c> (a previous service instance's install), waits up to
/// <see cref="WaitBound"/> for it to exit, then kills it so a resumed install never runs two SteamCMDs
/// against one <c>Server\</c>. Processes are matched by <c>Process.MainModule.FileName</c>; a
/// <c>steamcmd.exe</c> from elsewhere on the box is left alone.
/// </summary>
public sealed class SteamCmdReconciler(DataRootLayout layout, TimeProvider timeProvider, ILogger<SteamCmdReconciler> logger, TimeSpan? waitBound = null) : ISteamCmdReconciler
{
    public static readonly TimeSpan DefaultWaitBound = TimeSpan.FromMinutes(2);

    public TimeSpan WaitBound { get; } = waitBound ?? DefaultWaitBound;

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var survivors = FindSurvivors();
        if (survivors.Count == 0)
        {
            logger.LogDebug("No SteamCMD process is running from {Directory}.", layout.SteamCmd);
            return;
        }

        try
        {
            foreach (var process in survivors)
            {
                await WaitThenKillAsync(process, cancellationToken);
            }
        }
        finally
        {
            foreach (var process in survivors)
            {
                process.Dispose();
            }
        }
    }

    private List<Process> FindSurvivors()
    {
        var survivors = new List<Process>();
        var root = Path.TrimEndingDirectorySeparator(layout.SteamCmd) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcessesByName("steamcmd"))
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                logger.LogDebug(ex, "Could not read the executable path of steamcmd.exe pid {Pid}; ignoring it.", process.Id);
            }

            if (path is not null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                survivors.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return survivors;
    }

    private async Task WaitThenKillAsync(Process process, CancellationToken cancellationToken)
    {
        logger.LogWarning("SteamCMD pid {Pid} is still running from a previous service instance; waiting up to {Bound} for it to exit.", process.Id, WaitBound);

        using var timeout = new CancellationTokenSource(WaitBound, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            logger.LogInformation("SteamCMD pid {Pid} exited on its own with code {ExitCode}.", process.Id, process.ExitCode);
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Bound exceeded; fall through to kill.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            logger.LogWarning("Killed SteamCMD pid {Pid} after {Bound}.", process.Id, WaitBound);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogInformation(ex, "SteamCMD pid {Pid} was already gone when the kill was attempted.", process.Id);
        }
    }
}
