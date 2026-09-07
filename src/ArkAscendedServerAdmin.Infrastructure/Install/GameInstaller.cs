using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Install;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// The first-run installer used by the startup pipeline (plan steps 11, 20): one SteamCMD run with the
/// <c>SteamCmdValidate</c> setting, mapped to <see cref="InstallResult"/>. It never touches
/// <c>MaintenanceState</c>; the orchestrator and the update flow own phases.
/// </summary>
public sealed class GameInstaller(ISteamCmdRunner runner, IAppSettingsStore settings) : IGameInstaller
{
    public async Task<InstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken);
        var result = await runner.InstallOrUpdateAsync(current.SteamCmdValidate, cancellationToken);
        return result.Succeeded
            ? InstallResult.Success
            : InstallResult.Failure(result.Error ?? $"SteamCMD exited with code {result.ExitCode}.");
    }
}
