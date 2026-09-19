namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One occurrence's run of a <see cref="ScheduledAction"/> on one instance (B3). Inserting the row is the
/// atomic claim: the unique index on (action, instance, scheduled instant) means a cluster row fans out to one
/// run per member instance, a row that fires several times a day gets one run per occurrence, and a second
/// tick in the same minute inserts nothing. Skipped and failed runs carry a reason so a missed action is
/// visible, never silent. Rows older than thirty days are pruned by the runner.
/// </summary>
public sealed class ScheduledActionRun
{
    public int Id { get; set; }

    public int ScheduledActionId { get; set; }

    public ScheduledAction? ScheduledAction { get; set; }

    public int InstanceId { get; set; }

    public Instance? Instance { get; set; }

    /// <summary>The occurrence's deadline: the instant the action itself happens, one warning countdown after <see cref="StartedAt"/>.</summary>
    public DateTimeOffset ScheduledFor { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the operation returned; null while <see cref="Outcome"/> is <see cref="ScheduledActionOutcome.Started"/>.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public ScheduledActionOutcome Outcome { get; set; }

    /// <summary>Why the run was skipped, failed, or interrupted; empty when there is nothing to say.</summary>
    public string Reason { get; set; } = string.Empty;
}
