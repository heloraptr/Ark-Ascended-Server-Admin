using System.Collections.Concurrent;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests;

/// <summary>
/// A clock whose delays complete immediately (on the thread pool) while advancing the clock by the requested
/// amount, so quiescence windows and retry waits run in milliseconds.
/// </summary>
public sealed class FastTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _now = start;

    /// <summary>The zone <see cref="LocalTimeZone"/> reports; the machine's unless a test pins one.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_sync)
        {
            _now += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoOpTimer();
    }

    private sealed class NoOpTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A clock that moves only when the test calls <see cref="Advance"/>; a timer fires (on the thread pool) once the
/// clock reaches its due time. Unlike <see cref="FastTimeProvider"/>, which fires every timer at once, this one
/// lets a test hold a hosted loop between ticks and show how far apart its attempts are.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<TaskCompletionSource> _pendingWaiters = [];
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    /// <summary>How many timers are waiting to fire.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    /// <summary>
    /// Completes once at least one timer is waiting to fire. A loop that has registered its next delay is parked
    /// on it, so a test that waits here before advancing knows the advance cannot race the registration.
    /// </summary>
    public Task WaitForPendingTimerAsync()
    {
        lock (_sync)
        {
            if (_timers.Any(t => t.Due is not null))
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingWaiters.Add(waiter);
            return waiter.Task;
        }
    }

    /// <summary>Moves the clock forward and fires every timer whose due time it reached.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_sync)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            foreach (var timer in due)
            {
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan || timer.Period == TimeSpan.Zero ? null : _now + timer.Period;
            }
        }

        foreach (var timer in due)
        {
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        TaskCompletionSource[] waiters = [];
        lock (_sync)
        {
            if (!_timers.Contains(timer))
            {
                return;
            }

            timer.Period = period;
            timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            if (timer.Due is not null)
            {
                waiters = [.. _pendingWaiters];
                _pendingWaiters.Clear();
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>When the timer fires next; null while it is stopped. Guarded by the owner's lock.</summary>
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A readiness monitor the test moves by hand; <see cref="Subscribed"/> completes when someone starts waiting on it.</summary>
public sealed class FakeReadinessMonitor(ReadinessPhase phase = ReadinessPhase.Initializing) : IReadinessMonitor
{
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<ReadinessState>? _changed;

    public ReadinessState Current { get; private set; } = new(phase, phase.ToString(), null, DateTimeOffset.UnixEpoch);

    public Task Subscribed => _subscribed.Task;

    public event Action<ReadinessState>? Changed
    {
        add
        {
            _changed += value;
            _subscribed.TrySetResult();
        }
        remove => _changed -= value;
    }

    public void Report(ReadinessPhase next)
    {
        Current = new ReadinessState(next, next.ToString(), null, DateTimeOffset.UnixEpoch);
        _changed?.Invoke(Current);
    }
}

/// <summary>Counts calls into a faked dependency; <see cref="WhenAttempt"/> completes when the n-th call arrives.</summary>
public sealed class AttemptLog
{
    private readonly object _sync = new();
    private readonly List<TaskCompletionSource> _signals = [];
    private int _count;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Records one call and returns its number, starting at 1.</summary>
    public int Record()
    {
        TaskCompletionSource signal;
        int number;
        lock (_sync)
        {
            number = ++_count;
            signal = Signal(number);
        }

        signal.TrySetResult();
        return number;
    }

    public Task WhenAttempt(int number)
    {
        lock (_sync)
        {
            return Signal(number).Task;
        }
    }

    private TaskCompletionSource Signal(int number)
    {
        while (_signals.Count < number)
        {
            _signals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return _signals[number - 1];
    }
}

/// <summary>A logger that keeps every entry so a test can assert what was logged and at which level.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Errors => [.. _entries.Where(e => e.Level == LogLevel.Error)];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Warnings => [.. _entries.Where(e => e.Level == LogLevel.Warning)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>Per-instance locks backed by semaphores; <see cref="Holders"/> shows who currently holds one.</summary>
public sealed class FakeInstanceLocks : IInstanceLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public ConcurrentDictionary<int, int> Holders { get; } = new();

    public ConcurrentDictionary<int, int> ReservedClusters { get; } = new();

    public IDisposable? TryReserveCluster(int clusterId) =>
        ReservedClusters.TryAdd(clusterId, 1) ? new ClusterLease(this, clusterId) : null;

    public bool IsClusterReserved(int clusterId) => ReservedClusters.ContainsKey(clusterId);

    private sealed class ClusterLease(FakeInstanceLocks owner, int clusterId) : IDisposable
    {
        public void Dispose() => owner.ReservedClusters.TryRemove(clusterId, out _);
    }

    public IInstanceLease? TryAcquire(int instanceId)
    {
        var gate = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            return null;
        }

        Holders[instanceId] = 1;
        return new Lease(this, instanceId, gate);
    }

    public async Task<IInstanceLease> AcquireAsync(int instanceId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        Holders[instanceId] = 1;
        return new Lease(this, instanceId, gate);
    }

    private sealed class Lease(FakeInstanceLocks owner, int instanceId, SemaphoreSlim gate) : IInstanceLease
    {
        public int InstanceId { get; } = instanceId;

        public bool IsReleased { get; private set; }

        public void Dispose()
        {
            if (IsReleased)
            {
                return;
            }

            IsReleased = true;
            owner.Holders.TryRemove(InstanceId, out _);
            gate.Release();
        }
    }
}

/// <summary>Exclusive-only maintenance gate that records how often it was taken.</summary>
public sealed class FakeMaintenanceGate : IMaintenanceGate
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    public int ExclusiveAcquisitions { get; private set; }

    public bool IsHeldExclusively { get; private set; }

    public async Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        await _exclusive.WaitAsync(cancellationToken);
        IsHeldExclusively = true;
        ExclusiveAcquisitions++;
        return new Release(this);
    }

    public IDisposable? TryAcquireShared() => IsHeldExclusively ? null : new NoOp();

    private sealed class Release(FakeMaintenanceGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.IsHeldExclusively = false;
            gate._exclusive.Release();
        }
    }

    private sealed class NoOp : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A scriptable process manager: runtimes are set by the test; Stop moves an instance to Stopped and Start to
