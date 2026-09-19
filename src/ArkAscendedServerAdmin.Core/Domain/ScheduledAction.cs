namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One daily action at a local time of day (B3). Owned by exactly one instance or one cluster (a check
/// constraint enforces it); a cluster's rows apply to every member instance unless the instance sets
/// <see cref="Instance.OverridesClusterSchedule"/>. Each day's occurrence is an explicit instant computed by
/// <see cref="Scheduling.ScheduleOccurrences"/>, so a clock change never shifts or repeats an action.
/// </summary>
public sealed class ScheduledAction
{
    public int Id { get; set; }

    public int? InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public int? ClusterId { get; set; }

    public Cluster? Cluster { get; set; }

    /// <summary>Minutes since local midnight, 0–1439.</summary>
    public int TimeOfDay { get; set; }

    public ScheduledActionKind Kind { get; set; }

    /// <summary>The RCON command text for <see cref="ScheduledActionKind.RconCommand"/>; empty for the other kinds.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Countdown length before the deadline, 0–60. Ignored for <see cref="ScheduledActionKind.RconCommand"/>,
    /// whose effective warning is always zero.
    /// </summary>
    public int WarningMinutes { get; set; } = 10;

    public bool Enabled { get; set; } = true;

    public List<ScheduledActionRun> Runs { get; } = [];
}
