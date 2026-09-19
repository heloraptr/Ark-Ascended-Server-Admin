using System.Collections.Concurrent;
using System.Data.Common;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Scheduling;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Scheduling;

/// <summary>
/// The scheduled-action timer (B3): once a minute, on the minute, every instance's applicable rows are asked
/// which of them are due (<see cref="ScheduleOccurrences.DueInMinute"/>), and each due row is claimed by
/// inserting its run for the local day. The unique index on (action, instance, local date) makes that insert
/// the once-per-day guarantee: a second tick in the same minute, or a restart within it, inserts nothing. A
/// row that cannot run gets a <see cref="ScheduledActionOutcome.Skipped"/> run with the reason; a row that
/// can is executed detached from the tick, one per instance at a time, and its run is completed when the
/// operation returns. At service start every run still <see cref="ScheduledActionOutcome.Started"/> becomes
/// <see cref="ScheduledActionOutcome.Interrupted"/> and runs older than <see cref="RetainRunsFor"/> are pruned;
/// a tick the service missed is never caught up. The runner waits for the readiness pipeline to reach Ready
/// before touching the database, so the migrations have applied.
/// </summary>
public sealed class ScheduledActionRunner(
    IDbContextFactory<AppDbContext> contextFactory,
    IProcessManager processManager,
    IInstanceLocks locks,
    IMaintenanceGate gate,
    IRconOperations rconOperations,
    IReadinessMonitor readiness,
    TimeProvider timeProvider,
    ILogger<ScheduledActionRunner> logger) : BackgroundService
{
    /// <summary>SQLite's extended result code for a unique-index violation (SQLITE_CONSTRAINT_UNIQUE).</summary>
    private const int SqliteConstraintUnique = 2067;

    private readonly ConcurrentDictionary<int, Task> _inFlight = new();

    /// <summary>How often the runner looks for due rows; each tick is aligned to the start of a minute.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Runs older than this (by <see cref="ScheduledActionRun.StartedAt"/>) are deleted at service start.</summary>
    public static TimeSpan RetainRunsFor { get; } = TimeSpan.FromDays(30);

    /// <summary>The dino-wipe countdown's broadcast; <c>{0}</c> is "in N minute(s)" or "now".</summary>
    public const string DinoWipeCountdownTemplate = "Wild dinos will be wiped {0}.";

    public const string InterruptedReason = "The service was restarted before the action finished.";

    public const string NotRunningReason = "The server is not running.";

    public const string UpdateInProgressReason = "An update is in progress.";

    public const string ActionInProgressReason = "Another action is in progress.";

    /// <summary>The instance lock was held by something other than a scheduled action (a stop, a backup, a restore).</summary>
    public static string LockBusyReason { get; } = $"The instance is busy ({ProcessManager.OperationInProgress}).";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitUntilReadyAsync(stoppingToken);
            await RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException)
        {
            logger.LogError(ex, "Scheduled action recovery failed; interrupted runs stay Started and old runs are kept until the next start.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNextMinute(), timeProvider, stoppingToken);
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Scheduled action tick failed; it will try again next minute.");
            }
        }
    }

    /// <summary>
    /// Blocks until the readiness pipeline reports Ready. The orchestrator migrates the database on its own
    /// schedule after the host starts, so a recovery pass that ran at once could find the tables missing.
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        if (readiness.Current.IsReady)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(ReadinessState state)
        {
            if (state.IsReady)
            {
                ready.TrySetResult();
            }
        }

        readiness.Changed += OnChanged;
        try
        {
            if (readiness.Current.IsReady)
            {
                return;
            }

            await ready.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            readiness.Changed -= OnChanged;
        }
    }

    /// <summary>
    /// The start-of-service pass: every run still <see cref="ScheduledActionOutcome.Started"/> was in flight when
    /// the service last stopped and becomes <see cref="ScheduledActionOutcome.Interrupted"/>; runs older than
    /// <see cref="RetainRunsFor"/> are deleted. Exposed so tests can drive it without hosting the service.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var interrupted = await db.ScheduledActionRuns.Where(r => r.Outcome == ScheduledActionOutcome.Started).ToListAsync(cancellationToken);
        foreach (var run in interrupted)
        {
            run.Outcome = ScheduledActionOutcome.Interrupted;
            run.Reason = InterruptedReason;
            run.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (interrupted.Count > 0)
        {
            logger.LogInformation("Marked {Count} scheduled action run(s) interrupted by the last service stop.", interrupted.Count);
        }

        // Filtered in memory: SQLite cannot compare DateTimeOffset columns, and the table holds at most a month of rows.
        var cutoff = now - RetainRunsFor;
        var stale = (await db.ScheduledActionRuns.AsNoTracking().Select(r => new { r.Id, r.StartedAt }).ToListAsync(cancellationToken))
            .Where(r => r.StartedAt < cutoff)
            .Select(r => r.Id)
            .ToList();
        if (stale.Count > 0)
        {
            var pruned = await db.ScheduledActionRuns.Where(r => stale.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation("Pruned {Count} scheduled action run(s) older than {Days} days.", pruned, RetainRunsFor.TotalDays);
        }
    }

    /// <summary>One runner pass for the current minute; exposed so tests can drive it without waiting for the timer.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var zone = timeProvider.LocalTimeZone;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var instances = await db.Instances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(cancellationToken);
        var actions = await db.ScheduledActions.AsNoTracking().Where(a => a.Enabled).ToListAsync(cancellationToken);

        foreach (var instance in instances)
        {
            var due = ScheduleOccurrences.DueInMinute(ScheduleOccurrences.Applicable(instance, actions), now, zone);
            var startedThisMinute = false;
            foreach (var (action, occurrence) in due)
            {
                var run = new ScheduledActionRun
                {
                    ScheduledActionId = action.Id,
                    InstanceId = instance.Id,
                    LocalDate = occurrence.LocalDate,
                    StartedAt = now,
                    Outcome = ScheduledActionOutcome.Started,
                };

                IInstanceLease? lease = null;
                if (occurrence.SkipReason is { } gap)
                {
                    Skip(run, gap);
                }
                else if (processManager.GetRuntime(instance.Id).State != InstanceState.Running)
                {
                    Skip(run, NotRunningReason);
                }
                else if (gate.IsHeldExclusively)
                {
                    Skip(run, UpdateInProgressReason);
                }
                else if (startedThisMinute || (_inFlight.TryGetValue(instance.Id, out var running) && !running.IsCompleted))
                {
                    Skip(run, ActionInProgressReason);
                }
                else if ((lease = locks.TryAcquire(instance.Id)) is null)
                {
                    Skip(run, LockBusyReason);
                }

                if (!await TryClaimAsync(db, run, cancellationToken))
                {
                    // Already ran (or was skipped) today: a second tick in the same minute, or a restart within it.
                    lease?.Dispose();
                    continue;
                }

                if (run.Outcome == ScheduledActionOutcome.Skipped)
                {
                    logger.LogInformation("Scheduled {Kind} (action {ActionId}) for instance {InstanceId} skipped: {Reason}", action.Kind, action.Id, instance.Id, run.Reason);
                    continue;
                }

                startedThisMinute = true;
                logger.LogInformation("Scheduled {Kind} (action {ActionId}) for instance {InstanceId} started; deadline {Deadline:O}.", action.Kind, action.Id, instance.Id, occurrence.Deadline);
                _inFlight[instance.Id] = RunOneAsync(run.Id, instance.Id, action, occurrence.Deadline!.Value, lease!, cancellationToken);
            }
        }
    }

    /// <summary>Completes when every detached run has finished; for tests.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    private void Skip(ScheduledActionRun run, string reason)
    {
        run.Outcome = ScheduledActionOutcome.Skipped;
        run.Reason = reason;
        run.CompletedAt = timeProvider.GetUtcNow();
    }

    /// <summary>Inserts the run as the day's claim; false when the unique index says the day is already claimed.</summary>
    private static async Task<bool> TryClaimAsync(AppDbContext db, ScheduledActionRun run, CancellationToken cancellationToken)
    {
        db.ScheduledActionRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            db.Entry(run).State = EntityState.Detached;
            return false;
        }
    }

    private async Task RunOneAsync(int runId, int instanceId, ScheduledAction action, DateTimeOffset deadline, IInstanceLease lease, CancellationToken cancellationToken)
    {
        OperationOutcome outcome;
        try
        {
            switch (action.Kind)
            {
                case ScheduledActionKind.Restart:
                    // The restart takes the instance lock itself and the locks are not reentrant: the lease that served
                    // as the precondition probe is released first, so the restart's own acquisition succeeds.
                    lease.Dispose();
                    outcome = await processManager.RestartWithCountdownAsync(instanceId, deadline, cancellationToken);
                    break;
                case ScheduledActionKind.DinoWipe:
                    // Under the lease for the whole countdown, so no stop or backup slips in before the wipe.
                    outcome = await processManager.BroadcastCountdownAsync(instanceId, deadline, DinoWipeCountdownTemplate, cancellationToken);
                    if (outcome.Succeeded)
                    {
                        outcome = ToOutcome(await rconOperations.ExecuteAsync(instanceId, RconCommands.DestroyWildDinos, cancellationToken));
                    }

                    break;
                case ScheduledActionKind.RconCommand:
                    outcome = ToOutcome(await rconOperations.ExecuteAsync(instanceId, action.Command, cancellationToken));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown scheduled action kind {action.Kind}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service stop: the row stays Started and the next start marks it Interrupted.
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled {Kind} (action {ActionId}) for instance {InstanceId} threw.", action.Kind, action.Id, instanceId);
            outcome = OperationOutcome.Rejected(ex.Message);
        }
        finally
        {
            lease.Dispose();
        }

        await CompleteAsync(runId, outcome, cancellationToken);
    }

    private async Task CompleteAsync(int runId, OperationOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var run = await db.ScheduledActionRuns.SingleAsync(r => r.Id == runId, cancellationToken);
            run.Outcome = outcome.Succeeded ? ScheduledActionOutcome.Succeeded : ScheduledActionOutcome.Failed;
            run.Reason = outcome.Succeeded ? string.Empty : outcome.Error ?? "The operation was rejected without a reason.";
            run.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            if (!outcome.Succeeded)
            {
                logger.LogWarning("Scheduled action run {RunId} failed: {Reason}", runId, run.Reason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "Could not record the outcome of scheduled action run {RunId}.", runId);
        }
    }

    private static OperationOutcome ToOutcome<T>(Commands.CommandResult<T> result) =>
        result.Succeeded ? OperationOutcome.Success : OperationOutcome.Rejected(result.Error ?? "The command failed without a reason.");

    private TimeSpan UntilNextMinute()
    {
        var now = timeProvider.GetUtcNow();
        var nextMinute = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)) + Tick;
        return nextMinute - now;
    }
}
