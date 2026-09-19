using System.Globalization;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Scheduling;

/// <summary>
/// One local calendar day's occurrence of a <see cref="ScheduledAction"/>. The summary names the local day
/// (<paramref name="LocalDate"/>), the absolute instant the action happens (<paramref name="Deadline"/>), the
/// instant the countdown begins (<paramref name="Due"/>, the deadline less the effective warning), and, when
/// the local time does not exist that day, why the occurrence is skipped (<paramref name="SkipReason"/>, with
/// both instants null).
/// </summary>
public sealed record ScheduleOccurrence(DateOnly LocalDate, DateTimeOffset? Deadline, DateTimeOffset? Due, string? SkipReason)
{
    /// <summary>True when the local time did not exist that day (spring-forward gap).</summary>
    public bool IsSkipped => SkipReason is not null;
}

/// <summary>
/// Turns scheduled actions into explicit instants (B3). Pure: the zone is always a parameter, never
/// <see cref="TimeZoneInfo.Local"/>. A local time that does not exist on a day (the spring-forward gap) is
/// skipped for that day with a reason; a local time that exists twice (the fall-back overlap) uses the
/// earlier instant. The runner ticks once a minute and asks <see cref="DueInMinute"/> which rows fire.
/// </summary>
public static class ScheduleOccurrences
{
    private static readonly TimeSpan _oneMinute = TimeSpan.FromMinutes(1);

    /// <summary>The countdown length the runner uses: zero for <see cref="ScheduledActionKind.RconCommand"/>, else <see cref="ScheduledAction.WarningMinutes"/>.</summary>
    public static int EffectiveWarningMinutes(ScheduledAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind == ScheduledActionKind.RconCommand ? 0 : action.WarningMinutes;
    }

    /// <summary>The occurrence of <paramref name="action"/> on <paramref name="localDate"/> in <paramref name="zone"/>; see the class remarks for the clock-change rules.</summary>
    public static ScheduleOccurrence ForDate(ScheduledAction action, DateOnly localDate, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(zone);
        var wallClock = WallClock(action, localDate);
        if (zone.IsInvalidTime(wallClock))
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"The local time {wallClock:HH:mm} does not exist on {localDate:yyyy-MM-dd} (clocks moved forward).");
            return new ScheduleOccurrence(localDate, null, null, reason);
        }

        // In the overlap the larger offset (daylight time) is the earlier instant.
        var offset = zone.IsAmbiguousTime(wallClock)
            ? zone.GetAmbiguousTimeOffsets(wallClock).Max()
            : zone.GetUtcOffset(wallClock);
        var deadline = new DateTimeOffset(wallClock, offset);
        var due = deadline - TimeSpan.FromMinutes(EffectiveWarningMinutes(action));
        return new ScheduleOccurrence(localDate, deadline, due, null);
    }

    /// <summary>
    /// The enabled rows that apply to <paramref name="instance"/>: its own, plus its cluster's unless
    /// <see cref="Instance.OverridesClusterSchedule"/> is set; ordered by <see cref="ScheduledAction.Id"/>,
    /// which is the order two rows due in the same minute run in.
    /// </summary>
    public static IReadOnlyList<ScheduledAction> Applicable(Instance instance, IEnumerable<ScheduledAction> all)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(all);
        var inheritsCluster = !instance.OverridesClusterSchedule && instance.ClusterId is not null;

        return all
            .Where(a => a.Enabled)
            .Where(a => a.InstanceId == instance.Id || (inheritsCluster && a.ClusterId == instance.ClusterId))
            .OrderBy(a => a.Id)
            .ToList();
    }

    /// <summary>
    /// The rows whose due instant falls in the minute of <paramref name="tickMinute"/> (seconds are ignored),
    /// each at most once, in the order of <paramref name="applicable"/>. Yesterday, today, and tomorrow in
    /// <paramref name="zone"/> are all evaluated so a warning that crosses midnight still matches. A gap
    /// occurrence (<see cref="ScheduleOccurrence.IsSkipped"/>) has no due instant; it is reported exactly once,
    /// on the first tick whose local wall clock is past the missing time — that is, when the previous minute's
    /// wall clock was before the scheduled time and this minute's is after it. Because the missing time never
    /// equals a wall clock, no earlier or later tick can satisfy that, and the runner writes one skipped run
    /// for the day.
    /// </summary>
    public static IReadOnlyList<(ScheduledAction Action, ScheduleOccurrence Occurrence)> DueInMinute(
        IEnumerable<ScheduledAction> applicable,
        DateTimeOffset tickMinute,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(applicable);
        ArgumentNullException.ThrowIfNull(zone);
        var tick = TruncateToMinute(tickMinute);
        var tickWallClock = TimeZoneInfo.ConvertTime(tick, zone).DateTime;
        var previousWallClock = TimeZoneInfo.ConvertTime(tick - _oneMinute, zone).DateTime;
        var today = DateOnly.FromDateTime(tickWallClock);
        DateOnly[] dates = [today.AddDays(-1), today, today.AddDays(1)];

        var due = new List<(ScheduledAction, ScheduleOccurrence)>();
        foreach (var action in applicable)
        {
            foreach (var date in dates)
            {
                var occurrence = ForDate(action, date, zone);
                var matches = occurrence.Due is { } dueAt
                    ? TruncateToMinute(dueAt) == tick
                    : IsFirstTickPastGap(WallClock(action, date), previousWallClock, tickWallClock);
                if (matches)
                {
                    due.Add((action, occurrence));
                    break;
                }
            }
        }

        return due;
    }

    /// <summary>The earliest deadline strictly after <paramref name="now"/> over today and tomorrow in <paramref name="zone"/>, skipping gaps; null when there is none.</summary>
    public static DateTimeOffset? NextDeadline(IEnumerable<ScheduledAction> applicable, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(applicable);
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateOnly[] dates = [today, today.AddDays(1)];

        DateTimeOffset? next = null;
        foreach (var action in applicable)
        {
            foreach (var date in dates)
            {
                if (ForDate(action, date, zone).Deadline is { } deadline && deadline > now && (next is null || deadline < next))
                {
                    next = deadline;
                }
            }
        }

        return next;
    }

    private static DateTime WallClock(ScheduledAction action, DateOnly localDate) =>
        localDate.ToDateTime(new TimeOnly(action.TimeOfDay / 60, action.TimeOfDay % 60), DateTimeKind.Unspecified);

    private static bool IsFirstTickPastGap(DateTime scheduled, DateTime previousWallClock, DateTime tickWallClock) =>
        previousWallClock < scheduled && scheduled < tickWallClock;

    private static DateTimeOffset TruncateToMinute(DateTimeOffset at) => at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMinute));
}
