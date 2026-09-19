using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Scheduling;

namespace ArkAscendedServerAdmin.UnitTests.Scheduling;

/// <summary>
/// B3 occurrence rules against a fixed zone (US Eastern, never the machine's): the spring-forward gap
/// skips with a reason, the fall-back overlap takes the earlier instant, a warning may cross midnight,
/// and the applicability and next-deadline rules.
/// </summary>
public class ScheduleOccurrencesTests
{
    private static readonly TimeZoneInfo _eastern = FindEastern();

    private static readonly TimeSpan _edt = TimeSpan.FromHours(-4);

    private static readonly TimeSpan _est = TimeSpan.FromHours(-5);

    // ---------------------------------------------------------------- EffectiveWarningMinutes

    [Fact]
    public void EffectiveWarningMinutes_RconCommand_IsZeroWhateverTheColumnSays()
    {
        Assert.Equal(0, ScheduleOccurrences.EffectiveWarningMinutes(Action(1, 600, ScheduledActionKind.RconCommand, warning: 10)));
    }

    [Theory]
    [InlineData(ScheduledActionKind.Restart)]
    [InlineData(ScheduledActionKind.DinoWipe)]
    public void EffectiveWarningMinutes_CountdownKinds_UseTheColumn(ScheduledActionKind kind)
    {
        Assert.Equal(15, ScheduleOccurrences.EffectiveWarningMinutes(Action(1, 600, kind, warning: 15)));
    }

    // ---------------------------------------------------------------- ForDate

