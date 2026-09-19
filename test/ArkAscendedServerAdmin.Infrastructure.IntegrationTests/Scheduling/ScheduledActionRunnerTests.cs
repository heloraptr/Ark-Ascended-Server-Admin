using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Scheduling;

/// <summary>
/// The scheduled-action runner's claim, skip, fan-out, execute, and recovery rules (B3) against the real run
/// table, with the process manager and the RCON send path faked. The clock's zone is pinned (UTC, or US Eastern
/// for the clock-change case) so the machine's zone never matters.
/// </summary>
public class ScheduledActionRunnerTests
{
    private static readonly DateTimeOffset _noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private const int Noon = 12 * 60;

    // ---- claim -------------------------------------------------------------------------------------

    [Fact]
    public async Task DueRow_IsClaimedOnceForTheDay_AndLaterTicksInsertNothingMore()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((action, instance.Id, new DateOnly(2026, 9, 7), ScheduledActionOutcome.Succeeded, string.Empty), (run.ScheduledActionId, run.InstanceId, run.LocalDate, run.Outcome, run.Reason));
        Assert.Equal(_noon, run.StartedAt);
        Assert.Equal(_noon, run.CompletedAt);
        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task TwoRowsDueInTheSameMinute_RunTheFirstAndSkipTheSecond()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var first = await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        var second = await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "broadcast hi", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var runs = await f.RunsAsync(ct);
        Assert.Equal(
            [(first, ScheduledActionOutcome.Succeeded, string.Empty), (second, ScheduledActionOutcome.Skipped, ScheduledActionRunner.ActionInProgressReason)],
            runs.Select(r => (r.ScheduledActionId, r.Outcome, r.Reason)));
        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task ClusterRow_FansOutToEveryMember_ExceptOneThatOverridesTheClusterSchedule()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var alpha = await TestSeed.InstanceAsync(root, "alpha", clustered: true, ct);
        var beta = await TestSeed.InstanceAsync(root, "beta", clustered: true, ct);
        var gamma = await TestSeed.InstanceAsync(root, "gamma", clustered: true, ct, i => i.OverridesClusterSchedule = true);
        var action = await f.ActionAsync(null, alpha.ClusterId, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        foreach (var id in new[] { alpha.Id, beta.Id, gamma.Id })
        {
            f.Processes.Set(id, InstanceState.Running);
        }

        await f.TickAsync(ct);

        var runs = await f.RunsAsync(ct);
        Assert.Equal([(action, alpha.Id), (action, beta.Id)], runs.Select(r => (r.ScheduledActionId, r.InstanceId)));
        Assert.All(runs, r => Assert.Equal(ScheduledActionOutcome.Succeeded, r.Outcome));
        Assert.Equal([(alpha.Id, "saveworld"), (beta.Id, "saveworld")], f.Rcon.Calls.OrderBy(c => c.InstanceId));
    }

    // ---- skips -------------------------------------------------------------------------------------

