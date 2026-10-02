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

    private const string Client = "10.0.0.1";

    [Fact]
    public void BeginAttempt_AdmitsUpToMaxFailuresInFlight_ThenRefusesWithoutStoringALockout()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        BeginMany(throttle, LoginThrottle.MaxFailures);

        Assert.Null(throttle.TryBeginAttempt(Client, out var lockedUntil));
        Assert.Equal(_start + LoginThrottle.LockoutDuration, lockedUntil);
        Assert.Null(throttle.GetLockoutEnd(Client));
        Assert.Equal(LoginThrottle.MaxFailures, throttle.InFlightCount(Client));
        Assert.Equal(0, throttle.FailureCount(Client));
    }

    [Fact]
    public void FailuresAndAttemptsInFlight_ShareTheLimit()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        throttle.RecordFailure(Client);
        throttle.RecordFailure(Client);

        var attempts = BeginMany(throttle, LoginThrottle.MaxFailures - 2);
        Assert.Null(throttle.TryBeginAttempt(Client, out _));

        // Abandoning frees its slot and records nothing: exactly one newcomer fits.
        attempts[0].Dispose();
        Assert.Equal(2, throttle.InFlightCount(Client));
        Assert.Equal(2, throttle.FailureCount(Client));
        Assert.NotNull(throttle.TryBeginAttempt(Client, out _));
        Assert.Null(throttle.TryBeginAttempt(Client, out _));

        // Failing moves its slot from in flight to the failure count, so nothing new fits.
        Assert.Null(attempts[1].Fail());
        Assert.Equal(2, throttle.InFlightCount(Client));
        Assert.Equal(3, throttle.FailureCount(Client));
        Assert.Null(throttle.TryBeginAttempt(Client, out _));
    }

    [Fact]
    public void AbandonedAttempt_RecordsNoFailure()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        throttle.TryBeginAttempt(Client, out _)!.Dispose();

        Assert.Equal(0, throttle.FailureCount(Client));
        Assert.Equal(0, throttle.InFlightCount(Client));
        Assert.Equal(0, throttle.TrackedClientCount);
    }

    [Fact]
    public void AttemptCompletedTwice_ChangesNothingTheSecondTime()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));

        var failed = throttle.TryBeginAttempt(Client, out _)!;
        failed.Fail();
        failed.Fail();
        failed.Succeed();
        failed.Dispose();
        Assert.Equal(1, throttle.FailureCount(Client));
        Assert.Equal(0, throttle.InFlightCount(Client));

        var abandoned = throttle.TryBeginAttempt(Client, out _)!;
        abandoned.Dispose();
        abandoned.Fail();
        abandoned.Succeed();
        Assert.Equal(1, throttle.FailureCount(Client));
        Assert.Equal(0, throttle.InFlightCount(Client));

        var succeeded = throttle.TryBeginAttempt(Client, out _)!;
        succeeded.Succeed();
        succeeded.Fail();
        succeeded.Dispose();
        Assert.Equal(0, throttle.FailureCount(Client));
        Assert.Equal(0, throttle.TrackedClientCount);
    }

    [Fact]
    public void OverlappingSuccess_ReleasesOnlyItsOwnReservation()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        var attempts = BeginMany(throttle, LoginThrottle.MaxFailures);

        attempts[0].Succeed();

        var newcomer = throttle.TryBeginAttempt(Client, out _);
        Assert.NotNull(newcomer);
        Assert.Null(throttle.TryBeginAttempt(Client, out _));

        foreach (var attempt in attempts.Skip(1))
        {
            Assert.Null(attempt.Fail());
        }

        Assert.Equal(LoginThrottle.MaxFailures - 1, throttle.FailureCount(Client));
        Assert.Null(throttle.GetLockoutEnd(Client));

        var lockout = newcomer.Fail();
        Assert.Equal(_start + LoginThrottle.LockoutDuration, lockout);
        Assert.Equal(lockout, throttle.GetLockoutEnd(Client));
    }

    [Fact]
    public void EntryWithAttemptsInFlight_IsNeverRemoved_AndOldHandlesCannotTouchItsReplacement()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);
        var attempts = BeginMany(throttle, 2);

        // Success, expiry of the addressed entry, and the sweep all leave it alone while attempts are out.
        throttle.RecordSuccess(Client);
        clock.Advance(LoginThrottle.Window + LoginThrottle.SweepInterval);
        Assert.Null(throttle.GetLockoutEnd(Client));
        Assert.Equal(2, throttle.SweepCount);
        Assert.Equal(1, throttle.TrackedClientCount);
        Assert.Equal(2, throttle.InFlightCount(Client));

        attempts[0].Succeed();
        attempts[1].Dispose();
        Assert.Equal(0, throttle.TrackedClientCount);

        Assert.NotNull(throttle.TryBeginAttempt(Client, out _));
        throttle.RecordFailure(Client);

        attempts[0].Fail();
        attempts[0].Dispose();
        attempts[1].Fail();
        attempts[1].Succeed();

        Assert.Equal(1, throttle.InFlightCount(Client));
        Assert.Equal(1, throttle.FailureCount(Client));
    }

    [Fact]
    public void Sweep_RemovesIdleEntries_AndKeepsEntriesWithAttemptsInFlight()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);
        throttle.RecordFailure("10.0.0.2");
        using var attempt = throttle.TryBeginAttempt(Client, out _);

        clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));

        // Addresses neither entry, so only the sweep can touch them.
        throttle.GetLockoutEnd("10.0.0.3");
        Assert.Equal(1, throttle.TrackedClientCount);
        Assert.Equal(1, throttle.InFlightCount(Client));
    }

    [Fact]
    public void Sweep_RunsAtMostOncePerSweepInterval()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);

        throttle.GetLockoutEnd(Client);
        Assert.Equal(1, throttle.SweepCount);

        clock.Advance(LoginThrottle.SweepInterval - TimeSpan.FromSeconds(1));
        throttle.GetLockoutEnd(Client);
        throttle.RecordFailure(Client);
        throttle.TryBeginAttempt("10.0.0.2", out _)!.Dispose();
        throttle.RecordSuccess(Client);
        Assert.Equal(1, throttle.SweepCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.GetLockoutEnd(Client);
        Assert.Equal(2, throttle.SweepCount);
    }

    [Fact]
    public void LockedOutAddress_ReportsItsRealLockoutEnd()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);

        DateTimeOffset? lockout = null;
        foreach (var attempt in BeginMany(throttle, LoginThrottle.MaxFailures))
        {
            lockout = attempt.Fail();
        }

        Assert.Equal(_start + LoginThrottle.LockoutDuration, lockout);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(throttle.TryBeginAttempt(Client, out var lockedUntil));
        Assert.Equal(lockout, lockedUntil);
    }

    [Fact]
    public async Task VerifyGate_AdmitsTwo_AndAThirdWaitsForARelease()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        var ct = TestContext.Current.CancellationToken;

        var first = await throttle.TryEnterVerifyAsync(ct);
        using var second = await throttle.TryEnterVerifyAsync(ct);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var third = throttle.TryEnterVerifyAsync(ct);
        Assert.False(third.IsCompleted);
        Assert.Equal(3, throttle.VerifyAdmissions);

        first.Dispose();
        using var admitted = await third;
        Assert.NotNull(admitted);
        Assert.Equal(2, throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task VerifyGate_WithTwoVerifyingAndEightWaiting_RefusesTheNextAtOnce()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        var ct = TestContext.Current.CancellationToken;
        await HoldAllSlotsAsync(throttle, ct);

        var waiters = Enumerable.Range(0, LoginThrottle.MaxQueuedVerifies).Select(_ => throttle.TryEnterVerifyAsync(ct)).ToList();
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        var refused = throttle.TryEnterVerifyAsync(ct);
        Assert.True(refused.IsCompletedSuccessfully);
        Assert.Null(await refused);
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies + LoginThrottle.MaxQueuedVerifies, throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task VerifyGate_WaiterGivesUpAfterTheQueueTimeout()
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);
        var ct = TestContext.Current.CancellationToken;
        await HoldAllSlotsAsync(throttle, ct);

        var waiter = throttle.TryEnterVerifyAsync(ct);
        Assert.Equal(3, throttle.VerifyAdmissions);

        clock.Advance(LoginThrottle.VerifyQueueTimeout - TimeSpan.FromTicks(1));
        Assert.False(waiter.IsCompleted);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await waiter);
        Assert.Equal(2, throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task VerifyGate_CanceledWaiter_LeavesTheCountUnchanged()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        var ct = TestContext.Current.CancellationToken;
        await HoldAllSlotsAsync(throttle, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var waiter = throttle.TryEnterVerifyAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(2, throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task VerifyGate_SimultaneousCallersAtTheBoundary_AdmitExactlyOne()
    {
        var throttle = new LoginThrottle(new TestTimeProvider(_start));
        var ct = TestContext.Current.CancellationToken;
        await HoldAllSlotsAsync(throttle, ct);
        for (var i = 0; i < LoginThrottle.MaxQueuedVerifies - 1; i++)
        {
            _ = throttle.TryEnterVerifyAsync(ct);
        }

        const int callers = 32;
        var calls = new Task<VerifyPermit?>[callers];
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
        {
            go.Wait(ct);
            calls[i] = throttle.TryEnterVerifyAsync(ct);
        })).ToList();
        threads.ForEach(thread => thread.Start());
        go.Set();
        threads.ForEach(thread => thread.Join());

        var refused = 0;
        foreach (var call in calls.Where(call => call.IsCompleted))
        {
            refused += await call is null ? 1 : 0;
        }

        Assert.Equal(callers - 1, refused);
        _ = Assert.Single(calls, call => !call.IsCompleted);
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies + LoginThrottle.MaxQueuedVerifies, throttle.VerifyAdmissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyGate_ReleaseRacingATimeoutOrCancellation_NeverLeaksASlot(bool cancel)
    {
        var clock = new TestTimeProvider(_start);
        var throttle = new LoginThrottle(clock);
        var ct = TestContext.Current.CancellationToken;

        for (var round = 0; round < 200; round++)
        {
            var first = (await throttle.TryEnterVerifyAsync(ct))!;
            var second = (await throttle.TryEnterVerifyAsync(ct))!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var waiter = throttle.TryEnterVerifyAsync(cts.Token);

            // The waiter is granted the released slot at the same moment it times out or is canceled.
            using var go = new ManualResetEventSlim();
            var release = Task.Run(() =>
            {
                go.Wait(ct);
                first.Dispose();
            }, ct);
            var giveUp = Task.Run(() =>
            {
                go.Wait(ct);
                if (cancel)
                {
                    cts.Cancel();
                }
                else
                {
                    clock.Advance(LoginThrottle.VerifyQueueTimeout);
                }
            }, ct);
            go.Set();
            await Task.WhenAll(release, giveUp);

            try
            {
                (await waiter)?.Dispose();
            }
            catch (OperationCanceledException) when (cancel)
            {
            }

            second.Dispose();
            Assert.Equal(0, throttle.VerifyAdmissions);

            var again = new[] { throttle.TryEnterVerifyAsync(ct), throttle.TryEnterVerifyAsync(ct) };
            Assert.All(again, entered => Assert.True(entered.IsCompletedSuccessfully));
            foreach (var entered in again)
            {
                var permit = await entered;
                Assert.NotNull(permit);
                permit.Dispose();
            }

            Assert.Equal(0, throttle.VerifyAdmissions);
        }
    }

    private static List<LoginAttempt> BeginMany(LoginThrottle throttle, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => throttle.TryBeginAttempt(Client, out var _) ?? throw new InvalidOperationException("Attempt refused."))];

    private static async Task HoldAllSlotsAsync(LoginThrottle throttle, CancellationToken ct)
    {
        for (var i = 0; i < LoginThrottle.MaxConcurrentVerifies; i++)
        {
            Assert.NotNull(await throttle.TryEnterVerifyAsync(ct));
        }
    }
}
