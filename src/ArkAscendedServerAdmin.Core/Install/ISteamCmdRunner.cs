namespace ArkAscendedServerAdmin.Install;

public sealed record SteamCmdResult(bool Succeeded, int ExitCode, string? Error)
{
    public static readonly SteamCmdResult Success = new(true, 0, null);

    public static SteamCmdResult Failure(int exitCode, string error) => new(false, exitCode, error);
}

/// <summary>
/// Runs SteamCMD against the shared install (plan step 20): downloads and extracts it on first use,
/// runs <c>+force_install_dir</c> then <c>+login anonymous</c> then <c>+app_update 2430930</c> (with
/// <c>validate</c> when asked), retries non-zero exits with exponential backoff (5 attempts, 30 s → 8 min),
/// re-runs immediately on exit code 7 (first-run self-update), and streams output to the
/// <c>ConsoleChannels.SteamCmd</c> console. It never touches <c>MaintenanceState</c>; callers own phases.
/// </summary>
public interface ISteamCmdRunner
{
    Task<SteamCmdResult> InstallOrUpdateAsync(bool validate, CancellationToken cancellationToken);
}
