namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// Failed-login accounting keyed by client address: <see cref="MaxFailures"/> failures inside
/// <see cref="Window"/> lock the address out for <see cref="LockoutDuration"/>. The fixed per-failure delay
/// is applied by the login service, not here. Thread-safe; a singleton.
/// </summary>
public sealed class LoginThrottle(TimeProvider timeProvider)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 5;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Returns the lockout end when the address is currently locked out, otherwise null.</summary>
    public DateTimeOffset? GetLockoutEnd(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            Prune(now);
            return _entries.TryGetValue(clientKey, out var entry) && entry.LockedUntil > now ? entry.LockedUntil : null;
        }
    }

    /// <summary>Records a failure; returns the lockout end if this failure triggered (or extended) a lockout.</summary>
    public DateTimeOffset? RecordFailure(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            Prune(now);
            if (!_entries.TryGetValue(clientKey, out var entry))
            {
                entry = new Entry();
                _entries[clientKey] = entry;
            }

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
    }

    public void RecordSuccess(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);

        lock (_lock)
        {
            _entries.Remove(clientKey);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var stale = _entries
            .Where(kv => kv.Value.LockedUntil is null || kv.Value.LockedUntil <= now)
            .Where(kv => kv.Value.Failures.TrueForAll(t => t <= now - Window))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in stale)
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public DateTimeOffset? LockedUntil { get; set; }
    }
}
