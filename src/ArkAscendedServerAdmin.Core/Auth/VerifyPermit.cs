namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// A verify slot granted by <see cref="LoginThrottle.TryEnterVerifyAsync"/>. Hold it until the password check
/// has actually finished; disposing it frees the slot, and a second dispose does nothing.
/// </summary>
public sealed class VerifyPermit : IDisposable
{
    private readonly LoginThrottle _throttle;
    private int _released;

    internal VerifyPermit(LoginThrottle throttle) => _throttle = throttle;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _throttle.ReleaseVerify();
        }
    }
}
