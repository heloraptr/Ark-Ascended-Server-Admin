namespace ArkAscendedServerAdmin.UnitTests;

/// <summary>
/// A manually advanced clock for throttle, expiry, and scheduling tests. Timers created through it
/// (<c>Task.Delay(delay, provider)</c>, <c>WaitAsync(timeout, provider)</c>) fire only when
/// <see cref="Advance"/> moves the clock past their due time, in due order.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _sync = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;

    public DateTimeOffset Now
    {
        get
        {
            lock (_sync)
            {
                return _now;
            }
        }
    }

    /// <summary>Timers that are armed and waiting for the clock; lets a test wait until code under test has started its delay.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(timer => timer.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward, firing every timer due on the way (callbacks run on the calling thread).</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_sync)
        {
            target = _now + by;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_sync)
            {
                next = _timers.Where(timer => timer.DueAt is not null && timer.DueAt <= target).MinBy(timer => timer.DueAt);
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
                next.Rearm();
            }

            next.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private void Remove(FakeTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        /// <summary>Null while disarmed (infinite due time or disposed).</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        /// <summary>Called under the owner's lock just before firing: periodic timers move to their next tick, one-shots disarm.</summary>
        public void Rearm() => DueAt = _period > TimeSpan.Zero ? DueAt + _period : null;

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
