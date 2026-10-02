using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Infrastructure.Startup;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Scheduling;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Scheduling;

/// <summary>
/// The scheduled-action runner's claim, skip, fan-out, execute, and recovery rules (B3) against the real run
/// table, with the process manager and the RCON send path faked. The clock's zone is pinned (UTC, or US Eastern
/// for the clock-change cases) so the machine's zone never matters.
/// </summary>
public class ScheduledActionRunnerTests
{
    private static readonly DateTimeOffset _noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private const string AtNoon = "0 12 * * *";

    // ---- claim -------------------------------------------------------------------------------------

    [Fact]
    public async Task DueRow_IsClaimedOnceForTheOccurrence_AndLaterTicksInTheMinuteInsertNothingMore()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((action, instance.Id, _noon, ScheduledActionOutcome.Succeeded, string.Empty), (run.ScheduledActionId, run.InstanceId, run.ScheduledFor, run.Outcome, run.Reason));
        Assert.Equal(_noon, run.StartedAt);
        Assert.Equal(_noon, run.CompletedAt);
        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task RowFiringTwiceADay_GetsOneRunPerOccurrence()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, "0 12,13 * * *", ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        await f.TickAsync(ct);

        var runs = await f.RunsAsync(ct);
        Assert.Equal([(action, _noon), (action, _noon.AddHours(1))], runs.Select(r => (r.ScheduledActionId, r.ScheduledFor)));
        Assert.All(runs, r => Assert.Equal(ScheduledActionOutcome.Succeeded, r.Outcome));
        Assert.Equal([(instance.Id, "saveworld"), (instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task TwoRowsDueInTheSameMinute_RunTheFirstAndSkipTheSecond()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var first = await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        var second = await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "broadcast hi", ct: ct);
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
        var action = await f.ActionAsync(null, alpha.ClusterId, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
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
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
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
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
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
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);
        using var backup = f.Locks.TryAcquire(instance.Id);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Skipped, ScheduledActionRunner.LockBusyReason), (run.Outcome, run.Reason));
        Assert.Empty(f.Rcon.Calls);
        Assert.Equal(_noon + ScheduledActionRunner.LockRetryDelay, f.Clock.GetUtcNow()); // one retry, then the skip
    }

    /// <summary>
    /// The schedule row is deleted after the tick read it, so the claim fails on the foreign key rather than the
    /// unique index. The lease taken just before the claim must be released, or the instance stays locked.
    /// </summary>
    [Fact]
    public async Task ClaimFailingForAnotherReason_ReleasesTheInstanceLock()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct, deleteActionsOnAcquire: true);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await Assert.ThrowsAsync<DbUpdateException>(() => f.TickAsync(ct));

        Assert.Empty(await f.RunsAsync(ct));
        Assert.Empty(f.Rcon.Calls);
        using var free = f.Locks.TryAcquire(instance.Id);
        Assert.NotNull(free);
    }

    /// <summary>A lock held for a moment (a console history write) costs one short wait, not the occurrence.</summary>
    [Fact]
    public async Task InstanceLockBusyOnlyBriefly_RunsAfterOneRetry()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct, busyAttempts: 1);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal(ScheduledActionOutcome.Succeeded, run.Outcome);
        Assert.Single(f.Rcon.Calls);
        Assert.Equal(2, f.LockAttempts!.Attempts);
        Assert.Equal(_noon + ScheduledActionRunner.LockRetryDelay, f.Clock.GetUtcNow());

        await f.TickAsync(ct); // the same minute again: the occurrence is already claimed
        Assert.Single(await f.RunsAsync(ct));
        Assert.Single(f.Rcon.Calls);
    }

    [Fact]
    public async Task InstanceLockBusyOnBothTries_IsSkippedOnceWithTheReason()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct, busyAttempts: 2);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Skipped, ScheduledActionRunner.LockBusyReason), (run.Outcome, run.Reason));
        Assert.Empty(f.Rcon.Calls);
        Assert.Equal(2, f.LockAttempts!.Attempts);
    }

    [Fact]
    public async Task InstanceLockFree_TakesNoDelay()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct, busyAttempts: 0);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        Assert.Equal(ScheduledActionOutcome.Succeeded, Assert.Single(await f.RunsAsync(ct)).Outcome);
        Assert.Equal(1, f.LockAttempts!.Attempts);
        Assert.Equal(_noon, f.Clock.GetUtcNow()); // FastTimeProvider advances only when a delay is taken
    }

    // ---- clock changes (Cronos' rules, US Eastern) -------------------------------------------------

    [Fact]
    public async Task SpringForwardGap_RunsOnceRightAfterTheTransition()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        // 02:30 EST does not exist on 2026-03-08; the occurrence moves to 03:00 EDT (07:00Z), so a 10-minute warning is due at 06:50Z.
        var shifted = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero);
        var f = await Fixture.CreateAsync(root, shifted.AddMinutes(-11), FindEastern(), ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, "30 2 * * *", ScheduledActionKind.Restart, warning: 10, ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        for (var tick = 0; tick < 30; tick++)
        {
            await f.TickAsync(ct);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((action, shifted, ScheduledActionOutcome.Succeeded), (run.ScheduledActionId, run.ScheduledFor, run.Outcome));
        Assert.Equal(shifted.AddMinutes(-10), run.StartedAt);
        Assert.Equal([(instance.Id, shifted)], f.Processes.Restarts);
    }

    [Fact]
    public async Task FallBackOverlap_FixedTimeRunsOnceAtTheEarlierInstant()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        // 01:30 happens twice on 2026-11-01: 05:30Z (EDT) and 06:30Z (EST). Only the first is an occurrence.
        var earlier = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var f = await Fixture.CreateAsync(root, earlier, FindEastern(), ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        var action = await f.ActionAsync(instance.Id, null, "30 1 * * *", ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((action, earlier, ScheduledActionOutcome.Succeeded), (run.ScheduledActionId, run.ScheduledFor, run.Outcome));
        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
    }

    [Fact]
    public async Task FallBackOverlap_IntervalRunsInBothHours()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var firstHour = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero); // 01:00 EDT
        var f = await Fixture.CreateAsync(root, firstHour, FindEastern(), ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, "*/30 * * * *", ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        for (var tick = 0; tick < 4; tick++)
        {
            await f.TickAsync(ct);
            f.Clock.Advance(TimeSpan.FromMinutes(30));
        }

        var runs = await f.RunsAsync(ct);
        Assert.Equal([firstHour, firstHour.AddMinutes(30), firstHour.AddMinutes(60), firstHour.AddMinutes(90)], runs.Select(r => r.ScheduledFor));
        Assert.All(runs, r => Assert.Equal(ScheduledActionOutcome.Succeeded, r.Outcome));
        Assert.Equal(4, f.Rcon.Calls.Count);
    }

    // ---- execution ---------------------------------------------------------------------------------

    [Fact]
    public async Task RconCommand_SendsTheCommandText_AndRecordsAFailureWithItsMessage()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct);
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
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
        await f.ActionAsync(instance.Id, null, "10 12 * * *", ScheduledActionKind.Restart, warning: 10, ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal((ScheduledActionOutcome.Succeeded, _noon.AddMinutes(10)), (run.Outcome, run.ScheduledFor));
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
        await f.ActionAsync(instance.Id, null, "10 12 * * *", ScheduledActionKind.DinoWipe, warning: 10, ct: ct);
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
        var action = await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        await f.RunAsync(action, instance.Id, _noon.AddDays(-1), ScheduledActionOutcome.Started, ct);
        await f.RunAsync(action, instance.Id, _noon.AddDays(-2), ScheduledActionOutcome.Succeeded, ct);

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
        var action = await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        await f.RunAsync(action, instance.Id, _noon.AddDays(-31), ScheduledActionOutcome.Succeeded, ct);
        var kept = await f.RunAsync(action, instance.Id, _noon.AddDays(-29), ScheduledActionOutcome.Failed, ct);

        await f.Runner.RecoverAsync(ct);

        var run = Assert.Single(await f.RunsAsync(ct));
        Assert.Equal(kept, run.Id);
    }

    /// <summary>
    /// The run's outcome cannot be written (the context fails to open after the action ran). The detached run logs
    /// it and completes instead of faulting a task nobody observes; the row stays Started for the next recovery.
    /// </summary>
    [Fact]
    public async Task CompletionWriteFailing_IsLoggedAndTheDetachedRunCompletes()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, _noon, TimeZoneInfo.Utc, ct, failContextOnCall: 2); // call 1 is the tick's own context
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
        await f.ActionAsync(instance.Id, null, AtNoon, ScheduledActionKind.RconCommand, command: "saveworld", ct: ct);
        f.Processes.Set(instance.Id, InstanceState.Running);

        await f.TickAsync(ct); // WhenIdleAsync rethrows a faulted run

        Assert.Equal([(instance.Id, "saveworld")], f.Rcon.Calls);
        var error = Assert.Single(f.Log.Errors);
        Assert.IsType<NotSupportedException>(error.Exception);
        Assert.StartsWith("Could not record the outcome of scheduled action run", error.Message, StringComparison.Ordinal);
        Assert.Equal(ScheduledActionOutcome.Started, Assert.Single(await f.RunsAsync(ct)).Outcome);
        Assert.Empty(f.Locks.Holders);
    }

    // ---- hosted loop -------------------------------------------------------------------------------

    /// <summary>
    /// An exception type the loop never expected is logged and the next minute tries again; it must not escape
    /// <c>ExecuteAsync</c>, where it would stop the whole service. Ticks are aligned to the minute, so the retry
    /// comes at the next minute boundary, not a full minute after the failure.
    /// </summary>
    [Fact]
    public async Task Loop_TickThatThrows_IsLoggedAndTriedAgainAtTheNextMinute()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var clock = new ManualTimeProvider(_noon.AddSeconds(30));
        var contexts = new FailingContextFactory(root, failOnCall: 2); // call 1 is the start-of-service recovery
        var log = new RecordingLogger<ScheduledActionRunner>();
        var runner = HostedRunner(contexts, clock, log);

        await runner.StartAsync(ct);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        Assert.Equal(1, contexts.Calls.Count);
        clock.Advance(TimeSpan.FromSeconds(30));
        await contexts.Calls.WhenAttempt(2).WaitAsync(_wait, ct);

        // Parked on the next delay, so the failure has been handled and nothing else can run until the clock moves.
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        var error = Assert.Single(log.Errors);
        Assert.IsType<NotSupportedException>(error.Exception);
        Assert.Equal("Scheduled action tick failed; it will try again next minute.", error.Message);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(2, contexts.Calls.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await contexts.Calls.WhenAttempt(3).WaitAsync(_wait, ct);

        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        await runner.StopAsync(ct);
        Assert.True(runner.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(log.Errors);
    }

    /// <summary>A recovery pass that throws an unexpected type is logged, and the tick loop still starts.</summary>
    [Fact]
    public async Task Loop_RecoveryThatThrows_IsLoggedAndTheTicksStillRun()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var clock = new ManualTimeProvider(_noon);
        var contexts = new FailingContextFactory(root, failOnCall: 1);
        var log = new RecordingLogger<ScheduledActionRunner>();
        var runner = HostedRunner(contexts, clock, log);

        await runner.StartAsync(ct);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        var error = Assert.Single(log.Errors);
        Assert.IsType<NotSupportedException>(error.Exception);

        clock.Advance(ScheduledActionRunner.Tick);
        await contexts.Calls.WhenAttempt(2).WaitAsync(_wait, ct);

        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        await runner.StopAsync(ct);
        Assert.True(runner.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(log.Errors);
    }

    /// <summary>
    /// The retention prune is not only a start-of-service pass: the loop repeats it once a day, so a run that turns
    /// stale while the service keeps running is deleted at the first tick after a day has passed.
    /// </summary>
    [Fact]
    public async Task Loop_PrunesStaleRunsOnceADay()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var clock = new ManualTimeProvider(_noon);
        var runner = HostedRunner(root, clock, new RecordingLogger<ScheduledActionRunner>());
        var instance = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);

        await runner.StartAsync(ct);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct); // recovery and its prune are done; parked on the first delay
        int stale;
        await using (var db = root.CreateDbContext())
        {
            // Disabled, so the ticks below have nothing to run and only the prune touches the table.
            var action = new ScheduledAction { InstanceId = instance.Id, Cron = AtNoon, Kind = ScheduledActionKind.RconCommand, Command = "saveworld", Enabled = false };
            db.ScheduledActions.Add(action);
            await db.SaveChangesAsync(ct);
            var run = new ScheduledActionRun { ScheduledActionId = action.Id, InstanceId = instance.Id, ScheduledFor = _noon.AddDays(-31), StartedAt = _noon.AddDays(-31), Outcome = ScheduledActionOutcome.Succeeded };
            db.ScheduledActionRuns.Add(run);
            await db.SaveChangesAsync(ct);
            stale = run.Id;
        }

        clock.Advance(ScheduledActionRunner.Tick);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        Assert.Contains(stale, await RunIdsAsync(root, ct));

        clock.Advance(ScheduledActionRunner.PruneEvery);
        await clock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        Assert.DoesNotContain(stale, await RunIdsAsync(root, ct));

        await runner.StopAsync(ct);
        Assert.True(runner.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static async Task<List<int>> RunIdsAsync(TempDataRoot root, CancellationToken ct)
    {
        await using var db = root.CreateDbContext();
        return await db.ScheduledActionRuns.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
    }

    /// <summary>A runner for the hosted-loop tests: readiness already Ready, no instances to act on.</summary>
    private static ScheduledActionRunner HostedRunner(IDbContextFactory<AppDbContext> contexts, TimeProvider clock, RecordingLogger<ScheduledActionRunner> log)
    {
        var gate = new FakeMaintenanceGate();
        return new ScheduledActionRunner(contexts, new FakeProcessManager(gate), new FakeInstanceLocks(), gate, new RecordingRconOperations(), new FakeReadinessMonitor(ReadinessPhase.Ready), clock, log);
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

        public required RecordingLogger<ScheduledActionRunner> Log { get; init; }

        /// <summary>Set when the fixture was built with <c>busyAttempts</c>: counts the runner's lock attempts.</summary>
        public BusyFirstLocks? LockAttempts { get; init; }

        /// <summary>One tick, then every detached run it started.</summary>
        public async Task TickAsync(CancellationToken ct)
        {
            await Runner.RunTickAsync(ct);
            await Runner.WhenIdleAsync();
        }

        public async Task<int> ActionAsync(int? instanceId, int? clusterId, string cron, ScheduledActionKind kind, int warning = 10, string command = "", CancellationToken ct = default)
        {
            await using var db = Root.CreateDbContext();
            var action = new ScheduledAction { InstanceId = instanceId, ClusterId = clusterId, Cron = cron, Kind = kind, WarningMinutes = warning, Command = command };
            db.ScheduledActions.Add(action);
            await db.SaveChangesAsync(ct);
            return action.Id;
        }

        /// <summary>A run seeded straight into the table, scheduled for and started at <paramref name="startedAt"/>.</summary>
        public async Task<int> RunAsync(int actionId, int instanceId, DateTimeOffset startedAt, ScheduledActionOutcome outcome, CancellationToken ct)
        {
            await using var db = Root.CreateDbContext();
            var run = new ScheduledActionRun { ScheduledActionId = actionId, InstanceId = instanceId, ScheduledFor = startedAt, StartedAt = startedAt, Outcome = outcome };
            db.ScheduledActionRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run.Id;
        }

        public async Task<List<ScheduledActionRun>> RunsAsync(CancellationToken ct)
        {
            await using var db = Root.CreateDbContext();
            return await db.ScheduledActionRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, DateTimeOffset start, TimeZoneInfo zone, CancellationToken ct, int? busyAttempts = null, bool deleteActionsOnAcquire = false, int? failContextOnCall = null)
        {
            await root.InitializeAsync(ct);
            var clock = new FastTimeProvider(start) { Zone = zone };
            var gate = new FakeMaintenanceGate();
            var processes = new FakeProcessManager(gate);
            var locks = new FakeInstanceLocks();
            var rcon = new RecordingRconOperations();
            var attempts = busyAttempts is { } busy ? new BusyFirstLocks(locks, busy) : null;
            IInstanceLocks runnerLocks = deleteActionsOnAcquire ? new ActionDeletingLocks(locks, root) : (IInstanceLocks?)attempts ?? locks;
            IDbContextFactory<AppDbContext> contexts = failContextOnCall is { } call ? new FailingContextFactory(root, call) : root;
            var log = new RecordingLogger<ScheduledActionRunner>();
            var runner = new ScheduledActionRunner(contexts, processes, runnerLocks, gate, rcon, new ReadinessMonitor(clock, NullLogger<ReadinessMonitor>.Instance), clock, log);
            return new Fixture { Root = root, Runner = runner, Processes = processes, Locks = locks, Gate = gate, Rcon = rcon, Clock = clock, Log = log, LockAttempts = attempts };
        }
    }

    /// <summary>The real database, except that the context opened by call number <c>failOnCall</c> throws; every call is counted.</summary>
    private sealed class FailingContextFactory(TempDataRoot inner, int failOnCall) : IDbContextFactory<AppDbContext>
    {
        public AttemptLog Calls { get; } = new();

        public AppDbContext CreateDbContext() =>
            Calls.Record() == failOnCall ? throw new NotSupportedException("The database could not be opened.") : inner.CreateDbContext();
    }

    /// <summary>Reports the lock busy for the first <c>busy</c> attempts (a brief holder), then defers to the real locks.</summary>
    private sealed class BusyFirstLocks(FakeInstanceLocks inner, int busy) : IInstanceLocks
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IInstanceLease? TryAcquire(int instanceId) =>
            Interlocked.Increment(ref _attempts) <= busy ? null : inner.TryAcquire(instanceId);

        public Task<IInstanceLease> AcquireAsync(int instanceId, CancellationToken cancellationToken) => inner.AcquireAsync(instanceId, cancellationToken);

        public IDisposable? TryReserveCluster(int clusterId) => inner.TryReserveCluster(clusterId);

        public bool IsClusterReserved(int clusterId) => inner.IsClusterReserved(clusterId);
    }

    /// <summary>Deletes every schedule row as the runner takes the lock: the owner saving the schedule list mid-tick.</summary>
    private sealed class ActionDeletingLocks(FakeInstanceLocks inner, TempDataRoot root) : IInstanceLocks
    {
        public IInstanceLease? TryAcquire(int instanceId)
        {
            using var db = root.CreateDbContext();
            db.ScheduledActions.ExecuteDelete();
            return inner.TryAcquire(instanceId);
        }

        public Task<IInstanceLease> AcquireAsync(int instanceId, CancellationToken cancellationToken) => inner.AcquireAsync(instanceId, cancellationToken);

        public IDisposable? TryReserveCluster(int clusterId) => inner.TryReserveCluster(clusterId);

        public bool IsClusterReserved(int clusterId) => inner.IsClusterReserved(clusterId);
    }
}