/// Running unless a scripted outcome says otherwise. Every call is recorded with the gate state at the time.
/// </summary>
public sealed class FakeProcessManager(FakeMaintenanceGate? gate = null) : IProcessManager
{
    private readonly ConcurrentDictionary<int, InstanceRuntime> _runtimes = new();

    public ConcurrentDictionary<int, OperationOutcome> StopOutcomes { get; } = new();

    public ConcurrentDictionary<int, OperationOutcome> StartOutcomes { get; } = new();

    /// <summary>When set, every Start waits on it before completing (to observe hand-off behavior).</summary>
    public TaskCompletionSource? StartBarrier { get; set; }

    public List<(int InstanceId, StopOptions Options)> Stops { get; } = [];

    public List<(int InstanceId, LaunchKind Kind, bool GateHeldExclusively)> Starts { get; } = [];

    public event Action<InstanceRuntime>? RuntimeChanged;

    public event Action<ProbeObservation>? ProbeObserved;

    public event Action<int, InstanceTelemetry?>? TelemetryChanged;

    /// <summary>Scripted samples for <see cref="GetTelemetry"/>; <see cref="Publish"/> sets or clears one and raises the event.</summary>
    public ConcurrentDictionary<int, InstanceTelemetry> Telemetry { get; } = new();

    public void Publish(int instanceId, InstanceTelemetry? sample)
    {
        if (sample is null)
        {
            Telemetry.TryRemove(instanceId, out _);
        }
        else
        {
            Telemetry[instanceId] = sample;
        }

        TelemetryChanged?.Invoke(instanceId, sample);
    }

    public InstanceTelemetry? GetTelemetry(int instanceId) => Telemetry.TryGetValue(instanceId, out var sample) ? sample : null;

    /// <summary>Scripted answers for <see cref="ProbeSessionAsync"/>; Unknown when absent.</summary>
    public ConcurrentDictionary<int, SessionLiveness> Liveness { get; } = new();

    public void Observe(ProbeObservation observation) => ProbeObserved?.Invoke(observation);

    public Task<SessionLiveness> ProbeSessionAsync(int instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(Liveness.TryGetValue(instanceId, out var liveness) ? liveness : SessionLiveness.Unknown);

    /// <summary>Scripted: held with a no-op lease unless <see cref="ProjectionRefusal"/> is set.</summary>
    public string? ProjectionRefusal { get; set; }

    public Task<ProjectionReservationResult> TryReserveProjectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProjectionRefusal is { } reason ? new ProjectionReservationResult(null, reason) : new ProjectionReservationResult(new NoOpLease(), null));

