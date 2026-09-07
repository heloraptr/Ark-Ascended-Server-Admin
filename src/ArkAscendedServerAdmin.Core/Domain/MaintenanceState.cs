namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// The single persisted row driving install/update recovery across service restarts (plan steps 11, 29).
/// <see cref="Entries"/> lists the instances the current phase is acting on and which of them are done.
/// </summary>
public sealed class MaintenanceState
{
    /// <summary>There is exactly one row; its id is always <see cref="SingletonId"/>.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public MaintenancePhase Phase { get; set; } = MaintenancePhase.None;

    public List<MaintenanceEntry> Entries { get; set; } = [];

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsResolved => Phase == MaintenancePhase.None && Entries.Count == 0;
}

/// <summary>Per-instance progress inside a maintenance phase; stored as JSON on the state row.</summary>
public sealed record MaintenanceEntry(int InstanceId, bool Done = false, string? Error = null);
