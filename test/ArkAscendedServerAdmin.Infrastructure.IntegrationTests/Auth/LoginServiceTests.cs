using System.Net;
using System.Security.Claims;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Server;
using ArkAscendedServerAdmin.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Auth;

/// <summary>
/// The login service under parallel requests (finding 6): the per-address limit holds while attempts are
/// still being verified, at most two password checks run at once, and a disconnect never buys an uncounted
/// guess. The password check is a held fake, so every step waits on an explicit signal.
/// </summary>
public class LoginServiceTests
{
    private const string RightPassword = "right";
    private const string WrongPassword = "wrong";
    private const string Address = "10.0.0.1";
    private static readonly DateTimeOffset _start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A guard against a hang, not a timing assumption: every wait below completes on a signal.</summary>
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwentyParallelWrongPasswordsFromOneAddress_VerifyAtMostMaxFailuresTimes()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new Harness();

        var logins = Enumerable.Range(0, 20).Select(_ => Task.Run(() => h.Service(Address).LoginAsync(WrongPassword, ct), ct)).ToList();

        // Five reserve a place: two verify (held) and three wait for a slot. The other fifteen are refused.
        await WhenCompletedAsync(logins, 20 - LoginThrottle.MaxFailures, ct);
        await h.Verifier.Calls.WhenAttempt(LoginThrottle.MaxConcurrentVerifies).WaitAsync(_wait, ct);
        foreach (var refused in logins.Where(login => login.IsCompleted))
        {
            Assert.Equal(LoginOutcome.LockedOut, (await refused).Outcome);
        }

        Assert.Equal(LoginThrottle.MaxFailures, h.Throttle.InFlightCount(Address));

        h.Verifier.ReleaseAll();
        var results = await Task.WhenAll(logins).WaitAsync(_wait, ct);

