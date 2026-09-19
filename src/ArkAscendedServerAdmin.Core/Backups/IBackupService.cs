using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>
/// Best-effort live world backups (plan step 28): <c>saveworld</c>, quiescence wait, explicit inventory,
/// snapshot by copy with SHA-256, re-inventory, zip, full verification, atomic rename, record, prune.
/// Runs under the instance lock. The scheduler calls this on the interval while the instance is Running;
/// the manual button calls it directly. Every attempt is persisted, including skipped and failed ones.
/// Restore (B2) is the reverse: validate the archive, take every affected lock, copy the current files
/// aside, replace the selected set, roll back from the copy on failure, and leave a journal when interrupted.
/// </summary>
public interface IBackupService
{
    Task<BackupRecord> BackupNowAsync(int instanceId, bool isManual, CancellationToken cancellationToken);

    /// <summary>Raised on a background thread after every attempt's record is persisted, whatever its outcome.</summary>
    event Action<BackupRecord>? Recorded;

    /// <summary>
    /// What the restore dialog needs before it asks (B2): whether the archive can be restored at all, whether
    /// cluster data can be included and why not, and which sibling instances a cluster restore touches.
    /// </summary>
    Task<RestoreInspection> InspectRestoreAsync(int instanceId, string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Restores the archive named by one of the instance's successful backup records (B2). Rejected without touching
    /// anything when the archive fails validation, a lock is held, a journal references an affected instance, or an
    /// affected instance is not stopped. The destructive phase runs on a detached job that the call still awaits.
    /// </summary>
    Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken);

    /// <summary>Re-acquires the journal's lock set, re-checks the stopped states, and copies the safety copy back (B2).</summary>
    Task<OperationOutcome> RecoverAsync(string operationId, CancellationToken cancellationToken);

    /// <summary>Removes the journal without touching the directories, for the owner who resolved it by hand (B2).</summary>
    Task<OperationOutcome> DiscardJournalAsync(string operationId, CancellationToken cancellationToken);

    /// <summary>Raised on a background thread after every restore or recovery record is persisted, whatever its outcome.</summary>
    event Action<RestoreRecord>? Restored;
}

/// <summary>What the restore dialog shows before the owner confirms (B2).</summary>
/// <param name="Problem">Why the archive cannot be restored at all; null when it can.</param>
/// <param name="ClusterAvailable">True when "also restore cluster data" may be ticked.</param>
/// <param name="ClusterUnavailableReason">Why the checkbox is disabled; null when it is enabled.</param>
/// <param name="SiblingNames">The other instances in the cluster, whose shared directory a cluster restore replaces.</param>
public sealed record RestoreInspection(string? Problem, bool ClusterAvailable, string? ClusterUnavailableReason, IReadOnlyList<string> SiblingNames)
{
    public bool CanRestore => Problem is null;
}
