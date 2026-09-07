using ArkAscendedServerAdmin.Auth;

namespace ArkAscendedServerAdmin.UnitTests.Auth;

public class LoginThrottleTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshClient_IsNotLockedOut()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        Assert.Null(throttle.GetLockoutEnd("10.0.0.1"));
    }

    [Fact]
    public void FourFailures_DoNotLockOut()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            Assert.Null(throttle.RecordFailure("10.0.0.1"));
        }

        Assert.Null(throttle.GetLockoutEnd("10.0.0.1"));
    }

    [Fact]
    public void FifthFailureWithinWindow_LocksOutForLockoutDuration()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);

        DateTimeOffset? lockout = null;
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            lockout = throttle.RecordFailure("10.0.0.1");
        }

        Assert.Equal(_start + LoginThrottle.LockoutDuration, lockout);
        Assert.Equal(lockout, throttle.GetLockoutEnd("10.0.0.1"));

        clock.Advance(LoginThrottle.LockoutDuration - TimeSpan.FromSeconds(1));
        Assert.NotNull(throttle.GetLockoutEnd("10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(throttle.GetLockoutEnd("10.0.0.1"));
    }

    [Fact]
    public void FailuresOutsideWindow_DoNotCount()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("10.0.0.1");
        }

        clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));

        Assert.Null(throttle.RecordFailure("10.0.0.1"));
    }

    [Fact]
    public void Lockout_IsPerClient()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("10.0.0.1");
        }

        Assert.NotNull(throttle.GetLockoutEnd("10.0.0.1"));
        Assert.Null(throttle.GetLockoutEnd("10.0.0.2"));
    }

    [Fact]
    public void Success_ClearsFailureHistory()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("10.0.0.1");
        }

        throttle.RecordSuccess("10.0.0.1");

        Assert.Null(throttle.RecordFailure("10.0.0.1"));
    }
}
