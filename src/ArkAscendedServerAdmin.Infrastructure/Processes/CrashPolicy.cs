using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// The crash policy (B4): the one reader of <see cref="RecoveryRequests"/>. It filters out requested and maintenance
/// exits and instances with <c>AutoRestart</c> off, waits <see cref="RestartDelay"/>, and hands the request to
/// <see cref="IProcessManager.RecoverAsync"/>, which makes the decision and every state change under the instance lease.
/// A busy lease is retried every <see cref="BusyRetryInterval"/> for up to <see cref="BusyRetryWindow"/>. Requests for one
/// instance run in order on their own task chain, so a slow relaunch never holds up another instance. The policy waits
/// for the readiness pipeline to reach Ready before it reads the first request, and at shutdown it waits for every
/// chain still running; a launch already inside the launch queue finishes like any launch.
/// </summary>
public sealed class CrashPolicy(
    RecoveryRequests requests,
    IProcessManager processManager,
    IDbContextFactory<AppDbContext> contextFactory,
    IReadinessMonitor readiness,
    IConsoleService console,
    TimeProvider timeProvider,
    ILogger<CrashPolicy> logger) : BackgroundService
{
    /// <summary>The pause between an unexpected exit and its relaunch: a plain backoff; the launch checks still decide.</summary>
    public static TimeSpan RestartDelay { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a request whose instance lock is busy is tried again.</summary>
    public static TimeSpan BusyRetryInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a busy instance lock is retried before the request is given up for good.</summary>
    public static TimeSpan BusyRetryWindow { get; } = TimeSpan.FromMinutes(2);

    /// <summary>The console line for a request whose lock stayed busy for the whole <see cref="BusyRetryWindow"/>.</summary>
    public const string BusyMessage = "Automatic restart skipped: another operation is using this instance.";

    private readonly Lock _sync = new();
    private readonly Dictionary<int, Task> _chains = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitUntilReadyAsync(stoppingToken);
            await foreach (var request in requests.Reader.ReadAllAsync(stoppingToken))
            {
                Enqueue(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Task[] outstanding;
            lock (_sync)
            {
                outstanding = [.. _chains.Values];
            }

            // Chains never throw (see RunAfterAsync); delays and retries end on the stopping token.
            await Task.WhenAll(outstanding);
        }
    }

    /// <summary>Appends the request to its instance's chain; the entry is removed when the chain's last link completes.</summary>
    private void Enqueue(RecoveryRequest request, CancellationToken stoppingToken)
    {
        var instanceId = request.InstanceId;
        lock (_sync)
        {
            var previous = _chains.TryGetValue(instanceId, out var tail) ? tail : Task.CompletedTask;
            var link = Task.Run(() => RunAfterAsync(previous, request, stoppingToken), CancellationToken.None);
            _chains[instanceId] = link;
            _ = link.ContinueWith(
                completed =>
                {
                    lock (_sync)
                    {
                        if (_chains.TryGetValue(instanceId, out var current) && current == completed)
                        {
                            _chains.Remove(instanceId);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAfterAsync(Task previous, RecoveryRequest request, CancellationToken stoppingToken)
    {
        await previous;
        try
        {
            await HandleAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Automatic restart for instance {InstanceId} was abandoned by the service shutdown.", request.InstanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic restart for instance {InstanceId} (pid {Pid}) failed.", request.InstanceId, request.Pid);
        }
    }

    private async Task HandleAsync(RecoveryRequest request, CancellationToken stoppingToken)
    {
        if (request.StopIntent || request.DuringMaintenance)
        {
            return;
        }

        // A cheap pre-filter; RecoverAsync re-reads the row under the instance lease and decides.
        bool? autoRestart;
        await using (var db = await contextFactory.CreateDbContextAsync(stoppingToken))
        {
            autoRestart = await db.Instances.AsNoTracking()
                .Where(i => i.Id == request.InstanceId)
                .Select(i => (bool?)i.AutoRestart)
                .SingleOrDefaultAsync(stoppingToken);
        }

        if (autoRestart is not true)
        {
            return;
        }

        await Task.Delay(RestartDelay, timeProvider, stoppingToken);
        var giveUpAt = timeProvider.GetUtcNow() + BusyRetryWindow;
        var result = await processManager.RecoverAsync(request, stoppingToken);
        while (result.Status == CrashRecoveryStatus.Busy)
        {
            if (timeProvider.GetUtcNow() >= giveUpAt)
            {
                Report(request.InstanceId, BusyMessage, ConsoleLineKind.Warning);
                return;
            }

            await Task.Delay(BusyRetryInterval, timeProvider, stoppingToken);
            result = await processManager.RecoverAsync(request, stoppingToken);
        }

        switch (result.Status)
        {
            case CrashRecoveryStatus.GaveUp:
                Report(request.InstanceId, CrashLoopRule.GaveUpMessage, ConsoleLineKind.Warning);
                break;
            case CrashRecoveryStatus.Refused:
                Report(request.InstanceId, $"Automatic restart was refused: {result.Reason}", ConsoleLineKind.Warning);
                break;
            case CrashRecoveryStatus.Skipped:
                Report(request.InstanceId, $"Automatic restart skipped: {result.Reason?.TrimEnd('.')}.", ConsoleLineKind.Info);
                break;
            case CrashRecoveryStatus.Launched when result.Reason is not null:
                // The relaunched process runs; its launch failed afterwards (an identity that could not be saved), which the
                // manager already reported on the console and in the instance's state. Not a refusal.
                logger.LogWarning("Instance {InstanceId}: automatic restart {Attempt} launched, but: {Reason}", request.InstanceId, result.Attempt, result.Reason);
                break;
            default:
                // Launched (the manager wrote the "Restarting…" line), Disabled, Stale: the log is enough.
                logger.LogInformation("Instance {InstanceId}: crash recovery for pid {Pid} answered {Status} (attempt {Attempt}).", request.InstanceId, request.Pid, result.Status, result.Attempt);
                break;
        }
    }

    private void Report(int instanceId, string text, ConsoleLineKind kind)
    {
        logger.Log(kind == ConsoleLineKind.Warning ? LogLevel.Warning : LogLevel.Information, "Instance {InstanceId}: {Message}", instanceId, text);
        console.Append(ConsoleChannels.Instance(instanceId), new ConsoleLine(timeProvider.GetUtcNow(), text, kind));
    }

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
}
