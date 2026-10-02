namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// One login attempt reserved by <see cref="LoginThrottle.TryBeginAttempt"/>. It completes exactly once:
/// <see cref="Fail"/>, <see cref="Succeed"/> or, for an attempt that ended with neither answer,
/// <see cref="Dispose"/>; later calls do nothing. It acts on the throttle entry it reserved on, so a late
/// completion can never touch a newer entry created under the same address.
/// </summary>
public sealed class LoginAttempt : IDisposable
{
    private readonly LoginThrottle _throttle;
    private int _completed;

    internal LoginAttempt(LoginThrottle throttle, string clientKey, LoginThrottle.Entry entry)
    {
        _throttle = throttle;
        ClientKey = clientKey;
        Entry = entry;
    }

    public string ClientKey { get; }

    internal LoginThrottle.Entry Entry { get; }

    /// <summary>Records a wrong password; returns the lockout end if this failure triggered a lockout.</summary>
    public DateTimeOffset? Fail() => TryComplete() ? _throttle.CompleteFailure(this) : null;

    /// <summary>Records the right password: clears the address's failures and lockout, and releases only this reservation.</summary>
    public void Succeed()
    {
        if (TryComplete())
        {
            _throttle.CompleteSuccess(this);
        }
    }

    /// <summary>Abandons the attempt without recording anything (canceled before verifying, Busy, or an error).</summary>
    public void Dispose()
    {
        if (TryComplete())
        {
            _throttle.Abandon(this);
        }
    }

    private bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
}
