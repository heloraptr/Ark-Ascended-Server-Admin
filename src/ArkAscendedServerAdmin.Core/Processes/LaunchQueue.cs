using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// The global launch queue (plan steps 19, 25): the single place game-server launches run. One worker
/// drains the queue FIFO, applying <see cref="AppSettings.StaggerDelaySeconds"/> between consecutive
/// <i>successful</i> launches through <see cref="TimeProvider"/> so tests can drive it with a fake clock.
/// The launch callback itself takes the maintenance gate shared before <c>Process.Start</c> and releases
/// it after identity persistence; the queue only orders and paces.
/// </summary>
/// <remarks>
/// The caller's cancellation token covers the <i>queued</i> phase only: once a launch callback has
/// started, it runs to completion on the queue's own lifetime token so a closed browser tab never leaves
/// a half-registered process behind.
/// </remarks>
public sealed class LaunchQueue : IDisposable
{
    private readonly MaintenanceGate _gate;
    private readonly IAppSettingsStore _settings;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _sync = new();
    private readonly Queue<LaunchEntry> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Task? _worker;
    private DateTimeOffset? _nextLaunchAllowedAt;

    public LaunchQueue(MaintenanceGate gate, IAppSettingsStore settings, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _gate = gate;
        _settings = settings;
        _timeProvider = timeProvider;
        _gate.RegisterDrain(Drain);
    }

    /// <summary>The reservation every launch holds shared through registration and a file projection holds exclusively (B0).</summary>
    public ProjectionReservation Reservation { get; } = new();

    /// <summary>Launches still waiting for the worker; for diagnostics and tests.</summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Enqueues <paramref name="launch"/> and completes with its outcome, or with a rejection when the
    /// queue is drained, the gate is held exclusively when the entry's turn comes, or the caller cancels
    /// before the launch starts. Exceptions thrown by the callback become rejections.
    /// </summary>
    public Task<OperationOutcome> EnqueueAsync(int instanceId, LaunchKind kind, Func<CancellationToken, Task<OperationOutcome>> launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var entry = new LaunchEntry(instanceId, kind, launch);
        if (cancellationToken.CanBeCanceled)
        {
            entry.CallerCancellation = cancellationToken.Register(
                () => entry.TryComplete(OperationOutcome.Rejected("Launch cancelled before it started.")));
        }

        lock (_sync)
        {
            _pending.Enqueue(entry);
            _worker ??= Task.Run(WorkAsync);
        }

        _signal.Release();
        return entry.Completion.Task;
    }

    /// <summary>Completes every queued-but-not-started launch with <c>Rejected(reason)</c>; the in-flight launch is untouched.</summary>
    public void Drain(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        List<LaunchEntry> drained;
        lock (_sync)
        {
            drained = [.. _pending];
            _pending.Clear();
        }

        foreach (var entry in drained)
        {
            entry.TryComplete(OperationOutcome.Rejected(reason));
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        Drain("The service is shutting down.");
        _shutdown.Dispose();
    }

    private async Task WorkAsync()
    {
        var shutdown = _shutdown.Token;
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(shutdown);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LaunchEntry? entry;
            lock (_sync)
            {
                _pending.TryDequeue(out entry);
            }

            if (entry is null || entry.Completion.Task.IsCompleted)
            {
                continue;
            }

            await RunAsync(entry, shutdown);
        }
    }

    private async Task RunAsync(LaunchEntry entry, CancellationToken shutdown)
    {
        try
        {
            if (_gate.IsHeldExclusively)
            {
                entry.TryComplete(OperationOutcome.Rejected(MaintenanceGate.UpdateInProgress));
                return;
            }

            if (_nextLaunchAllowedAt is { } notBefore)
            {
                var wait = notBefore - _timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _timeProvider, shutdown);
                }
            }

            // Detach the caller's cancellation first (Dispose waits for an in-flight callback), so a
            // launch that starts can no longer be reported as cancelled.
            entry.CallerCancellation.Dispose();
            if (entry.Completion.Task.IsCompleted)
            {
                return;
            }

            if (_gate.IsHeldExclusively)
            {
                entry.TryComplete(OperationOutcome.Rejected(MaintenanceGate.UpdateInProgress));
                return;
            }

            var outcome = await entry.Launch(shutdown);
            if (outcome.Succeeded)
            {
                var settings = await _settings.GetAsync(shutdown);
                _nextLaunchAllowedAt = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(0, settings.StaggerDelaySeconds));
            }

            entry.TryComplete(outcome);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            entry.TryComplete(OperationOutcome.Rejected("The service is shutting down."));
        }
        catch (Exception ex)
        {
            entry.TryComplete(OperationOutcome.Rejected(ex.Message));
        }
    }

    private sealed class LaunchEntry(int instanceId, LaunchKind kind, Func<CancellationToken, Task<OperationOutcome>> launch)
    {
        public int InstanceId { get; } = instanceId;

        public LaunchKind Kind { get; } = kind;

        public Func<CancellationToken, Task<OperationOutcome>> Launch { get; } = launch;

        public TaskCompletionSource<OperationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CallerCancellation { get; set; }

        public void TryComplete(OperationOutcome outcome)
        {
            if (Completion.TrySetResult(outcome))
            {
                CallerCancellation.Dispose();
            }
        }
    }
}
