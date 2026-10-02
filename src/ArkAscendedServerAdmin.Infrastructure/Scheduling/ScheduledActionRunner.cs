using System.Collections.Concurrent;
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
/// inserting its run for the occurrence. The unique index on (action, instance, scheduled instant) makes that
/// insert the once-per-occurrence guarantee: a second tick in the same minute, or a restart within it, inserts
/// nothing, while a row that fires several times a day gets one run per occurrence. A
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

    /// <summary>When the last retention prune succeeded; the loop prunes again once <see cref="PruneEvery"/> has passed.</summary>
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>How often the runner looks for due rows; each tick is aligned to the start of a minute.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Runs older than this (by <see cref="ScheduledActionRun.StartedAt"/>) are deleted at service start and once a day after.</summary>
    public static TimeSpan RetainRunsFor { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often the tick loop repeats the retention prune, so a service that runs for months does not keep every
    /// run until its next restart.
    /// </summary>
    public static TimeSpan PruneEvery { get; } = TimeSpan.FromHours(24);

    /// <summary>The dino-wipe countdown's broadcast; <c>{0}</c> is "in N minute(s)" or "now".</summary>
    public const string DinoWipeCountdownTemplate = "Wild dinos will be wiped {0}.";

    public const string InterruptedReason = "The service was restarted before the action finished.";

    public const string NotRunningReason = "The server is not running.";

    public const string UpdateInProgressReason = "An update is in progress.";

    public const string ActionInProgressReason = "Another action is in progress.";

    /// <summary>The instance lock was held by something other than a scheduled action (a stop, a backup, a restore).</summary>
    public static string LockBusyReason { get; } = $"The instance is busy ({ProcessManager.OperationInProgress}).";

    /// <summary>
    /// How long a due action waits before its one second try for a busy instance lock. Long enough to outlast the
    /// few milliseconds a console history write holds it; a lock still held after that is a real operation.
    /// </summary>
    public static TimeSpan LockRetryDelay { get; } = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await readiness.WaitUntilReadyAsync(stoppingToken);
            await RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Any failure, not a list of expected ones: an exception escaping ExecuteAsync stops the whole service.
            logger.LogError(ex, "Scheduled action recovery failed; interrupted runs stay Started and old runs are kept until the next start.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNextMinute(), timeProvider, stoppingToken);
                await RunTickAsync(stoppingToken);

                // Inside the try: a failed prune is logged like a failed tick and, since _lastPrune only moves on
                // success, tried again at the next tick.
                var now = timeProvider.GetUtcNow();
                if (now - _lastPrune >= PruneEvery)
                {
                    await PruneAsync(now, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed tick is logged and the next minute tries again; letting it escape would stop the service.
                logger.LogError(ex, "Scheduled action tick failed; it will try again next minute.");
            }
        }
    }

    /// <summary>
    /// The start-of-service pass: every run still <see cref="ScheduledActionOutcome.Started"/> was in flight when
    /// the service last stopped and becomes <see cref="ScheduledActionOutcome.Interrupted"/>; runs older than
    /// <see cref="RetainRunsFor"/> are deleted (<see cref="PruneAsync(DateTimeOffset, CancellationToken)"/>).
    /// Exposed so tests can drive it without hosting the service.
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

        await PruneAsync(db, now, cancellationToken);
    }

    /// <summary>
    /// Deletes runs older than <see cref="RetainRunsFor"/> as of <paramref name="now"/>. Called by
    /// <see cref="RecoverAsync"/> at service start and by the tick loop once every <see cref="PruneEvery"/>.
    /// </summary>
    public async Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await PruneAsync(db, now, cancellationToken);
    }

    /// <summary>The prune on a context the caller already holds, so the start-of-service pass opens only one.</summary>
    private async Task PruneAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
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

        _lastPrune = now;
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
                    ScheduledFor = occurrence.Deadline,
                    StartedAt = now,
                    Outcome = ScheduledActionOutcome.Started,
                };

                IInstanceLease? lease = null;
                if (processManager.GetRuntime(instance.Id).State != InstanceState.Running)
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
                else if ((lease = await TryLeaseAsync(instance.Id, action.Id, cancellationToken)) is null)
                {
                    Skip(run, LockBusyReason);
                }

                bool claimed;
                try
                {
                    claimed = await TryClaimAsync(db, run, cancellationToken);
                }
                catch
                {
                    // The tick ends here and nothing took the lease over: left held, it would refuse every
                    // operation on the instance until the service restarts.
                    lease?.Dispose();
                    throw;
                }

                if (!claimed)
                {
                    // The occurrence was already claimed (run or skipped): a second tick in the same minute, or a restart within it.
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
                _inFlight[instance.Id] = RunOneAsync(run.Id, instance.Id, action, occurrence.Deadline, lease!, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The instance lease, tried a second time after <see cref="LockRetryDelay"/> when the first try finds it held.
    /// The occurrence is claimed only after this returns, so the retry can neither run it twice nor move it into
    /// another minute (due rows are computed from the tick's own clock reading). Instances are evaluated in turn,
    /// so a busy one holds the rest of the tick up by the delay at most once per due action.
    /// </summary>
    private async Task<IInstanceLease?> TryLeaseAsync(int instanceId, int actionId, CancellationToken cancellationToken)
    {
        if (locks.TryAcquire(instanceId) is { } lease)
        {
            return lease;
        }

        logger.LogDebug("Scheduled action {ActionId}: instance {InstanceId} is busy; trying once more in {Delay} ms.", actionId, instanceId, LockRetryDelay.TotalMilliseconds);
        await Task.Delay(LockRetryDelay, timeProvider, cancellationToken);
        return locks.TryAcquire(instanceId);
    }

    /// <summary>Completes when every detached run has finished; for tests.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    private void Skip(ScheduledActionRun run, string reason)
    {
        run.Outcome = ScheduledActionOutcome.Skipped;
        run.Reason = reason;
        run.CompletedAt = timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Inserts the run as the occurrence's claim; false when the unique index says the occurrence is already claimed.
    /// The run is detached on every failure, so a later save on the tick's context does not retry it.
    /// </summary>
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
        catch
        {
            // Any other failure (a busy database, a schedule row deleted since the tick read it) goes to the caller.
            db.Entry(run).State = EntityState.Detached;
            throw;
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
        catch (Exception ex)
        {
            // The run is detached from the tick and nothing observes it, so anything not logged here is lost.
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
