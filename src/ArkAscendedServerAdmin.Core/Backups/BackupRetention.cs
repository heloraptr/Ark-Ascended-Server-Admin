using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>
/// Retention pruning (plan step 28): keep the newest <c>retention</c> successful archives, manual and
/// scheduled counted alike. Skipped and failed records hold no file and never count toward retention;
/// they are capped separately at <see cref="UnsuccessfulRecordsKept"/> per instance so a schedule that
/// keeps failing cannot grow the table without bound.
/// </summary>
public static class BackupRetention
{
    /// <summary>How many skipped and failed records an instance keeps; the oldest beyond that go after each backup.</summary>
    public const int UnsuccessfulRecordsKept = 50;

    /// <summary>Returns the successful records to delete, oldest first, so that at most <paramref name="retention"/> remain.</summary>
    /// <param name="records">Every record of the instance, in any order; only <see cref="BackupOutcome.Success"/> ones count.</param>
    /// <param name="retention">How many successful archives to keep; values below one keep one.</param>
    public static IReadOnlyList<BackupRecord> SelectForPruning(IEnumerable<BackupRecord> records, int retention)
    {
        ArgumentNullException.ThrowIfNull(records);
        var keep = Math.Max(1, retention);

        return records
            .Where(r => r.Outcome == BackupOutcome.Success)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(keep)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>Returns the skipped and failed records to delete, oldest first, so that at most <paramref name="keep"/> remain.</summary>
    /// <param name="records">Every record of the instance, in any order; successful ones never count and are never selected.</param>
    /// <param name="keep">How many skipped and failed records to keep; values below zero keep none.</param>
    public static IReadOnlyList<BackupRecord> SelectUnsuccessfulForPruning(IEnumerable<BackupRecord> records, int keep = UnsuccessfulRecordsKept)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records
            .Where(r => r.Outcome != BackupOutcome.Success)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(Math.Max(0, keep))
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();
    }
}
