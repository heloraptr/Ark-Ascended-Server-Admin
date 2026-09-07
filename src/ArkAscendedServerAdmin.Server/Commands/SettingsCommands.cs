using System.Globalization;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
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

/// <summary>Guarded facade over owner-initiated maintenance actions.</summary>
public sealed class MaintenanceCommands(IAuthorizationGuard guard, IStartupControl startupControl) : IMaintenanceCommands
{
    public async Task RetryInstallAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await startupControl.RetryInstallAsync(cancellationToken);
    }
}
