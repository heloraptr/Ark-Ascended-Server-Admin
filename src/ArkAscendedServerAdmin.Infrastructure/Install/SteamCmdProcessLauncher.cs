using System.Diagnostics;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>What <see cref="SteamCmdRunner"/> asks the launcher to start (plan step 20).</summary>
/// <param name="FileName">The executable, normally <c>DataRoot\SteamCMD\steamcmd.exe</c>.</param>
/// <param name="Arguments">Arguments in order; passed through <c>ProcessStartInfo.ArgumentList</c>, never joined by hand.</param>
/// <param name="WorkingDirectory">The SteamCMD directory, so its own logs and update land there.</param>
public sealed record SteamCmdLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// The process seam under <see cref="SteamCmdRunner"/>: starts one child, streams its stdout/stderr
/// line by line, and returns the exit code. Cancellation kills the child and throws
/// <see cref="OperationCanceledException"/>. Tests substitute this to script exit codes or to point the
/// runner at <c>cmd.exe</c>; production uses <see cref="ProcessSteamCmdLauncher"/>.
/// </summary>
public interface ISteamCmdProcessLauncher
{
    Task<int> RunAsync(SteamCmdLaunch launch, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);
}

/// <summary>Real <see cref="ISteamCmdProcessLauncher"/> over <see cref="Process"/> with redirected, windowless output.</summary>
public sealed class ProcessSteamCmdLauncher : ISteamCmdProcessLauncher
{
    public async Task<int> RunAsync(SteamCmdLaunch launch, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onOutput);
        ArgumentNullException.ThrowIfNull(onError);

        var startInfo = new ProcessStartInfo(launch.FileName)
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onError(e.Data);
            }
        };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return process.ExitCode;
    }
}
