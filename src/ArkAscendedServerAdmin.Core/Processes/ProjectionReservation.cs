namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// The non-draining reservation launches and file projections share (B0). A launch holds it shared from before
/// it generates config until its session is registered; a projection holds it exclusively, which waits for the
/// in-flight shared holders and makes new shared acquires wait (never reject) until it is released. Exclusive
/// acquires are serialized among themselves. Unlike the maintenance gate it drains nothing: a queued launch just
/// runs a little later.
/// </summary>
public sealed class ProjectionReservation
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _exclusiveTurn = new(1, 1);
    private int _sharedCount;
    private bool _exclusive;
    private TaskCompletionSource? _drained;
    private TaskCompletionSource _opened = Completed();

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

    /// <summary>Shared holders right now; for diagnostics and tests.</summary>
    public int SharedCount
    {
        get
        {
            lock (_sync)
            {
                return _sharedCount;
            }
        }
    }

    /// <summary>Waits while an exclusive holder is active or pending, then holds shared.</summary>
    public async Task<IDisposable> AcquireSharedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task opened;
            lock (_sync)
            {
                if (!_exclusive)
                {
                    _sharedCount++;
                    return new SharedLease(this);
                }

                opened = _opened.Task;
            }

            await opened.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Blocks new shared holders, waits for the in-flight ones, then holds exclusively.</summary>
    public async Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTurn.WaitAsync(cancellationToken);
        try
        {
            Task drained;
            lock (_sync)
            {
                _exclusive = true;
                _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

            await drained.WaitAsync(cancellationToken);
            return new ExclusiveLease(this);
        }
        catch
        {
            ReleaseExclusiveCore();
            throw;
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

    private void ReleaseExclusiveCore()
    {
        TaskCompletionSource opened;
        lock (_sync)
        {
            _exclusive = false;
            _drained = null;
            opened = _opened;
            _opened = Completed();
        }

        opened.TrySetResult();
        _exclusiveTurn.Release();
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class SharedLease(ProjectionReservation owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseShared();
            }
        }
    }

    private sealed class ExclusiveLease(ProjectionReservation owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseExclusiveCore();
            }
        }
    }
}
