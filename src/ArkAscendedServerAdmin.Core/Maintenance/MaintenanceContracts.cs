using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Maintenance;

/// <summary>What the Dashboard shows about the current update/recovery (plan steps 11, 29).</summary>
public sealed record MaintenanceSnapshot(MaintenancePhase Phase, IReadOnlyList<MaintenanceEntry> Entries, DateTimeOffset? StartedAt, string? Detail)
{
    public bool IsResolved => Phase == MaintenancePhase.None && Entries.Count == 0;

    public bool HasFailedEntries => Entries.Any(e => e.Error is not null);
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

    /// <summary>Starts the flow in the background; the outcome only reports acceptance or the refusal reason.</summary>
    Task<OperationOutcome> StartUpdateAsync(bool confirmStopRunningInstances, CancellationToken cancellationToken);

    /// <summary>Re-enqueues a <c>Restarting</c> entry that recorded an error.</summary>
    Task<OperationOutcome> RetryEntryAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>Marks a failed entry done without launching it.</summary>
    Task<OperationOutcome> SkipEntryAsync(int instanceId, CancellationToken cancellationToken);
}

/// <summary>Instance delete (plan step 30), run to completion in the background under the instance lock.</summary>
public interface IInstanceDeleteService
{
    /// <summary>Stop with verified exit → firewall rules → junctions → archive or delete <c>Saved</c> → database rows.</summary>
    Task<OperationOutcome> DeleteAsync(int instanceId, bool keepWorldData, CancellationToken cancellationToken);
}
