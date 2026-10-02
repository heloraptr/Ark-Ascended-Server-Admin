namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// Failed-login accounting keyed by client address: <see cref="MaxFailures"/> failures inside
/// <see cref="Window"/> lock the address out for <see cref="LockoutDuration"/>. The fixed per-failure delay
/// is applied by the login service, not here. Thread-safe; a singleton.
/// </summary>
/// <remarks>
/// A login reserves its place with <see cref="TryBeginAttempt"/> before the password is checked, so attempts
/// still being verified count against the limit alongside recorded failures: parallel requests from one
/// address cannot get past it. Separately, <see cref="TryEnterVerifyAsync"/> caps password verification for
/// the whole app at <see cref="MaxConcurrentVerifies"/> running plus <see cref="MaxQueuedVerifies"/> waiting,
/// because every login post costs a PBKDF2 run on the machine that hosts the game servers.
/// </remarks>
public sealed class LoginThrottle(TimeProvider timeProvider)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 5;

    /// <summary>How often the whole table is scanned for idle entries; every call still expires the entry it addresses.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public const int MaxConcurrentVerifies = 2;
    public const int MaxQueuedVerifies = 8;

    /// <summary>How long a login waits for a verify slot before it is answered with Busy.</summary>
    public static readonly TimeSpan VerifyQueueTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _verifySlots = new(MaxConcurrentVerifies, MaxConcurrentVerifies);
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private int _sweepCount;

    /// <summary>Requests that are waiting for a verify slot or holding one; never above the cap.</summary>
    private int _verifyAdmissions;

    /// <summary>Returns the lockout end when the address is currently locked out, otherwise null.</summary>
    public DateTimeOffset? GetLockoutEnd(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            var entry = Lookup(clientKey, now);
            return entry?.LockedUntil;
        }
    }

    /// <summary>
    /// Reserves one attempt for the address, or returns null when it may not try now. A refusal sets
    /// <paramref name="lockedUntil"/> to the stored lockout end, or, when the address is only full of
    /// unresolved attempts, to a display value one lockout from now: nothing is stored then, because those
    /// attempts may still turn out to be the right password.
    /// </summary>
    public LoginAttempt? TryBeginAttempt(string clientKey, out DateTimeOffset lockedUntil)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            var entry = Lookup(clientKey, now);
            if (entry?.LockedUntil is { } until)
            {
                lockedUntil = until;
                return null;
            }

            if (entry is not null && entry.Failures.Count + entry.InFlight >= MaxFailures)
            {
                lockedUntil = now + LockoutDuration;
                return null;
            }

            if (entry is null)
            {
                entry = new Entry();
                _entries[clientKey] = entry;
            }

            entry.InFlight++;
            lockedUntil = default;
            return new LoginAttempt(this, clientKey, entry);
        }
    }

    /// <summary>Records a failure; returns the lockout end if this failure triggered (or extended) a lockout.</summary>
    public DateTimeOffset? RecordFailure(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            var entry = Lookup(clientKey, now);
            if (entry is null)
            {
                entry = new Entry();
                _entries[clientKey] = entry;
            }

            return AddFailure(entry, now);
        }
    }

    /// <summary>Clears the address's failures and lockout. Attempts still in flight keep their reservations.</summary>
    public void RecordSuccess(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);

        lock (_lock)
        {
            if (_entries.TryGetValue(clientKey, out var entry))
            {
                ClearHistory(entry);
                RemoveIfIdle(clientKey, entry);
            }
        }
    }

    /// <summary>
    /// Waits for one of the <see cref="MaxConcurrentVerifies"/> verify slots. Returns null at once when
    /// <see cref="MaxQueuedVerifies"/> requests are already waiting, or after <see cref="VerifyQueueTimeout"/>
    /// without a slot; throws when <paramref name="cancellationToken"/> fires first. Dispose the permit when
    /// the verify has finished.
    /// </summary>
    public async Task<VerifyPermit?> TryEnterVerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check and increment in one atomic step, so callers racing at the boundary cannot overshoot the cap.
        var seen = Volatile.Read(ref _verifyAdmissions);
        while (true)
        {
            if (seen >= MaxConcurrentVerifies + MaxQueuedVerifies)
            {
                return null;
            }

            var previous = Interlocked.CompareExchange(ref _verifyAdmissions, seen + 1, seen);
            if (previous == seen)
            {
                break;
            }

            seen = previous;
        }

        var acquired = false;
        try
        {
            if (_verifySlots.Wait(0))
            {
                acquired = true;
            }
            else
            {
                using var timeout = new CancellationTokenSource(VerifyQueueTimeout, timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    // SemaphoreSlim either grants the slot or cancels the wait, never both, so a release that
                    // races the timeout leaves the slot with exactly one owner.
                    await _verifySlots.WaitAsync(linked.Token);
                    acquired = true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Granted at the same moment the caller gave up: hand the slot straight back.
                _verifySlots.Release();
                acquired = false;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new VerifyPermit(this);
        }
        finally
        {
            if (!acquired)
            {
                Interlocked.Decrement(ref _verifyAdmissions);
            }
        }
    }

    /// <summary>Failures recorded for the address and not yet expired or cleared, for tests.</summary>
    internal int FailureCount(string clientKey)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(clientKey, out var entry) ? entry.Failures.Count : 0;
        }
    }

    /// <summary>Attempts reserved for the address and not yet completed, for tests.</summary>
    internal int InFlightCount(string clientKey)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(clientKey, out var entry) ? entry.InFlight : 0;
        }
    }

    /// <summary>Addresses that currently have an entry, for tests.</summary>
    internal int TrackedClientCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How many full-table sweeps have run, for tests.</summary>
    internal int SweepCount
    {
        get
        {
            lock (_lock)
            {
                return _sweepCount;
            }
        }
    }

    /// <summary>Requests waiting for or holding a verify slot, for tests.</summary>
    internal int VerifyAdmissions => Volatile.Read(ref _verifyAdmissions);

    internal DateTimeOffset? CompleteFailure(LoginAttempt attempt)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            var entry = attempt.Entry;
            entry.InFlight--;
            Expire(entry, now);
            return AddFailure(entry, now);
        }
    }

    internal void CompleteSuccess(LoginAttempt attempt)
    {
        lock (_lock)
        {
            var entry = attempt.Entry;
            entry.InFlight--;
            ClearHistory(entry);
            RemoveIfIdle(attempt.ClientKey, entry);
        }
    }

    internal void Abandon(LoginAttempt attempt)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            var entry = attempt.Entry;
            entry.InFlight--;
            Expire(entry, now);
            RemoveIfIdle(attempt.ClientKey, entry);
        }
    }

    internal void ReleaseVerify()
    {
        _verifySlots.Release();
        Interlocked.Decrement(ref _verifyAdmissions);
    }

    /// <summary>Runs the periodic sweep if due, then expires and returns the addressed entry (null when it has none).</summary>
    private Entry? Lookup(string clientKey, DateTimeOffset now)
    {
        SweepIfDue(now);
        if (!_entries.TryGetValue(clientKey, out var entry))
        {
            return null;
        }

        Expire(entry, now);
        return RemoveIfIdle(clientKey, entry) ? null : entry;
    }

    private static DateTimeOffset? AddFailure(Entry entry, DateTimeOffset now)
    {
        entry.Failures.Add(now);
        entry.Failures.RemoveAll(t => t <= now - Window);

        if (entry.Failures.Count >= MaxFailures)
        {
            entry.LockedUntil = now + LockoutDuration;
            entry.Failures.Clear();
            return entry.LockedUntil;
        }

        return null;
    }

    private static void ClearHistory(Entry entry)
    {
        entry.Failures.Clear();
        entry.LockedUntil = null;
    }

    /// <summary>Drops failures older than the window and a lockout that has ended.</summary>
    private static void Expire(Entry entry, DateTimeOffset now)
    {
        entry.Failures.RemoveAll(t => t <= now - Window);
        if (entry.LockedUntil <= now)
        {
            entry.LockedUntil = null;
        }
    }

    /// <summary>
    /// Removes the entry when nothing about it is left to remember. An entry with attempts in flight always
    /// stays: their handles act on this object, and a replacement under the same key would not see them.
    /// </summary>
    private bool RemoveIfIdle(string clientKey, Entry entry)
    {
        if (entry.InFlight > 0 || entry.Failures.Count > 0 || entry.LockedUntil is not null)
        {
            return false;
        }

        if (_entries.TryGetValue(clientKey, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(clientKey);
        }

        return true;
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now + SweepInterval;
        _sweepCount++;
        foreach (var (key, entry) in _entries.ToList())
        {
            Expire(entry, now);
            RemoveIfIdle(key, entry);
        }
    }

    internal sealed class Entry
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public DateTimeOffset? LockedUntil { get; set; }

        /// <summary>Attempts reserved and not yet completed; while above zero the entry is never removed.</summary>
        public int InFlight { get; set; }
    }
}
