namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// The maintenance gate (plan step 19) with reader/writer semantics over async waits. An exclusive
/// acquire (install/update) first runs the registered drain callback so the launch queue rejects every
/// queued-but-not-started launch, then refuses new shared leases and waits for the in-flight ones to be
/// released. Exclusive acquires are serialized among themselves.
/// </summary>
/// <remarks>
/// <see cref="IsHeldExclusively"/> is true from the moment an exclusive acquire begins (while it waits for
/// shared leases to drain) until the exclusive holder disposes, so a Start requested during the drain is
/// rejected with "update in progress" rather than sneaking in ahead of the update.
/// </remarks>
public sealed class MaintenanceGate : IMaintenanceGate
{
    /// <summary>The rejection reason handed to drained launches and to <c>TryAcquireShared</c> callers.</summary>
    public const string UpdateInProgress = "update in progress";

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _exclusiveTurn = new(1, 1);
    private readonly List<Action<string>> _drains = [];
    private int _sharedCount;
    private bool _exclusive;
    private TaskCompletionSource? _drained;

    public bool IsHeldExclusively
    {
        get
        {
            lock (_sync)
            {
                return _exclusive;
            }
        }
    }

    /// <summary>Number of shared leases currently held; for diagnostics and tests.</summary>
    public int SharedLeaseCount
    {
        get
        {
            lock (_sync)
            {
                return _sharedCount;
            }
        }
    }

    /// <summary>Registers a callback the exclusive acquire runs first; the launch queue uses it to reject queued launches.</summary>
    public void RegisterDrain(Action<string> drain)
    {
        ArgumentNullException.ThrowIfNull(drain);
        lock (_sync)
        {
            _drains.Add(drain);
        }
    }

    public async Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTurn.WaitAsync(cancellationToken);
        try
        {
            Task drained;
            Action<string>[] drains;
            lock (_sync)
            {
                _exclusive = true;
                drains = [.. _drains];
                if (_sharedCount == 0)
                {
                    drained = Task.CompletedTask;
                }
                else
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    drained = _drained.Task;
                }
            }

            foreach (var drain in drains)
            {
                drain(UpdateInProgress);
            }

            await drained.WaitAsync(cancellationToken);
            return new ExclusiveLease(this);
        }
        catch
        {
            lock (_sync)
            {
                _exclusive = false;
                _drained = null;
            }

            _exclusiveTurn.Release();
            throw;
        }
    }

    public IDisposable? TryAcquireShared()
    {
        lock (_sync)
        {
            if (_exclusive)
            {
                return null;
            }

            _sharedCount++;
            return new SharedLease(this);
        }
    }

    private void ReleaseShared()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _sharedCount--;
            if (_sharedCount == 0 && _drained is not null)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void ReleaseExclusive()
    {
        lock (_sync)
        {
            _exclusive = false;
        }

        _exclusiveTurn.Release();
    }

    private sealed class SharedLease(MaintenanceGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.ReleaseShared();
            }
        }
    }

    private sealed class ExclusiveLease(MaintenanceGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.ReleaseExclusive();
            }
        }
    }
}
