using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>
/// Scoped command facade for the Settings page. Every method first awaits
/// <see cref="Auth.IAuthorizationGuard.EnsureAuthorizedAsync"/>; the UI never touches the stores directly.
/// </summary>
public interface ISettingsCommands
{
    Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<HostConfiguration> GetHostConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a consistent copy of the database under <c>DataRoot\Exports</c> using SQLite's online backup
    /// API and returns its full path. A raw file copy is only safe with the service stopped.
    /// </summary>
    Task<string> ExportConfigBackupAsync(CancellationToken cancellationToken = default);
}

/// <summary>Scoped command facade for install/update actions; guarded like every other facade.</summary>
public interface IMaintenanceCommands
{
    Task RetryInstallAsync(CancellationToken cancellationToken = default);
}
