using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Scheduling;

namespace ArkAscendedServerAdmin.UnitTests.Scheduling;

/// <summary>
/// B3 occurrence rules on Cronos against a fixed zone (US Eastern, never the machine's): parsing, the
/// spring-forward gap (fires once, right after the transition), the fall-back overlap (a fixed time fires
/// once at the earlier instant, an interval fires in both hours), several occurrences a day matched once
/// each, a warning that crosses midnight, and the applicability and next-deadline rules.
/// </summary>
public class ScheduleOccurrencesTests
{
    private static readonly TimeZoneInfo _eastern = FindEastern();

    private static readonly TimeSpan _edt = TimeSpan.FromHours(-4);

    private static readonly TimeSpan _est = TimeSpan.FromHours(-5);

    // ---------------------------------------------------------------- TryParse

    [Theory]
    [InlineData("0 3 * * *")]
    [InlineData("*/30 * * * *")]
    [InlineData("0 3 * * 1-5")]
    [InlineData("  0 3 * * *  ")]
    public void TryParse_FiveFieldExpression_Parses(string cron)
    {
        Assert.True(ScheduleOccurrences.TryParse(cron, out var expression));
        Assert.NotNull(expression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("60 3 * * *")]
    [InlineData("0 3 * * * *")]
    public void TryParse_AnythingElse_Fails(string? cron)
    {
        Assert.False(ScheduleOccurrences.TryParse(cron, out var expression));
        Assert.Null(expression);
    }

    // ---------------------------------------------------------------- EffectiveWarningMinutes

    [Fact]
    public void EffectiveWarningMinutes_RconCommand_IsZeroWhateverTheColumnSays()
    {
        Assert.Equal(0, ScheduleOccurrences.EffectiveWarningMinutes(Action(1, "0 10 * * *", ScheduledActionKind.RconCommand, warning: 10)));
    }

    [Theory]
    [InlineData(ScheduledActionKind.Restart)]
    [InlineData(ScheduledActionKind.DinoWipe)]
    public void EffectiveWarningMinutes_CountdownKinds_UseTheColumn(ScheduledActionKind kind)
    {
        Assert.Equal(15, ScheduleOccurrences.EffectiveWarningMinutes(Action(1, "0 10 * * *", kind, warning: 15)));
    }

    // ---------------------------------------------------------------- NextDeadline / NextDeadlines (one action)

    [Fact]
    public void NextDeadline_OrdinaryDay_IsTheNextWallClockInstant()
    {
        var next = ScheduleOccurrences.NextDeadline(Action(1, "0 20 * * *"), new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt), _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 20, 0, 0, _edt), next);
        Assert.Equal(_edt, next!.Value.Offset);
    }

    [Fact]
    public void NextDeadline_ExactlyAtTheOccurrence_IsStrictlyAfterSoRollsToTheNext()
    {
        var next = ScheduleOccurrences.NextDeadline(Action(1, "0 20 * * *"), new DateTimeOffset(2026, 6, 15, 20, 0, 0, _edt), _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 20, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_UnparsableExpression_IsNull()
    {
        Assert.Null(ScheduleOccurrences.NextDeadline(Action(1, "bogus"), new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt), _eastern));
        Assert.Empty(ScheduleOccurrences.NextDeadlines(Action(1, "bogus"), new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt), _eastern, 3));
    }

    [Fact]
    public void NextDeadline_ExpressionThatNeverFires_IsNull()
    {
        var after = new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt);

        Assert.Null(ScheduleOccurrences.NextDeadline(Action(1, "0 0 30 2 *"), after, _eastern));
        Assert.Empty(ScheduleOccurrences.NextDeadlines(Action(1, "0 0 30 2 *"), after, _eastern, 3));
    }

    [Fact]
    public void NextDeadlines_SpringForwardGap_FiresOnceRightAfterTheTransition()
    {
        // 02:30 does not exist on 2026-03-08; Cronos moves that one occurrence to 03:00 EDT.
        var deadlines = ScheduleOccurrences.NextDeadlines(Action(1, "30 2 * * *"), new DateTimeOffset(2026, 3, 8, 0, 0, 0, _est), _eastern, 3);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 3, 8, 3, 0, 0, _edt),
                new DateTimeOffset(2026, 3, 9, 2, 30, 0, _edt),
                new DateTimeOffset(2026, 3, 10, 2, 30, 0, _edt),
            ],
            deadlines);
        Assert.Equal(_edt, deadlines[0].Offset);
    }

    [Fact]
    public void NextDeadlines_FallBackOverlap_FixedTimeFiresOnceAtTheEarlierInstant()
    {
        // 01:30 happens twice on 2026-11-01; Cronos fires the daylight-time one and not the standard-time one.
        var deadlines = ScheduleOccurrences.NextDeadlines(Action(1, "30 1 * * *"), new DateTimeOffset(2026, 11, 1, 0, 0, 0, _edt), _eastern, 3);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt),
                new DateTimeOffset(2026, 11, 2, 1, 30, 0, _est),
                new DateTimeOffset(2026, 11, 3, 1, 30, 0, _est),
            ],
            deadlines);
        Assert.Equal(_edt, deadlines[0].Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), deadlines[0].ToUniversalTime());
    }

    [Fact]
    public void NextDeadlines_FallBackOverlap_IntervalFiresInBothHours()
    {
        var deadlines = ScheduleOccurrences.NextDeadlines(Action(1, "*/30 * * * *"), new DateTimeOffset(2026, 11, 1, 0, 45, 0, _edt), _eastern, 5);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 1, 1, 0, 0, _edt),
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt),
                new DateTimeOffset(2026, 11, 1, 1, 0, 0, _est),
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _est),
                new DateTimeOffset(2026, 11, 1, 2, 0, 0, _est),
            ],
            deadlines);
    }

    [Fact]
    public void NextDeadlines_HourlyAtAMinute_AlsoFiresInBothHours()
    {
        // A star in the hour field is an interval to Cronos, so "30 * * * *" fires at 01:30 EDT and again at 01:30 EST.
        var deadlines = ScheduleOccurrences.NextDeadlines(Action(1, "30 * * * *"), new DateTimeOffset(2026, 11, 1, 0, 45, 0, _edt), _eastern, 3);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt),
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _est),
                new DateTimeOffset(2026, 11, 1, 2, 30, 0, _est),
            ],
            deadlines);
    }

    [Fact]
    public void NextDeadlines_ReturnsAtMostCount_Ascending()
    {
        var deadlines = ScheduleOccurrences.NextDeadlines(Action(1, "0 */6 * * *"), new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt), _eastern, 3);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 6, 15, 12, 0, 0, _edt),
                new DateTimeOffset(2026, 6, 15, 18, 0, 0, _edt),
                new DateTimeOffset(2026, 6, 16, 0, 0, 0, _edt),
            ],
            deadlines);
        Assert.Empty(ScheduleOccurrences.NextDeadlines(Action(1, "0 */6 * * *"), new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt), _eastern, 0));
    }

    // ---------------------------------------------------------------- DueInMinute

    [Fact]
    public void DueInMinute_WarningCrossingMidnight_MatchesOnThePreviousDaysTick()
    {
        var midnightRestart = Action(1, "0 0 * * *", warning: 10);
        var tick = new DateTimeOffset(2026, 6, 14, 23, 50, 0, _edt);

        var due = ScheduleOccurrences.DueInMinute([midnightRestart], tick, _eastern);

        var (action, occurrence) = Assert.Single(due);
        Assert.Same(midnightRestart, action);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 0, 0, 0, _edt), occurrence.Deadline);
        Assert.Equal(tick, occurrence.Due);
    }

    [Fact]
    public void DueInMinute_RconCommand_IsDueAtItsDeadline()
    {
        var tick = new DateTimeOffset(2026, 6, 15, 20, 0, 0, _edt);

        var due = ScheduleOccurrences.DueInMinute([Action(1, "0 20 * * *", ScheduledActionKind.RconCommand, warning: 10)], tick, _eastern);

        var (_, occurrence) = Assert.Single(due);
        Assert.Equal(tick, occurrence.Deadline);
        Assert.Equal(tick, occurrence.Due);
    }

    [Fact]
    public void DueInMinute_IgnoresSecondsOfTheTick()
    {
        var tick = new DateTimeOffset(2026, 6, 15, 19, 50, 37, _edt);

        var due = ScheduleOccurrences.DueInMinute([Action(1, "0 20 * * *", warning: 10)], tick, _eastern);

        var (_, occurrence) = Assert.Single(due);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 19, 50, 0, _edt), occurrence.Due);
    }

    [Fact]
    public void DueInMinute_TheMinuteBeforeAndAfter_ReturnsNothing()
    {
        ScheduledAction[] rows = [Action(1, "0 20 * * *", warning: 10)];

        Assert.Empty(ScheduleOccurrences.DueInMinute(rows, new DateTimeOffset(2026, 6, 15, 19, 49, 0, _edt), _eastern));
        Assert.Empty(ScheduleOccurrences.DueInMinute(rows, new DateTimeOffset(2026, 6, 15, 19, 51, 0, _edt), _eastern));
    }

    [Fact]
    public void DueInMinute_ReturnsEachDueRowOnceInInputOrder()
    {
        var first = Action(1, "0 20 * * *", warning: 10);
        var second = Action(2, "50 19 * * *", ScheduledActionKind.RconCommand);
        var later = Action(3, "0 21 * * *", warning: 10);
        var tick = new DateTimeOffset(2026, 6, 15, 19, 50, 0, _edt);

        var due = ScheduleOccurrences.DueInMinute([first, second, later], tick, _eastern);

        Assert.Equal([1, 2], due.Select(d => d.Action.Id));
        Assert.All(due, d => Assert.Equal(tick, d.Occurrence.Due));
    }

    [Fact]
    public void DueInMinute_UtcTickMatchesTheSameInstant()
    {
        var tick = new DateTimeOffset(2026, 6, 15, 23, 50, 0, TimeSpan.Zero);

        var due = ScheduleOccurrences.DueInMinute([Action(1, "0 20 * * *", warning: 10)], tick, _eastern);

        var (_, occurrence) = Assert.Single(due);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 20, 0, 0, _edt), occurrence.Deadline);
        Assert.Equal(_edt, occurrence.Deadline.Offset);
    }

    [Fact]
    public void DueInMinute_UnparsableRow_IsIgnored()
    {
        Assert.Empty(ScheduleOccurrences.DueInMinute([Action(1, "bogus")], new DateTimeOffset(2026, 6, 15, 19, 50, 0, _edt), _eastern));
    }

    [Fact]
    public void DueInMinute_SeveralOccurrencesADay_MatchesEachOnce()
    {
        var row = Action(1, "0 */6 * * *", warning: 10);
        var dayStart = new DateTimeOffset(2026, 6, 15, 0, 0, 0, _edt);

        var deadlines = SweepDeadlines([row], dayStart, dayStart.AddDays(1));

        Assert.Equal(
            [
                new DateTimeOffset(2026, 6, 15, 6, 0, 0, _edt),
                new DateTimeOffset(2026, 6, 15, 12, 0, 0, _edt),
                new DateTimeOffset(2026, 6, 15, 18, 0, 0, _edt),
                new DateTimeOffset(2026, 6, 16, 0, 0, 0, _edt),
            ],
            deadlines);
    }

    [Fact]
    public void DueInMinute_SpringForwardGap_FiresOnceRightAfterTheTransition()
    {
        var row = Action(1, "30 2 * * *", warning: 10);
        var firstValidMinute = new DateTimeOffset(2026, 3, 8, 3, 0, 0, _edt);

        var matches = new List<(DateTimeOffset Tick, ScheduleOccurrence Occurrence)>();
        for (var tick = new DateTimeOffset(2026, 3, 8, 1, 0, 0, _est); tick <= new DateTimeOffset(2026, 3, 8, 4, 0, 0, _edt); tick = tick.AddMinutes(1))
        {
            foreach (var (_, occurrence) in ScheduleOccurrences.DueInMinute([row], tick, _eastern))
            {
                matches.Add((tick, occurrence));
            }
        }

        var (matchedTick, matched) = Assert.Single(matches);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 1, 50, 0, _est), matchedTick);
        Assert.Equal(firstValidMinute, matched.Deadline);
        Assert.Equal(matchedTick, matched.Due);
    }

    [Fact]
    public void DueInMinute_TheDayAfterTheGap_IsAnOrdinaryOccurrence()
    {
        var due = ScheduleOccurrences.DueInMinute([Action(1, "30 2 * * *", warning: 10)], new DateTimeOffset(2026, 3, 9, 2, 20, 0, _edt), _eastern);

        var (_, occurrence) = Assert.Single(due);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 2, 30, 0, _edt), occurrence.Deadline);
    }

    [Fact]
    public void DueInMinute_FallBackOverlap_FixedTimeFiresOnTheEarlierInstantOnly()
    {
        var row = Action(1, "30 1 * * *", warning: 0);
        var earlier = new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt);
        var later = new DateTimeOffset(2026, 11, 1, 1, 30, 0, _est);

        var (_, occurrence) = Assert.Single(ScheduleOccurrences.DueInMinute([row], earlier, _eastern));
        Assert.Equal(earlier, occurrence.Deadline);
        Assert.Equal(_edt, occurrence.Deadline.Offset);
        Assert.Empty(ScheduleOccurrences.DueInMinute([row], later, _eastern));
    }

    [Fact]
    public void DueInMinute_FallBackOverlap_IntervalFiresInBothHours()
    {
        var row = Action(1, "*/30 * * * *", ScheduledActionKind.RconCommand);

        var deadlines = SweepDeadlines([row], new DateTimeOffset(2026, 11, 1, 0, 45, 0, _edt), new DateTimeOffset(2026, 11, 1, 2, 15, 0, _est));

        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 1, 1, 0, 0, _edt),
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _edt),
                new DateTimeOffset(2026, 11, 1, 1, 0, 0, _est),
                new DateTimeOffset(2026, 11, 1, 1, 30, 0, _est),
                new DateTimeOffset(2026, 11, 1, 2, 0, 0, _est),
            ],
            deadlines);
    }

    // ---------------------------------------------------------------- NextDeadline (applicable rows)

    [Fact]
    public void NextDeadline_TodayStillAhead_PicksToday()
    {
        var now = new DateTimeOffset(2026, 6, 15, 7, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "0 8 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_TodayAlreadyPast_RollsToTomorrow()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "0 8 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_ExactlyNow_IsNotStrictlyAfterSoRollsToTomorrow()
    {
        var now = new DateTimeOffset(2026, 6, 15, 8, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "0 8 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 16, 8, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_PicksTheEarliestAcrossRows()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "0 8 * * *"), Action(2, "0 22 * * *"), Action(3, "0 12 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 0, 0, _edt), next);
    }

    [Fact]
    public void NextDeadline_IgnoresRowsThatDoNotParseOrNeverFire()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, _edt);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "bogus"), Action(2, "0 0 30 2 *"), Action(3, "0 12 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 0, 0, _edt), next);
        Assert.Null(ScheduleOccurrences.NextDeadline([Action(1, "bogus"), Action(2, "0 0 30 2 *")], now, _eastern));
    }

    [Fact]
    public void NextDeadline_OnTheGapDay_IsTheShiftedOccurrence()
    {
        var now = new DateTimeOffset(2026, 3, 8, 1, 0, 0, _est);

        var next = ScheduleOccurrences.NextDeadline([Action(1, "30 2 * * *")], now, _eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 0, 0, _edt), next);
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
            Action(7, "0 10 * * *", instanceId: 10),
            Action(3, "0 10 * * *", clusterId: 5),
            Action(9, "0 10 * * *", clusterId: 6),
            Action(1, "0 10 * * *", instanceId: 11),
            Action(5, "0 10 * * *", clusterId: 5),
        ];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([3, 5, 7], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_OverridesClusterSchedule_DropsTheClusterRows()
    {
        var instance = Instance(10, clusterId: 5, overridesClusterSchedule: true);
        ScheduledAction[] all = [Action(3, "0 10 * * *", clusterId: 5), Action(7, "0 10 * * *", instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([7], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_DisabledRows_AreLeftOut()
    {
        var instance = Instance(10, clusterId: 5);
        ScheduledAction[] all = [Action(3, "0 10 * * *", clusterId: 5, enabled: false), Action(7, "0 10 * * *", instanceId: 10, enabled: false), Action(8, "0 10 * * *", instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([8], applicable.Select(a => a.Id));
    }

    [Fact]
    public void Applicable_StandaloneInstance_SeesOnlyItsOwnRows()
    {
        var instance = Instance(10, clusterId: null);
        ScheduledAction[] all = [Action(3, "0 10 * * *", clusterId: 5), Action(7, "0 10 * * *", instanceId: 10)];

        var applicable = ScheduleOccurrences.Applicable(instance, all);

        Assert.Equal([7], applicable.Select(a => a.Id));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The deadlines <see cref="ScheduleOccurrences.DueInMinute"/> reports over every minute from <paramref name="from"/> up to <paramref name="until"/>, in tick order.</summary>
    private static List<DateTimeOffset> SweepDeadlines(ScheduledAction[] rows, DateTimeOffset from, DateTimeOffset until)
    {
        var deadlines = new List<DateTimeOffset>();
        for (var tick = from; tick <= until; tick = tick.AddMinutes(1))
        {
            deadlines.AddRange(ScheduleOccurrences.DueInMinute(rows, tick, _eastern).Select(d => d.Occurrence.Deadline));
        }

        return deadlines;
    }

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
        string cron,
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
            Cron = cron,
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
