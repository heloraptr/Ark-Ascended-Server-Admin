using System.Collections.Concurrent;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// One <see cref="SemaphoreSlim"/> per instance id (plan step 19). Semaphores are created on first use and
/// never removed: an instance id is a small integer and a deleted instance's semaphore is harmless.
/// </summary>
public sealed class InstanceLocks : IInstanceLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();

    public IDisposable? TryAcquire(int instanceId)
    {
        var semaphore = Get(instanceId);
        return semaphore.Wait(0) ? new Releaser(semaphore) : null;
    }

    public async Task<IDisposable> AcquireAsync(int instanceId, CancellationToken cancellationToken)
    {
        var semaphore = Get(instanceId);
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    /// <summary>True while some operation holds the instance's lock; for diagnostics and tests only.</summary>
    public bool IsHeld(int instanceId) => _semaphores.TryGetValue(instanceId, out var semaphore) && semaphore.CurrentCount == 0;

    private SemaphoreSlim Get(int instanceId) => _semaphores.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
