using System.Diagnostics;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

/// <summary>
/// Runs a script in a child Windows PowerShell (<c>powershell.exe</c>, the shell the installer scripts must work
/// in) with <c>install\ArkInstall.Common.ps1</c> dot-sourced, so the script functions are exercised for real.
/// </summary>
internal static class ChildPowerShell
{
    private static readonly Lazy<string?> _unavailableReason = new(Probe);

    /// <summary>Why <c>powershell.exe</c> cannot be used here, or null when it can; tests skip with this reason.</summary>
    public static string? UnavailableReason => _unavailableReason.Value;

    /// <summary>The repository's <c>install\ArkInstall.Common.ps1</c>, found by walking up from the test binaries.</summary>
    public static string CommonScriptPath
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "install", "ArkInstall.Common.ps1");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"install\\ArkInstall.Common.ps1 was not found above {AppContext.BaseDirectory}.");
        }
    }

    /// <summary>Single-quoted PowerShell literal for <paramref name="value"/>.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Runs <paramref name="body"/> after dot-sourcing the common script; returns the exit code and the standard output lines.</summary>
    public static (int ExitCode, IReadOnlyList<string> Lines, string Error) Run(string body)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ArkAdminTests-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, $"$ErrorActionPreference = 'Stop'\r\n. {Quote(CommonScriptPath)}\r\n{body}\r\n");
        try
        {
            var (exitCode, output, error) = Start($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"");
            var lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            return (exitCode, lines, error);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string? Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows PowerShell only exists on Windows.";
        }

        try
        {
            var (exitCode, output, error) = Start("-NoProfile -NonInteractive -Command \"'ok'\"");
            return exitCode == 0 && output.Trim() == "ok" ? null : $"powershell.exe did not run a trivial command (exit {exitCode}): {error.Trim()}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return $"powershell.exe could not be started: {ex.Message}";
        }
    }

    private static (int ExitCode, string Output, string Error) Start(string arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("powershell.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("powershell.exe did not finish within 120 s.");
        }

        return (process.ExitCode, output.Result, error.Result);
    }
}