        Assert.Equal(LoginThrottle.MaxFailures, h.Verifier.Calls.Count);
        Assert.Equal(LoginThrottle.MaxFailures - 1, results.Count(r => r.Outcome == LoginOutcome.InvalidPassword));
        Assert.Equal(20 - LoginThrottle.MaxFailures + 1, results.Count(r => r.Outcome == LoginOutcome.LockedOut));
        Assert.NotNull(h.Throttle.GetLockoutEnd(Address));
        Assert.Equal(0, h.Throttle.InFlightCount(Address));
        Assert.Equal(0, h.Throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task ParallelLoginsFromManyAddresses_VerifyTwoAtATime_AndAFullGateAnswersBusy()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new Harness();
        const int overflow = 2;
        var capacity = LoginThrottle.MaxConcurrentVerifies + LoginThrottle.MaxQueuedVerifies;
        var addresses = Enumerable.Range(1, capacity + overflow).Select(i => $"10.0.1.{i}").ToList();

        var logins = addresses.Select(address => Task.Run(() => h.Service(address).LoginAsync(WrongPassword, ct), ct)).ToList();

        // Two verify (held) and eight wait; only the overflow can finish now, and it finishes as Busy.
        await WhenCompletedAsync(logins, overflow, ct);
        await h.Verifier.Calls.WhenAttempt(LoginThrottle.MaxConcurrentVerifies).WaitAsync(_wait, ct);
        Assert.Equal(capacity, h.Throttle.VerifyAdmissions);
        var busy = new List<string>();
        for (var i = 0; i < logins.Count; i++)
        {
            if (logins[i].IsCompleted)
            {
                Assert.Equal(LoginOutcome.Busy, (await logins[i]).Outcome);
                busy.Add(addresses[i]);
            }
        }

        Assert.Equal(overflow, busy.Count);
        Assert.All(busy, address =>
        {
            Assert.Equal(0, h.Throttle.InFlightCount(address));
            Assert.Equal(0, h.Throttle.FailureCount(address));
        });

        h.Verifier.ReleaseAll();
        var results = await Task.WhenAll(logins).WaitAsync(_wait, ct);

        Assert.Equal(capacity, results.Count(r => r.Outcome == LoginOutcome.InvalidPassword));
        Assert.Equal(capacity, h.Verifier.Calls.Count);
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies, h.Verifier.MaxRunning);
        Assert.Equal(0, h.Throttle.VerifyAdmissions);
        Assert.All(addresses, address => Assert.Equal(0, h.Throttle.InFlightCount(address)));
    }

    [Fact]
    public async Task RequestAbortedWhileWaitingForASlot_EndsTheRequestWithoutVerifying()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new Harness();
        var holders = new[] { "10.0.2.1", "10.0.2.2" }.Select(address => Task.Run(() => h.Service(address).LoginAsync(WrongPassword, ct), ct)).ToList();
        await h.Verifier.Calls.WhenAttempt(LoginThrottle.MaxConcurrentVerifies).WaitAsync(_wait, ct);

        // Like the login form, the caller passes no token: only the request's abort signal can end it.
        using var abort = new CancellationTokenSource();
        var queued = Task.Run(() => h.Service(Address, abort.Token).LoginAsync(WrongPassword), ct);

        // The waiter's queue timeout is the only timer on the throttle's clock.
        await h.ThrottleClock.WaitForPendingTimerAsync().WaitAsync(_wait, ct);
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies + 1, h.Throttle.VerifyAdmissions);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(_wait, ct));
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies, h.Verifier.Calls.Count);
        Assert.Equal(0, h.Throttle.InFlightCount(Address));
        Assert.Equal(0, h.Throttle.FailureCount(Address));
        Assert.Equal(LoginThrottle.MaxConcurrentVerifies, h.Throttle.VerifyAdmissions);

        h.Verifier.ReleaseAll();
        await Task.WhenAll(holders).WaitAsync(_wait, ct);
        Assert.Equal(0, h.Throttle.VerifyAdmissions);
    }

    [Fact]
    public async Task RequestAbortedMidVerify_WrongPassword_StillRecordsTheFailureAndHoldsThePermit()
    {
        var ct = TestContext.Current.CancellationToken;
        var serviceClock = new ManualTimeProvider(_start);
        var h = new Harness(serviceClock);
        using var abort = new CancellationTokenSource();

        var login = Task.Run(() => h.Service(Address, abort.Token).LoginAsync(WrongPassword), ct);
        await h.Verifier.Calls.WhenAttempt(1).WaitAsync(_wait, ct);
        await abort.CancelAsync();

        // The verify is held, so the request cannot have moved on: its slot and reservation are still taken.
        Assert.False(login.IsCompleted);
        Assert.Equal(1, h.Throttle.VerifyAdmissions);
        Assert.Equal(1, h.Throttle.InFlightCount(Address));

        h.Verifier.ReleaseAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(_wait, ct));

        Assert.Equal(1, h.Throttle.FailureCount(Address));
        Assert.Equal(0, h.Throttle.InFlightCount(Address));
        Assert.Equal(0, h.Throttle.VerifyAdmissions);
        Assert.Equal(0, serviceClock.PendingTimers);
        await h.Authentication.DidNotReceiveWithAnyArgs().SignInAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task RequestAbortedMidVerify_RightPassword_ClearsFailuresButDoesNotSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new Harness();
        h.Throttle.RecordFailure(Address);
        h.Throttle.RecordFailure(Address);
        using var abort = new CancellationTokenSource();

        var login = Task.Run(() => h.Service(Address, abort.Token).LoginAsync(RightPassword), ct);
        await h.Verifier.Calls.WhenAttempt(1).WaitAsync(_wait, ct);
        await abort.CancelAsync();
        h.Verifier.ReleaseAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(_wait, ct));
        Assert.Equal(0, h.Throttle.FailureCount(Address));
        Assert.Equal(0, h.Throttle.TrackedClientCount);
        await h.Authentication.DidNotReceiveWithAnyArgs().SignInAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task RightPassword_SignsIn_AndClearsTheAddressFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new Harness();
        for (var i = 0; i < LoginThrottle.MaxFailures - 2; i++)
        {
            h.Throttle.RecordFailure(Address);
        }

        h.Verifier.ReleaseAll();
        var result = await h.Service(Address, ct).LoginAsync(RightPassword, ct).WaitAsync(_wait, ct);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal(0, h.Throttle.FailureCount(Address));
        Assert.Equal(0, h.Throttle.TrackedClientCount);
        Assert.Equal(0, h.Throttle.VerifyAdmissions);
        await h.Authentication.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string?>(),
            Arg.Is<ClaimsPrincipal>(p => p.FindFirstValue(ArkClaimTypes.PasswordHash) == HeldVerifier.Credential),
            Arg.Any<AuthenticationProperties?>());
    }

    /// <summary>Waits until at least <paramref name="count"/> of <paramref name="tasks"/> have completed.</summary>
    private static async Task WhenCompletedAsync(IReadOnlyList<Task> tasks, int count, CancellationToken ct)
    {
        while (true)
        {
            // Count from the same snapshot that is awaited, so a task finishing in between is never missed.
            var pending = tasks.Where(task => !task.IsCompleted).ToList();
            if (tasks.Count - pending.Count >= count)
            {
                return;
            }

            await Task.WhenAny(pending).WaitAsync(_wait, ct);
        }
    }

    /// <summary>
    /// One throttle (on a clock that never moves, so no queue timeout fires by itself), one held password
    /// check, and a fresh scoped service per request. The failure delay runs on <c>serviceClock</c>, which
    /// completes it at once unless a test passes its own.
    /// </summary>
    private sealed class Harness
    {
        private readonly PasswordSource _passwords;
        private readonly TimeProvider _serviceClock;
        private readonly ServiceProvider _services;

        public Harness(TimeProvider? serviceClock = null)
        {
            var options = Substitute.For<IOptionsMonitor<ArkAdminOptions>>();
            options.CurrentValue.Returns(new ArkAdminOptions { Password = RightPassword });
            _passwords = new PasswordSource(options, NullLogger<PasswordSource>.Instance);
            _serviceClock = serviceClock ?? new FastTimeProvider(_start);
            Authentication = Substitute.For<IAuthenticationService>();
            _services = new ServiceCollection().AddSingleton(Authentication).BuildServiceProvider();
            Throttle = new LoginThrottle(ThrottleClock);
        }

        public ManualTimeProvider ThrottleClock { get; } = new(_start);

        public LoginThrottle Throttle { get; }

        public HeldVerifier Verifier { get; } = new();

        public IAuthenticationService Authentication { get; }

        public LoginService Service(string address, CancellationToken requestAborted = default)
        {
            var context = new DefaultHttpContext { RequestServices = _services, RequestAborted = requestAborted };
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            return new LoginService(new FixedHttpContextAccessor(context), _passwords, Throttle, _serviceClock, NullLogger<LoginService>.Instance, Verifier.Verify);
        }
    }

    /// <summary>
    /// A password check the test holds open: it counts calls (signaling each), tracks how many run at once,
    /// and blocks every call until <see cref="ReleaseAll"/>.
    /// </summary>
    private sealed class HeldVerifier
    {
        public const string Credential = "matched-credential";

        private readonly ManualResetEventSlim _released = new();
        private readonly Lock _sync = new();
        private int _running;
        private int _maxRunning;

        public AttemptLog Calls { get; } = new();

        public int MaxRunning
        {
            get
            {
                lock (_sync)
                {
                    return _maxRunning;
                }
            }
        }

        public void ReleaseAll() => _released.Set();

        public string? Verify(string password)
        {
            lock (_sync)
            {
                _running++;
                _maxRunning = Math.Max(_maxRunning, _running);
            }

            Calls.Record();
            if (!_released.Wait(_wait))
            {
                throw new TimeoutException("The test never released the password check.");
            }

            lock (_sync)
            {
                _running--;
            }

            return password == RightPassword ? Credential : null;
        }
    }

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => context;
            set => throw new NotSupportedException();
        }
    }
}
