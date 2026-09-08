using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Processes;

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

/// <summary>Scoped command facade for install/update actions (plan steps 11, 20, 29); guarded like every other facade.</summary>
public interface IMaintenanceCommands
{
    /// <summary>Re-runs the install after an <c>InstallFailed</c> readiness phase.</summary>
    Task RetryInstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the update flow in the background; the outcome reports acceptance or the refusal reason.</summary>
    Task<OperationOutcome> StartUpdateAsync(bool confirmStopRunningInstances, CancellationToken cancellationToken = default);

    /// <summary>Re-enqueues a <c>Restarting</c> entry that recorded an error.</summary>
    Task<OperationOutcome> RetryEntryAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Marks a failed entry done without launching it.</summary>
    Task<OperationOutcome> SkipEntryAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes the persisted maintenance phase in the background, e.g. re-runs SteamCMD after a failed
    /// update left the phase at <c>Updating</c>. Rejected when nothing is pending.
    /// </summary>
    Task<OperationOutcome> ResumeMaintenanceAsync(CancellationToken cancellationToken = default);
}
