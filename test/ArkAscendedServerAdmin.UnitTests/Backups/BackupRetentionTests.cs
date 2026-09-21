using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.UnitTests.Backups;

public class BackupRetentionTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithinRetention_NothingIsPruned()
    {
        var records = Successes(3);

        Assert.Empty(BackupRetention.SelectForPruning(records, 3));
        Assert.Empty(BackupRetention.SelectForPruning(records, 10));
    }

    [Fact]
    public void BeyondRetention_TheOldestArePrunedOldestFirst()
    {
        var records = Successes(5);

        var prune = BackupRetention.SelectForPruning(records, 2);

        Assert.Equal([1, 2, 3], prune.Select(r => r.Id));
    }

    [Fact]
    public void ManualAndScheduledCountAlike()
    {
        var records = Successes(4);
        records[3].IsManual = true; // the newest is manual
        records[0].IsManual = true; // the oldest is manual

        var prune = BackupRetention.SelectForPruning(records, 2);

        Assert.Equal([1, 2], prune.Select(r => r.Id));
    }

    [Fact]
    public void SkippedAndFailedRecords_NeverCountAndAreNeverPruned()
    {
        var records = Successes(3);
        records.Add(new BackupRecord { Id = 10, CreatedAt = _start.AddHours(10), Outcome = BackupOutcome.Skipped, Reason = "RCON unreachable" });
        records.Add(new BackupRecord { Id = 11, CreatedAt = _start.AddHours(11), Outcome = BackupOutcome.Failed, Reason = "verification" });

        var prune = BackupRetention.SelectForPruning(records, 2);

        Assert.Equal([1], prune.Select(r => r.Id));
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        var records = Successes(4);
        records.Reverse();

        var prune = BackupRetention.SelectForPruning(records, 1);

        Assert.Equal([1, 2, 3], prune.Select(r => r.Id));
    }

    [Fact]
    public void RetentionBelowOne_KeepsTheNewest()
    {
        var prune = BackupRetention.SelectForPruning(Successes(2), 0);

        Assert.Equal([1], prune.Select(r => r.Id));
    }

    [Fact]
    public void WithinTheUnsuccessfulCap_NothingIsPruned()
    {
        var records = Unsuccessful(BackupRetention.UnsuccessfulRecordsKept);

        Assert.Empty(BackupRetention.SelectUnsuccessfulForPruning(records));
    }

    [Fact]
    public void BeyondTheUnsuccessfulCap_TheOldestArePrunedOldestFirst()
    {
        var records = Unsuccessful(BackupRetention.UnsuccessfulRecordsKept + 3);
        records.Reverse();

        var prune = BackupRetention.SelectUnsuccessfulForPruning(records);

        Assert.Equal([1, 2, 3], prune.Select(r => r.Id));
    }

    [Fact]
    public void SuccessfulRecords_NeverCountTowardTheUnsuccessfulCap()
    {
        var records = Unsuccessful(2);
        var successes = Successes(3);
        foreach (var success in successes)
        {
            success.Id += 100;
        }

        records.AddRange(successes);

        var prune = BackupRetention.SelectUnsuccessfulForPruning(records, keep: 1);

        Assert.Equal([1], prune.Select(r => r.Id));
    }

    [Fact]
    public void SkippedAndFailedCountAlike()
    {
        var records = Unsuccessful(3);
        records[1].Outcome = BackupOutcome.Failed;

        var prune = BackupRetention.SelectUnsuccessfulForPruning(records, keep: 1);

        Assert.Equal([1, 2], prune.Select(r => r.Id));
    }

    [Fact]
    public void AKeepBelowZero_PrunesEverythingUnsuccessful()
    {
        var prune = BackupRetention.SelectUnsuccessfulForPruning(Unsuccessful(2), keep: -1);

        Assert.Equal([1, 2], prune.Select(r => r.Id));
    }

    private static List<BackupRecord> Unsuccessful(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new BackupRecord { Id = i, InstanceId = 1, CreatedAt = _start.AddHours(i), Outcome = BackupOutcome.Skipped, Reason = $"reason {i}" })
            .ToList();

    private static List<BackupRecord> Successes(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new BackupRecord { Id = i, InstanceId = 1, CreatedAt = _start.AddHours(i), Outcome = BackupOutcome.Success, FileName = $"{i}.zip", SizeBytes = 100 })
            .ToList();
}