    private sealed class NoOpLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>The start time a live runtime carries when a test does not name one; with the pid it makes up the session identity.</summary>
    public static readonly DateTimeOffset DefaultStartTime = new(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Publishes a runtime. A live one carries pid <c>1000 + instanceId</c> and <paramref name="startTime"/>, or
    /// <see cref="DefaultStartTime"/> when none is given; passing a second start time stands for a restart.
    /// </summary>
    public void Set(int instanceId, InstanceState state, DateTimeOffset? startTime = null)
    {
        var live = state is not (InstanceState.Stopped or InstanceState.Crashed);
        var runtime = new InstanceRuntime(instanceId, state, live ? 1000 + instanceId : null, live ? startTime ?? DefaultStartTime : null, null, null, null);
        _runtimes[instanceId] = runtime;
        RuntimeChanged?.Invoke(runtime);
    }

    public InstanceRuntime GetRuntime(int instanceId) =>
        _runtimes.TryGetValue(instanceId, out var runtime) ? runtime : new InstanceRuntime(instanceId, InstanceState.Stopped, null, null, null, null, null);

    public IReadOnlyList<InstanceRuntime> GetAllRuntimes() => _runtimes.Values.OrderBy(r => r.InstanceId).ToList();

    public async Task<OperationOutcome> StartAsync(int instanceId, LaunchKind kind, CancellationToken cancellationToken)
    {
        lock (Starts)
        {
            Starts.Add((instanceId, kind, gate?.IsHeldExclusively ?? false));
        }

        if (StartBarrier is { } barrier)
        {
            await barrier.Task.WaitAsync(cancellationToken);
        }

        if (StartOutcomes.TryGetValue(instanceId, out var scripted) && !scripted.Succeeded)
        {
            return scripted;
        }

        Set(instanceId, InstanceState.Running);
        return OperationOutcome.Success;
    }

    public Task<OperationOutcome> StopAsync(int instanceId, StopOptions options, CancellationToken cancellationToken)
    {
        lock (Stops)
        {
            Stops.Add((instanceId, options));
        }

        if (StopOutcomes.TryGetValue(instanceId, out var scripted) && !scripted.Succeeded)
        {
            return Task.FromResult(scripted);
        }

        Set(instanceId, InstanceState.Stopped);
        return Task.FromResult(OperationOutcome.Success);
    }

    /// <summary>Recorded in <see cref="Stops"/> like a plain stop; the lease is the caller's and is left alone.</summary>
    public Task<OperationOutcome> StopUnderLeaseAsync(IInstanceLease lease, StopOptions options, CancellationToken cancellationToken) =>
        StopAsync(lease.InstanceId, options, cancellationToken);

    public Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Restarts recorded with their deadline; scripted by <see cref="RestartOutcomes"/>, else success with the instance left Running.</summary>
    public List<(int InstanceId, DateTimeOffset Deadline)> Restarts { get; } = [];

    public ConcurrentDictionary<int, OperationOutcome> RestartOutcomes { get; } = new();

    public Task<OperationOutcome> RestartWithCountdownAsync(int instanceId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        lock (Restarts)
        {
            Restarts.Add((instanceId, deadline));
        }

        return Task.FromResult(RestartOutcomes.TryGetValue(instanceId, out var scripted) ? scripted : OperationOutcome.Success);
    }

    public bool TrySkipCountdown(int instanceId) => false;

    /// <summary>Countdowns recorded with their deadline and template; they complete at once with success.</summary>
    public List<(int InstanceId, DateTimeOffset Deadline, string MessageTemplate)> Countdowns { get; } = [];

    public Task<OperationOutcome> BroadcastCountdownAsync(int instanceId, DateTimeOffset deadline, string messageTemplate, CancellationToken cancellationToken)
    {
        lock (Countdowns)
        {
            Countdowns.Add((instanceId, deadline, messageTemplate));
        }

        return Task.FromResult(OperationOutcome.Success);
    }

    public Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Every <see cref="RecoverAsync"/> call in order, with the wall-clock instant it arrived.</summary>
    public List<(RecoveryRequest Request, DateTimeOffset At)> Recoveries { get; } = [];

    /// <summary>Scripted answers for <see cref="RecoverAsync"/>; Launched(1) when null. May block or throw.</summary>
    public Func<RecoveryRequest, CancellationToken, Task<CrashRecovery>>? RecoverHandler { get; set; }

    public async Task<CrashRecovery> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken)
    {
        lock (Recoveries)
        {
            Recoveries.Add((request, DateTimeOffset.UtcNow));
        }

        return RecoverHandler is { } handler ? await handler(request, cancellationToken) : new CrashRecovery(CrashRecoveryStatus.Launched, 1);
    }