    [Fact]
    public void ForDate_OrdinaryDay_DeadlineAtLocalTimeAndDueOneWarningEarlier()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 20 * 60, warning: 10), new DateOnly(2026, 6, 15), _eastern);

        Assert.False(occurrence.IsSkipped);
        Assert.Null(occurrence.SkipReason);
        Assert.Equal(new DateOnly(2026, 6, 15), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 20, 0, 0, _edt), occurrence.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 19, 50, 0, _edt), occurrence.Due);
    }

    [Fact]
    public void ForDate_RconCommand_DueEqualsDeadline()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 20 * 60, ScheduledActionKind.RconCommand, warning: 10), new DateOnly(2026, 6, 15), _eastern);

        Assert.Equal(occurrence.Deadline, occurrence.Due);
    }

    [Fact]
    public void ForDate_SpringForwardGap_SkipsWithReason()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 2 * 60 + 30), new DateOnly(2026, 3, 8), _eastern);

        Assert.True(occurrence.IsSkipped);
        Assert.Null(occurrence.Deadline);
        Assert.Null(occurrence.Due);
        Assert.Equal("The local time 02:30 does not exist on 2026-03-08 (clocks moved forward).", occurrence.SkipReason);
    }

    [Fact]
    public void ForDate_SameTimeTheDayAfterTheGap_IsAnOrdinaryOccurrence()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 2 * 60 + 30), new DateOnly(2026, 3, 9), _eastern);

        Assert.False(occurrence.IsSkipped);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 2, 30, 0, _edt), occurrence.Deadline);
    }

    [Fact]
    public void ForDate_FallBackOverlap_TakesTheEarlierInstant()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 60 + 30, warning: 0), new DateOnly(2026, 11, 1), _eastern);

        Assert.False(occurrence.IsSkipped);
        Assert.Equal(_edt, occurrence.Deadline!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt), occurrence.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), occurrence.Deadline!.Value.ToUniversalTime());
    }

    [Fact]
    public void ForDate_AfterTheOverlap_UsesStandardTime()
    {
        var occurrence = ScheduleOccurrences.ForDate(Action(1, 3 * 60), new DateOnly(2026, 11, 1), _eastern);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, _est), occurrence.Deadline);
    }

    // ---------------------------------------------------------------- DueInMinute

    [Fact]
    public void DueInMinute_WarningCrossingMidnight_MatchesOnThePreviousLocalDatesTick()
    {
        var midnightRestart = Action(1, 0, warning: 10);
        var tick = new DateTimeOffset(2026, 6, 14, 23, 50, 0, _edt);

        var due = ScheduleOccurrences.DueInMinute([midnightRestart], tick, _eastern);

        var (action, occurrence) = Assert.Single(due);
        Assert.Same(midnightRestart, action);
        Assert.Equal(new DateOnly(2026, 6, 15), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 0, 0, 0, _edt), occurrence.Deadline);
        Assert.Equal(tick, occurrence.Due);
    }

    [Fact]
    public void DueInMinute_IgnoresSecondsOfTheTick()
    {
        var tick = new DateTimeOffset(2026, 6, 15, 19, 50, 37, _edt);

        var due = ScheduleOccurrences.DueInMinute([Action(1, 20 * 60, warning: 10)], tick, _eastern);

        Assert.Single(due);
    }

    [Fact]
    public void DueInMinute_TheMinuteBeforeAndAfter_ReturnsNothing()
    {
        ScheduledAction[] rows = [Action(1, 20 * 60, warning: 10)];

        Assert.Empty(ScheduleOccurrences.DueInMinute(rows, new DateTimeOffset(2026, 6, 15, 19, 49, 0, _edt), _eastern));
        Assert.Empty(ScheduleOccurrences.DueInMinute(rows, new DateTimeOffset(2026, 6, 15, 19, 51, 0, _edt), _eastern));
    }

    [Fact]
    public void DueInMinute_ReturnsEachDueRowOnceInInputOrder()
    {
        var first = Action(1, 20 * 60, warning: 10);
        var second = Action(2, 19 * 60 + 50, ScheduledActionKind.RconCommand);
        var later = Action(3, 21 * 60, warning: 10);
        var tick = new DateTimeOffset(2026, 6, 15, 19, 50, 0, _edt);

        var due = ScheduleOccurrences.DueInMinute([first, second, later], tick, _eastern);

        Assert.Equal([1, 2], due.Select(d => d.Action.Id));
        Assert.All(due, d => Assert.Equal(tick, d.Occurrence.Due));
    }

    [Fact]
    public void DueInMinute_UtcTickMatchesTheSameInstant()
    {
        var tick = new DateTimeOffset(2026, 6, 15, 23, 50, 0, TimeSpan.Zero);

        var due = ScheduleOccurrences.DueInMinute([Action(1, 20 * 60, warning: 10)], tick, _eastern);

        Assert.Single(due);
    }

    [Fact]
    public void DueInMinute_GapRow_ReportedOnceOnTheFirstTickPastTheGap()
    {
        var row = Action(1, 2 * 60 + 30, warning: 10);
        var firstValidMinute = new DateTimeOffset(2026, 3, 8, 3, 0, 0, _edt);

        var reported = new List<DateTimeOffset>();
        for (var tick = firstValidMinute.AddMinutes(-40); tick <= firstValidMinute.AddMinutes(40); tick = tick.AddMinutes(1))
        {
            var due = ScheduleOccurrences.DueInMinute([row], tick, _eastern);
            if (due.Count > 0)
            {
                reported.Add(tick);
                var (_, occurrence) = Assert.Single(due);
                Assert.True(occurrence.IsSkipped);
                Assert.Equal(new DateOnly(2026, 3, 8), occurrence.LocalDate);
                Assert.Equal("The local time 02:30 does not exist on 2026-03-08 (clocks moved forward).", occurrence.SkipReason);
            }
        }

        Assert.Equal([firstValidMinute], reported);
    }

    [Fact]
    public void DueInMinute_GapRow_IsNotReportedOnTheDayBeforeOrAfter()
    {
        var row = Action(1, 2 * 60 + 30, warning: 0);

        var dayBefore = ScheduleOccurrences.DueInMinute([row], new DateTimeOffset(2026, 3, 7, 2, 30, 0, _est), _eastern);
        var dayAfter = ScheduleOccurrences.DueInMinute([row], new DateTimeOffset(2026, 3, 9, 2, 30, 0, _edt), _eastern);

        Assert.False(Assert.Single(dayBefore).Occurrence.IsSkipped);
        Assert.False(Assert.Single(dayAfter).Occurrence.IsSkipped);
    }

    [Fact]
    public void DueInMinute_FallBackOverlap_FiresOnTheEarlierInstantOnly()
    {
        var row = Action(1, 60 + 30, warning: 0);
        var earlier = new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt);
        var later = new DateTimeOffset(2026, 11, 1, 1, 30, 0, _est);

        Assert.Single(ScheduleOccurrences.DueInMinute([row], earlier, _eastern));
        Assert.Empty(ScheduleOccurrences.DueInMinute([row], later, _eastern));
    }

    // ---------------------------------------------------------------- NextDeadline

    [Fact]
    public void NextDeadline_TodayStillAhead_PicksToday()
    {
        var now = new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, 8 * 60)], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_TodayAlreadyPast_RollsToTomorrow()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, 8 * 60)], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_ExactlyNow_IsNotStrictlyAfterSoRollsToTomorrow()
    {
        var now = new DateTimeOffset(2026, 6, 15, 8, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, 8 * 60)], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_PicksTheEarliestAcrossRows()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, 8 * 60), Action(2, 22 * 60), Action(3, 12 * 60)], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_SkipsTheGapDay()
    {
        var now = new DateTimeOffset(2026, 3, 8, 1, 0, 0, _est);

        var next = ScheduleOccurrences.NextDeadline([Action(1, 2 * 60 + 30)], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 2, 30, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_NoRows_IsNull()
    {
        Assert.Null(ScheduleOccurrences.NextDeadline([], new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt), _eastern));
    }

    // ---------------------------------------------------------------- Applicable

    [Fact]
    public void Applicable_OwnRowsPlusClusterRows_OrderedById()
    {
        var instance = Instance(10, clusterId: 5);
        ScheduledAction[] all =
        [
            Action(7, 600, instanceId: 10),
            Action(3, 600, clusterId: 5),
            Action(9, 600, clusterId: 6),
            Action(1, 600, instanceId: 11),
            Action(5, 600, clusterId: 5),
        ];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([3, 5, 7], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_OverridesClusterSchedule_DropsTheClusterRows()
    {
        var instance = Instance(10, clusterId: 5, overridesClusterSchedule: true);
        ScheduledAction[] all = [Action(3, 600, clusterId: 5), Action(7, 600, instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([7], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_DisabledRows_AreLeftOut()
    {
        var instance = Instance(10, clusterId: 5);
        ScheduledAction[] all = [Action(3, 600, clusterId: 5, enabled: false), Action(7, 600, instanceId: 10, enabled: false), Action(8, 600, instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([8], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_StandaloneInstance_SeesOnlyItsOwnRows()
    {
        var instance = Instance(10, clusterId: null);
        ScheduledAction[] all = [Action(3, 600, clusterId: 5), Action(7, 600, instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([7], applicable.Select(a => a.Id));
    }

    // ---------------------------------------------------------------- helpers

    private static TimeZoneInfo FindEastern()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }

    private static ScheduledAction Action(
        int id,
        int timeOfDay,
        ScheduledActionKind kind = ScheduledActionKind.Restart,
        int warning = 10,
        int? instanceId = 10,
        int? clusterId = null,
        bool enabled = true) =>
        new()
        {
            Id = id,
            InstanceId = clusterId is null ? instanceId : null,
            ClusterId = clusterId,
            TimeOfDay = timeOfDay,
            Kind = kind,
            Command = kind == ScheduledActionKind.RconCommand ? "saveworld" : string.Empty,
            WarningMinutes = warning,
            Enabled = enabled,
        };

    private static Instance Instance(int id, int? clusterId, bool overridesClusterSchedule = false) =>
        new()
        {
            Id = id,
            Name = $"Instance {id}",
            Slug = $"instance-{id}",
            SessionName = $"Instance {id}",
            ClusterId = clusterId,
            OverridesClusterSchedule = overridesClusterSchedule,
        };
}
