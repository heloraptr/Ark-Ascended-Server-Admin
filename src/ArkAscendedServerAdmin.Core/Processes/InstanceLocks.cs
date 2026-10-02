using System.Collections.Concurrent;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// One <see cref="SemaphoreSlim"/> per instance id (plan step 19). Semaphores are created on first use and
/// never removed: an instance id is a small integer and a deleted instance's semaphore is harmless.
/// </summary>
public sealed class InstanceLocks : IInstanceLocks
{
    /// <summary>The refusal for anything a restore's cluster reservation (<see cref="TryReserveCluster"/>) holds off.</summary>
    public const string ClusterReservedByRestore = "The cluster is reserved by a restore; try again when it finishes.";

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _clusters = new();

    public IInstanceLease? TryAcquire(int instanceId)
    {
        var semaphore = Get(instanceId);
        return semaphore.Wait(0) ? new Releaser(instanceId, semaphore) : null;
    }

    public async Task<IInstanceLease> AcquireAsync(int instanceId, CancellationToken cancellationToken)
    {
        var semaphore = Get(instanceId);
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(instanceId, semaphore);
    }

    public IDisposable? TryReserveCluster(int clusterId)
    {
        var semaphore = _clusters.GetOrAdd(clusterId, _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0) ? new Releaser(clusterId, semaphore) : null;
    }

    public bool IsClusterReserved(int clusterId) => _clusters.TryGetValue(clusterId, out var semaphore) && semaphore.CurrentCount == 0;

    /// <summary>True while some operation holds the instance's lock; for diagnostics and tests only.</summary>
    public bool IsHeld(int instanceId) => _semaphores.TryGetValue(instanceId, out var semaphore) && semaphore.CurrentCount == 0;

    private SemaphoreSlim Get(int instanceId) => _semaphores.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));

    private sealed class Releaser(int instanceId, SemaphoreSlim semaphore) : IInstanceLease
    {
        private int _released;

        public int InstanceId { get; } = instanceId;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