    public ConcurrentQueue<int> Dismissals { get; } = new();

    public void DismissCrash(int instanceId) => Dismissals.Enqueue(instanceId);
}

/// <summary>In-memory console that keeps every appended line per channel.</summary>
public sealed class FakeConsoleService : IConsoleService
{
    private readonly ConcurrentDictionary<string, List<ConsoleLine>> _lines = new();

    public event Action<string, ConsoleLine>? LineAppended;

    public event Action<string>? Cleared;

    public IReadOnlyList<ConsoleLine> Snapshot(string channel) =>
        _lines.TryGetValue(channel, out var lines) ? lines.ToList() : [];

    public void Append(string channel, ConsoleLine line)
    {
        _lines.GetOrAdd(channel, _ => []).Add(line);
        LineAppended?.Invoke(channel, line);
    }

    public void Clear(string channel)
    {
        _lines.TryRemove(channel, out _);
        Cleared?.Invoke(channel);
    }
}

/// <summary>Enough of the junction layout for delete tests: retire moves or deletes the instance's <c>Saved</c>.</summary>
public sealed class FakeInstanceLayoutService(TempDataRoot root, TimeProvider timeProvider) : IInstanceLayoutService
{
    public List<string> RemovedJunctions { get; } = [];

    public Task EnsureAsync(string slug, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root.Layout.InstanceSavedDirectory(slug));
        return Task.CompletedTask;
    }

    public bool IsComplete(string slug) => Directory.Exists(root.Layout.InstanceSavedDirectory(slug));

    public Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken)
    {
        RemovedJunctions.Add(slug);
        return Task.CompletedTask;
    }

    public Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken)
    {
        var saved = root.Layout.InstanceSavedDirectory(slug);
        string? archive = null;
        if (keepWorldData && Directory.Exists(saved))
        {
            archive = root.Layout.ArchiveDirectory(slug, timeProvider.GetUtcNow());
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            Directory.Move(saved, archive);
        }

        var instanceDirectory = root.Layout.InstanceDirectory(slug);
        if (Directory.Exists(instanceDirectory))
        {
            Directory.Delete(instanceDirectory, recursive: true);
        }

        return Task.FromResult(archive);
    }
}

/// <summary>Seeds the rows the backup, update, and delete tests need.</summary>
public static class TestSeed
{
    public static async Task<Instance> InstanceAsync(TempDataRoot root, string slug, bool clustered, CancellationToken cancellationToken, Action<Instance>? configure = null)
    {
        await using var db = root.CreateDbContext();
        var map = await db.Maps.SingleAsync(m => m.Key == "TheIsland_WP", cancellationToken);
        Cluster? cluster = null;
        if (clustered)
        {
            cluster = await db.Clusters.SingleOrDefaultAsync(c => c.Slug == "cluster", cancellationToken);
            if (cluster is null)
            {
                cluster = new Cluster { Name = "Cluster", Slug = "cluster", ClusterKey = "cluster", CreatedAt = DateTimeOffset.UnixEpoch };
                db.Clusters.Add(cluster);
            }
        }

        var instance = new Instance
        {
            Name = slug,
            Slug = slug,
            SessionName = slug,
            MapId = map.Id,
            Cluster = cluster,
            GamePort = 7777,
            RconPort = 27020,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        configure?.Invoke(instance);
        db.Instances.Add(instance);
        await db.SaveChangesAsync(cancellationToken);
        instance.Map = map;
        return instance;
    }

    public static async Task MaintenanceAsync(TempDataRoot root, MaintenancePhase phase, IEnumerable<MaintenanceEntry> entries, CancellationToken cancellationToken)
    {
        await using var db = root.CreateDbContext();
        var row = await db.MaintenanceStates.SingleAsync(cancellationToken);
        row.Phase = phase;
        row.Entries = [.. entries];
        row.StartedAt = phase == MaintenancePhase.None ? null : DateTimeOffset.UnixEpoch;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<MaintenanceState> MaintenanceRowAsync(TempDataRoot root, CancellationToken cancellationToken)
    {
        await using var db = root.CreateDbContext();
        return await db.MaintenanceStates.AsNoTracking().SingleAsync(cancellationToken);
    }
}
