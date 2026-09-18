namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One restore attempt that reached the destructive phase, or a recovery from a safety copy (B2). Kept apart from
/// <see cref="BackupRecord"/> on purpose: the backup scheduler takes the newest backup record as its cadence marker
/// and retention counts successful backups, and a restore must move neither. Capped at fifty rows per instance.
/// </summary>
public sealed class RestoreRecord
{
    public int Id { get; set; }

    public int InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The archive under <c>Backups\&lt;slug&gt;\</c> the restore was taken from.</summary>
    public required string SourceFileName { get; set; }

    /// <summary>True when the cluster directory was replaced as well.</summary>
    public bool IncludedCluster { get; set; }

    public RestoreOutcome Outcome { get; set; }

    /// <summary>Why the restore failed or was rolled back; the safety-copy path for a recovery.</summary>
    public string? Reason { get; set; }
}
