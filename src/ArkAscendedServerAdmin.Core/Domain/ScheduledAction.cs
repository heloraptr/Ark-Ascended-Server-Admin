namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One recurring action on a cron schedule (B3). Owned by exactly one instance or one cluster (a check
/// constraint enforces it); a cluster's rows apply to every member instance unless the instance sets
/// <see cref="Instance.OverridesClusterSchedule"/>. Each occurrence is an explicit instant computed by
/// <see cref="Scheduling.ScheduleOccurrences"/> from <see cref="Cron"/> in the host's local zone.
/// </summary>
public sealed class ScheduledAction
{
    public int Id { get; set; }

    public int? InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public int? ClusterId { get; set; }

    public Cluster? Cluster { get; set; }

    /// <summary>A standard five-field cron expression (minute, hour, day of month, month, day of week) read in the host's local zone.</summary>
    public string Cron { get; set; } = string.Empty;

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
