using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>
/// One row of the schedule editor as the pages read it (B3). Exactly one of <paramref name="OwnerInstanceId"/>
/// and <paramref name="OwnerClusterId"/> is set. <paramref name="Inherited"/> is true on an instance's list for
/// a row that comes from its cluster: the instance page shows it locked and cannot save it.
/// </summary>
public sealed record ScheduledActionView(
    int Id,
    int? OwnerInstanceId,
    int? OwnerClusterId,
    int TimeOfDay,
    ScheduledActionKind Kind,
    string Command,
    int WarningMinutes,
    bool Enabled,
    bool Inherited);

/// <summary>
/// One row of the schedule editor as it saves it (B3). <paramref name="Id"/> zero inserts a row; a known id
/// updates that row in place, so its runs keep pointing at it.
/// </summary>
public sealed record ScheduledActionEdit(
    int Id,
    int TimeOfDay,
    ScheduledActionKind Kind,
    string Command,
    int WarningMinutes,
    bool Enabled);

/// <summary>
/// One run in the "last runs" list (B3). <paramref name="Kind"/> is the action's, so the list can say what
/// ran; <paramref name="InstanceName"/> tells a cluster's list which member the run belongs to.
/// </summary>
public sealed record ScheduledActionRunView(
    int Id,
    int ScheduledActionId,
    int InstanceId,
    string InstanceName,
    DateOnly LocalDate,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    ScheduledActionOutcome Outcome,
    string Reason,
    ScheduledActionKind Kind);
