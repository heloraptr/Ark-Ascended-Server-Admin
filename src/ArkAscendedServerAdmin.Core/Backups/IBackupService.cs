using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>
/// Best-effort live world backups (plan step 28): <c>saveworld</c>, quiescence wait, explicit inventory,
/// snapshot by copy with SHA-256, re-inventory, zip, full verification, atomic rename, record, prune.
/// Runs under the instance lock. The scheduler calls this on the interval while the instance is Running;
/// the manual button calls it directly. Every attempt is persisted, including skipped and failed ones.
/// </summary>
public interface IBackupService
{
    Task<BackupRecord> BackupNowAsync(int instanceId, bool isManual, CancellationToken cancellationToken);
}
