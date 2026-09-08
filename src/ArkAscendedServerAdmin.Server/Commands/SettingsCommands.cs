using System.Globalization;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;
using ArkAscendedServerAdmin.Storage;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade over the App Settings store, host configuration, and config export.</summary>
public sealed class SettingsCommands(
    IAuthorizationGuard guard,
    IAppSettingsStore settings,
    IConfigBackupExporter exporter,
    DataRootLayout layout,
    HostConfiguration hostConfiguration,
    TimeProvider timeProvider) : ISettingsCommands
{
    public async Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await settings.GetAsync(cancellationToken);
    }

    public async Task SaveAppSettingsAsync(AppSettings appSettings, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await settings.SaveAsync(appSettings, cancellationToken);
    }

    public async Task<HostConfiguration> GetHostConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return hostConfiguration;
    }

    public async Task<string> ExportConfigBackupAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = Path.Combine(layout.Exports, $"config-{stamp}.db");
        await exporter.ExportAsync(destination, cancellationToken);
        return destination;
    }
}

/// <summary>Guarded facade over owner-initiated maintenance actions (install retry, update, recovery entries).</summary>
public sealed class MaintenanceCommands(
    IAuthorizationGuard guard,
    IStartupControl startupControl,
    IUpdateService updateService,
    IMaintenanceRecovery recovery,
    ILogger<MaintenanceCommands> logger) : IMaintenanceCommands
{
    public async Task RetryInstallAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await startupControl.RetryInstallAsync(cancellationToken);
    }

    public async Task<OperationOutcome> StartUpdateAsync(bool confirmStopRunningInstances, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await updateService.StartUpdateAsync(confirmStopRunningInstances, cancellationToken);
    }

    public async Task<OperationOutcome> RetryEntryAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await updateService.RetryEntryAsync(instanceId, cancellationToken);
    }

    public async Task<OperationOutcome> SkipEntryAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await updateService.SkipEntryAsync(instanceId, cancellationToken);
    }

    public async Task<OperationOutcome> ResumeMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        var phase = updateService.Current.Phase;
        if (phase is MaintenancePhase.None or MaintenancePhase.Installing)
        {
            return OperationOutcome.Rejected("There is no interrupted update to resume.");
        }

        // Fire-and-forget by design (HANDOVER §2): the resume drives SteamCMD and the relaunches in the
        // background; the dashboard follows it through IUpdateService.Changed.
        _ = Task.Run(async () =>
        {
            try
            {
                await recovery.ResumeAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Resuming the {Phase} maintenance phase failed.", phase);
            }
        }, CancellationToken.None);

        return OperationOutcome.Success;
    }
}
