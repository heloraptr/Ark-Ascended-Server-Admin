using System.Diagnostics.CodeAnalysis;
using ArkAscendedServerAdmin.Domain;
using Cronos;

namespace ArkAscendedServerAdmin.Scheduling;

/// <summary>
/// One occurrence of a <see cref="ScheduledAction"/>: the absolute instant the action happens
/// (<paramref name="Deadline"/>) and the instant the countdown begins (<paramref name="Due"/>, the deadline
/// less the effective warning). Both carry the schedule zone's offset.
/// </summary>
public sealed record ScheduleOccurrence(DateTimeOffset Deadline, DateTimeOffset Due);

/// <summary>
/// Turns scheduled actions' cron expressions into explicit instants (B3) with Cronos. Pure: the zone is
/// always a parameter, never <see cref="TimeZoneInfo.Local"/>. Clock changes follow Cronos' rules: an
/// occurrence that falls in the spring-forward gap fires once, right after the transition (a 02:30 job fires
/// at 03:00); a fixed-time occurrence in the fall-back overlap fires once, at the earlier instant; an
/// expression with an interval in its hour field (<c>*/30 * * * *</c>, <c>30 * * * *</c>) fires in both
/// repeated hours. The runner ticks once a minute and asks <see cref="DueInMinute"/> which rows fire.
/// </summary>
public static class ScheduleOccurrences
{
    private static readonly TimeSpan _oneMinute = TimeSpan.FromMinutes(1);

    /// <summary>Parses a standard five-field cron expression (surrounding whitespace ignored); false, with a null <paramref name="expression"/>, when <paramref name="cron"/> is not one.</summary>
    public static bool TryParse(string? cron, [NotNullWhen(true)] out CronExpression? expression)
    {
        if (!string.IsNullOrWhiteSpace(cron) && CronExpression.TryParse(cron.Trim(), out var parsed))
        {
            expression = parsed;
            return true;
        }

        expression = null;
        return false;
    }

    /// <summary>The countdown length the runner uses: zero for <see cref="ScheduledActionKind.RconCommand"/>, else <see cref="ScheduledAction.WarningMinutes"/>.</summary>
    public static int EffectiveWarningMinutes(ScheduledAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind == ScheduledActionKind.RconCommand ? 0 : action.WarningMinutes;
    }

    /// <summary>The first deadline of <paramref name="action"/> strictly after <paramref name="after"/> in <paramref name="zone"/>; null when its expression does not parse or never fires again.</summary>
    public static DateTimeOffset? NextDeadline(ScheduledAction action, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(zone);
        return TryParse(action.Cron, out var expression) ? expression.GetNextOccurrence(after, zone) : null;
    }

    /// <summary>Up to <paramref name="count"/> deadlines of <paramref name="action"/> strictly after <paramref name="after"/>, ascending; fewer when the expression stops firing, none when it does not parse.</summary>
    public static IReadOnlyList<DateTimeOffset> NextDeadlines(ScheduledAction action, DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryParse(action.Cron, out var expression) ? NextDeadlines(expression, after, zone, count) : [];
    }

    /// <summary>Up to <paramref name="count"/> occurrences of <paramref name="expression"/> strictly after <paramref name="after"/>, ascending; fewer when the expression stops firing.</summary>
    public static IReadOnlyList<DateTimeOffset> NextDeadlines(CronExpression expression, DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var deadlines = new List<DateTimeOffset>(count);
        var from = after;
        while (deadlines.Count < count && expression.GetNextOccurrence(from, zone) is { } next)
        {
            deadlines.Add(next);
            from = next;
        }

        return deadlines;
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
    /// each with its occurrence, in the order of <paramref name="applicable"/>. A row is due when its first
    /// deadline after (tick + warning − 1 minute) is exactly tick + warning. Starting the search one minute
    /// before the candidate deadline is what makes an occurrence shifted out of the spring-forward gap visible:
    /// Cronos reports the shifted instant only to a search that begins before the transition. A row whose
    /// expression does not parse is ignored.
    /// </summary>
    public static IReadOnlyList<(ScheduledAction Action, ScheduleOccurrence Occurrence)> DueInMinute(
        IEnumerable<ScheduledAction> applicable,
        DateTimeOffset tickMinute,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(applicable);
        ArgumentNullException.ThrowIfNull(zone);
        var tick = TruncateToMinute(tickMinute);

        var due = new List<(ScheduledAction, ScheduleOccurrence)>();
        foreach (var action in applicable)
        {
            if (!TryParse(action.Cron, out var expression))
            {
                continue;
            }

            var warning = TimeSpan.FromMinutes(EffectiveWarningMinutes(action));
            var candidate = tick + warning;
            if (expression.GetNextOccurrence(candidate - _oneMinute, zone) is { } deadline && deadline == candidate)
            {
                due.Add((action, new ScheduleOccurrence(deadline, deadline - warning)));
            }
        }

        return due;
    }

    /// <summary>The earliest deadline strictly after <paramref name="now"/> across <paramref name="applicable"/>; null when no row fires again.</summary>
    public static DateTimeOffset? NextDeadline(IEnumerable<ScheduledAction> applicable, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(applicable);
        ArgumentNullException.ThrowIfNull(zone);

        DateTimeOffset? next = null;
        foreach (var action in applicable)
        {
            if (NextDeadline(action, now, zone) is { } deadline && (next is null || deadline < next))
            {
                next = deadline;
            }
        }

        return next;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset at) => at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMinute));
}