    [Fact]
    public async Task InstanceNotRunning_IsSkippedWithTheReason()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Stopped);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Skipped, ScheduledActionRunner.NotRunningReason), (run.Outcome, run.Reason));
        Assert.Equal(_noon, run.CompletedAt);
        Assert.Empty(f.Rcon.Calls);
    }

    [Fact]
    public async Task UpdateInProgress_IsSkippedWithTheReason()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);
        using var update = await f.Gate.AcquireExclusiveAsync(ct);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Skipped, ScheduledActionRunner.UpdateInProgressReason), (run.Outcome, run.Reason));
        Assert.Empty(f.Rcon.Calls);
    }

    [Fact]
    public async Task InstanceLockHeldElsewhere_IsSkippedWithTheReason()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);
        using var backup = f.Locks.TryAcquire(instance.Id);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Skipped, ScheduledActionRunner.LockBusyReason), (run.Outcome, run.Reason));
        Assert.Empty(f.Rcon.Calls);
    }

    [Fact]
    public async Task SpringForwardGap_WritesOneSkippedRunWithTheGapReason()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        // 03:00 EDT on 2026-03-08 is the first minute after the missing hour.
        var f = await Fixture.CreateAsync(root, new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), FindEastern(), ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, 2 * 60 + 30, ScheduledActionKind.Restart, warning: 10, ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((action, new DateOnly(2026, 3, 8), ScheduledActionOutcome.Skipped), (run.ScheduledActionId, run.LocalDate, run.Outcome));
        Assert.Equal("The local time 02:30 does not exist on 2026-03-08 (clocks moved forward).", run.Reason);
        Assert.Empty(f.Processes.Restarts);
    }

    // ---- execution ---------------------------------------------------------------------------------

    [Fact]
    public async Task RconCommand_SendsTheCommandText_AndRecordsAFailureWithItsMessage()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);
        f.Rcon.Results["saveworld"] = CommandResult<string>.Fail("RCON connect failure: Connection refused.");

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Failed, "RCON connect failure: Connection refused."), (run.Outcome, run.Reason));
        Assert.Equal(_noon, run.CompletedAt);
        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task Restart_HandsTheAbsoluteDeadlineToTheProcessManager_WithoutHoldingTheLock()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon + 10, ScheduledActionKind.Restart, warning: 10, ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal(ScheduledActionOutcome.Succeeded, run.Outcome);
        Assert.Equal([(instance.Id, _noon.AddMinutes(10))], f.Processes.Restarts);
        Assert.Empty(f.Locks.Holders);
        Assert.Empty(f.Rcon.Calls);
    }

    [Fact]
    public async Task DinoWipe_CountsDownToTheDeadline_ThenDestroysWildDinos()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, Noon + 10, ScheduledActionKind.DinoWipe, warning: 10, ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal(ScheduledActionOutcome.Succeeded, run.Outcome);
        Assert.Equal([(instance.Id, _noon.AddMinutes(10), ScheduledActionRunner.DinoWipeCountdownTemplate)], f.Processes.Countdowns);
        Assert.Equal([(instance.Id, RconCommands.DestroyWildDinos)], f.Rcon.Calls);
        Assert.Empty(f.Locks.Holders);
    }

    // ---- recovery ----------------------------------------------------------------------------------

    [Fact]
    public async Task Recover_MarksEveryStartedRunInterrupted()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        await f.RunAsync(action, instance.Id, new DateOnly(2026, 9, 6), _noon.AddDays(-1), ScheduledActionOutcome.Started, ct);
        await f.RunAsync(action, instance.Id, new DateOnly(2026, 9, 5), _noon.AddDays(-2), ScheduledActionOutcome.Succeeded, ct);

        await f.Runner.RecoverAsync(ct);

        var runs = await f.RunsAsync(ct);
        Assert.Equal(
            [(ScheduledActionOutcome.Interrupted, ScheduledActionRunner.InterruptedReason, (DateTimeOffset?)_noon), (ScheduledActionOutcome.Succeeded, string.Empty, null)],
            runs.Select(r => (r.Outcome, r.Reason, r.CompletedAt)));
    }

    [Fact]
    public async Task Recover_PrunesRunsOlderThanThirtyDays()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, Noon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        await f.RunAsync(action, instance.Id, new DateOnly(2026, 8, 7), _noon.AddDays(-31), ScheduledActionOutcome.Succeeded, ct);
        var kept = await f.RunAsync(action, instance.Id, new DateOnly(2026, 8, 9), _noon.AddDays(-29), ScheduledActionOutcome.Failed, ct);

        await f.Runner.RecoverAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal(kept, run.Id);
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

    /// <summary>Records every send; replies with a scripted result per command, else success.</summary>
    private sealed class RecordingRconOperations : IRconOperations
    {
        public List<(int InstanceId, string Command)> Calls { get; } = [];

        public Dictionary<string, CommandResult<string>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<CommandResult<string>> ExecuteAsync(int instanceId, string command, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((instanceId, command));
            }

            return Task.FromResult(Results.TryGetValue(command, out var scripted) ? scripted : CommandResult<string>.Ok("ok"));
        }
    }

    private sealed class Fixture
    {
        public required TempDataRoot Root { get; init; }

        public required ScheduledActionRunner Runner { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FakeInstanceLocks Locks { get; init; }

        public required FakeMaintenanceGate Gate { get; init; }

        public required RecordingRconOperations Rcon { get; init; }

        public required FastTimeProvider Clock { get; init; }

        /// <summary>One tick, then every detached run it started.</summary>
        public async Task TickAsync(CancellationToken ct)
        {
            await Runner.RunTickAsync(ct);
            await Runner.WhenIdleAsync();
        }

        public async Task<int> ActionAsync(int? instanceId, int? clusterId, int timeOfDay, ScheduledActionKind kind, int warning = 10, string command = "", CancellationToken ct = default)
        {
            await using var db = Root.CreateDbContext();
            var action = new ScheduledAction { InstanceId = instanceId, ClusterId = clusterId, TimeOfDay = timeOfDay, Kind = kind, WarningMinutes = warning, Command = command };
            db.ScheduledActions.Add(action);
            await db.SaveChangesAsync(ct);
            return action.Id;
        }

        public async Task<int> RunAsync(int actionId, int instanceId, DateOnly localDate, DateTimeOffset startedAt, ScheduledActionOutcome outcome, CancellationToken ct)
        {
            await using var db = Root.CreateDbContext();
            var run = new ScheduledActionRun { ScheduledActionId = actionId, InstanceId = instanceId, LocalDate = localDate, StartedAt = startedAt, Outcome = outcome };
            db.ScheduledActionRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run.Id;
        }

        public async Task<List<ScheduledActionRun>> RunsAsync(CancellationToken ct)
        {
            await using var db = Root.CreateDbContext();
            return await db.ScheduledActionRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, DateTimeOffset start, TimeZoneInfo zone, CancellationToken ct)
        {
            await root.InitializeAsync(ct);
            var clock = new FastTimeProvider(start) { Zone = zone };
            var gate = new FakeMaintenanceGate();
            var processes = new FakeProcessManager(gate);
            var locks = new FakeInstanceLocks();
            var rcon = new RecordingRconOperations();
            var runner = new ScheduledActionRunner(root, processes, locks, gate, rcon, clock, NullLogger<ScheduledActionRunner>.Instance);
            return new Fixture { Root = root, Runner = runner, Processes = processes, Locks = locks, Gate = gate, Rcon = rcon, Clock = clock };
        }
    }
}
