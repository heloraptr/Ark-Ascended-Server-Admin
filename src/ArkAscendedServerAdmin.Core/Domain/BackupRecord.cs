namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One backup attempt. Successful attempts name the archive under <c>Backups\&lt;instance&gt;\</c>;
/// skipped and failed attempts are recorded with a reason so a missed schedule is visible, never silent.
/// </summary>
public sealed class BackupRecord
{
    public int Id { get; set; }

    public int InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public BackupOutcome Outcome { get; set; }

    /// <summary>Archive file name relative to the instance's backup directory; null unless successful.</summary>
    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>Why the attempt was skipped or failed.</summary>
    public string? Reason { get; set; }

    /// <summary>Manual backups count toward retention exactly like scheduled ones.</summary>
    public bool IsManual { get; set; }
}
