using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Maintenance;

/// <summary>What the Dashboard shows about the current update/recovery (plan steps 11, 29).</summary>
public sealed record MaintenanceSnapshot(MaintenancePhase Phase, IReadOnlyList<MaintenanceEntry> Entries, DateTimeOffset? StartedAt, string? Detail)
{
    public bool IsResolved => Phase == MaintenancePhase.None && Entries.Count == 0;

    public bool HasFailedEntries => Entries.Any(e => e.Error is not null);

    /// <summary>What the most recent SteamCMD run in this process concluded; in-memory only, null until one completes.</summary>
    public UpdateResult? LastResult { get; init; }
}

/// <summary>
/// The conclusion of one SteamCMD update run: the build id before and after (as Steam writes it to the
/// app manifest) and whether the files were validated. Equal ids mean the install was already current.
/// </summary>
public sealed record UpdateResult(DateTimeOffset CompletedAt, string? PreviousBuild, string? InstalledBuild, bool Validated)
{
    public bool BuildChanged => PreviousBuild is not null && InstalledBuild is not null && PreviousBuild != InstalledBuild;

    public string Summary => this switch
    {
        { BuildChanged: true } => $"Updated from build {PreviousBuild} to build {InstalledBuild}.",
        { InstalledBuild: not null, Validated: true } => $"Files verified; build {InstalledBuild} is the latest.",
        { InstalledBuild: not null } => $"Already on the latest build ({InstalledBuild}).",
        _ => "SteamCMD finished; the app manifest has no build id.",
    };
}

/// <summary>
/// The update flow (plan step 29): stop every live instance with verified exit, run SteamCMD, verify the
/// manifest, relaunch through the queue, persisting <c>MaintenanceState</c> at every transition. A second
/// update is rejected while one is running or while the persisted state is unresolved.
/// </summary>
public interface IUpdateService
{
    MaintenanceSnapshot Current { get; }

    /// <summary>Raised on a background thread whenever the persisted state changes.</summary>
    event Action<MaintenanceSnapshot>? Changed;

    /// <summary>
    /// Starts the flow in the background; the outcome only reports acceptance or the refusal reason.
    /// <paramref name="validate"/> forces SteamCMD's <c>validate</c> for this run even when the setting is off.
    /// </summary>
    Task<OperationOutcome> StartUpdateAsync(bool confirmStopRunningInstances, bool validate, CancellationToken cancellationToken);

    /// <summary>Re-enqueues a <c>Restarting</c> entry that recorded an error.</summary>
    Task<OperationOutcome> RetryEntryAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>Marks a failed entry done without launching it.</summary>
    Task<OperationOutcome> SkipEntryAsync(int instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// What a delete leaves behind. The two choices are independent: the world can be archived while its
/// backups go, or removed while they stay.
/// </summary>
/// <param name="KeepWorldData">Move <c>Saved</c> to <c>Archive</c> under the data root instead of deleting it.</param>
/// <param name="DeleteBackups">
/// Delete the instance's backup archives under <c>Backups\&lt;slug&gt;</c>. The backup history in the database
/// goes with the instance either way; this decides whether the zip files on disk go with it.
/// </param>
public sealed record InstanceDeleteOptions(bool KeepWorldData, bool DeleteBackups);

/// <summary>Instance delete (plan step 30), run under the instance lock on a detached job that the call nevertheless awaits: it returns once the rows are gone, or with the reason the job stopped.</summary>
public interface IInstanceDeleteService
{
    /// <summary>Stop with verified exit → firewall rules → junctions → archive or delete <c>Saved</c> → backups → database rows.</summary>
    Task<OperationOutcome> DeleteAsync(int instanceId, InstanceDeleteOptions options, CancellationToken cancellationToken);
}
