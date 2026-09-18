using System.Collections.Concurrent;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests;

/// <summary>
/// A clock whose delays complete immediately (on the thread pool) while advancing the clock by the requested
/// amount, so quiescence windows and retry waits run in milliseconds.
/// </summary>
public sealed class FastTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _now = start;

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

/// <summary>Per-instance locks backed by semaphores; <see cref="Holders"/> shows who currently holds one.</summary>
public sealed class FakeInstanceLocks : IInstanceLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public ConcurrentDictionary<int, int> Holders { get; } = new();

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

    public void Set(int instanceId, InstanceState state)
    {
        var runtime = new InstanceRuntime(instanceId, state, state == InstanceState.Stopped ? null : 1000 + instanceId, null, null, null, null);
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

    public Task<OperationOutcome> RestartWithCountdownAsync(int instanceId, DateTimeOffset deadline, CancellationToken cancellationToken) => throw new NotSupportedException();

    public bool TrySkipCountdown(int instanceId) => false;

    public Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
