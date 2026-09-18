namespace ArkAscendedServerAdmin.Maintenance;

/// <summary>
/// The registry of jobs that outlive the request that started them (B0): delete today, restore with B2. Each
/// job registers before it starts and releases its registration when it ends. Once shutdown has begun, new
/// registrations are refused (the caller reports "the service is stopping") and <see cref="ShutdownAsync"/>
/// waits up to <see cref="ShutdownWait"/> for the active ones; the host's shutdown timeout is raised past that
/// so the wait fits inside it.
/// </summary>
public sealed class DetachedJobs(TimeProvider timeProvider)
{
    public static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(60);

    private readonly Lock _sync = new();
    private readonly Dictionary<long, Registration> _active = [];
    private long _nextId;
    private bool _stopping;

    public bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _stopping;
            }
        }
    }

    /// <summary>Names of the jobs registered right now; for diagnostics and tests.</summary>
    public IReadOnlyList<string> ActiveNames
    {
        get
        {
            lock (_sync)
            {
                return _active.Values.Select(r => r.Name).ToList();
            }
        }
    }

    /// <summary>Registers a job by name; disposing the result marks it finished. Null once shutdown has begun.</summary>
    public IDisposable? TryBegin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (_stopping)
            {
                return null;
            }

            var registration = new Registration(this, ++_nextId, name);
            _active[registration.Id] = registration;
            return registration;
        }
    }

    /// <summary>
    /// Refuses new registrations from now on and waits up to <see cref="ShutdownWait"/> for the active jobs. Returns
    /// the names of the jobs still running when the wait ended (empty when every job finished in time).
    /// </summary>
    public async Task<IReadOnlyList<string>> ShutdownAsync(CancellationToken cancellationToken)
    {
        List<Registration> pending;
        lock (_sync)
        {
            _stopping = true;
            pending = [.. _active.Values];
        }

        if (pending.Count == 0)
        {
            return [];
        }

        try
        {
            await Task.WhenAll(pending.Select(r => r.Done.Task)).WaitAsync(ShutdownWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        lock (_sync)
        {
            return _active.Values.Select(r => r.Name).ToList();
        }
    }

    private void End(Registration registration)
    {
        lock (_sync)
        {
            _active.Remove(registration.Id);
        }

        registration.Done.TrySetResult();
    }

    private sealed class Registration(DetachedJobs owner, long id, string name) : IDisposable
    {
        private int _ended;

        public long Id { get; } = id;

        public string Name { get; } = name;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.End(this);
            }
        }
    }
}
