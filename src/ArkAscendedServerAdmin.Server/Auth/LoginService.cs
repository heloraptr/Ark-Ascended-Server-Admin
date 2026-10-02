using System.Security.Claims;
using ArkAscendedServerAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// Cookie sign-in for the single password (plan step 8): a fixed 1 s delay on every failed attempt and a
/// 5-failure / 5-minute lockout per client address on top of it. Attempts still being verified count toward
/// the lockout, and the password check itself waits for one of the throttle's few app-wide verify slots.
/// </summary>
public sealed class LoginService(
    IHttpContextAccessor httpContextAccessor,
    PasswordSource passwordSource,
    LoginThrottle throttle,
    TimeProvider timeProvider,
    ILogger<LoginService> logger) : ILoginService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan _failureDelay = TimeSpan.FromSeconds(1);

    /// <summary>Replaces <see cref="PasswordSource.Verify"/> in tests, which need to count and hold the password checks.</summary>
    private readonly Func<string, string?>? _verifyOverride;

    internal LoginService(
        IHttpContextAccessor httpContextAccessor,
        PasswordSource passwordSource,
        LoginThrottle throttle,
        TimeProvider timeProvider,
        ILogger<LoginService> logger,
        Func<string, string?> verify)
        : this(httpContextAccessor, passwordSource, throttle, timeProvider, logger) => _verifyOverride = verify;

    public async Task<LoginResult> LoginAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Login requires an active HTTP request (static server rendering).");
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // The login form passes no token, so the request's own abort signal is linked in here.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, httpContext.RequestAborted);
        var ct = linked.Token;

        if (!passwordSource.IsConfigured)
        {
            logger.LogError(
                "Login refused from {Client}: no usable password is configured (ArkAdmin:PasswordHash or ArkAdmin:Password; state {State}).",
                clientKey,
                passwordSource.Kind);
            return LoginResult.NotConfigured;
        }

        // Every exit that does not call Fail or Succeed abandons the reservation without recording anything.
        using var attempt = throttle.TryBeginAttempt(clientKey, out var lockedUntil);
        if (attempt is null)
        {
            logger.LogWarning("Login refused from {Client}: locked out until {Until:u}.", clientKey, lockedUntil);
            return LoginResult.LockedOut(lockedUntil);
        }

        string? credential;
        using (var permit = await throttle.TryEnterVerifyAsync(ct))
        {
            if (permit is null)
            {
                logger.LogWarning("Login refused from {Client}: too many logins are being verified.", clientKey);
                return LoginResult.Busy;
            }

            // The hash cannot be interrupted, so once it starts it runs to the end on its permit and its
            // answer is recorded even if the client has gone: a disconnect must not buy an uncounted guess.
            // The claim is issued from the snapshot Verify matched, never from a later read of CurrentHash.
            var verify = _verifyOverride ?? passwordSource.Verify;
            credential = await Task.Run(() => verify(password), CancellationToken.None);
        }

        if (credential is null)
        {
            var lockout = attempt.Fail();
            if (lockout is null)
            {
                logger.LogWarning("Failed login from {Client}.", clientKey);
            }
            else
            {
                logger.LogWarning("Failed login from {Client}; locked out until {Until:u}.", clientKey, lockout);
            }

            ct.ThrowIfCancellationRequested();
            await Task.Delay(_failureDelay, timeProvider, ct);
            return lockout is { } until ? LoginResult.LockedOut(until) : LoginResult.InvalidPassword;
        }

        attempt.Succeed();
        ct.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + SessionLifetime;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, ArkClaimTypes.OwnerName),
                new Claim(ArkClaimTypes.PasswordHash, credential),
                new Claim(ArkClaimTypes.ExpiresAt, PasswordClaimValidator.EncodeExpiry(expiresAt)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, ExpiresUtc = expiresAt });

        logger.LogInformation("Login succeeded from {Client}.", clientKey);
        return LoginResult.Success;
    }
}
