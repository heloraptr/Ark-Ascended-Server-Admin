using System.Security.Claims;
using ArkAscendedServerAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// Cookie sign-in for the single password (plan step 8): a fixed 1 s delay on every failed attempt and a
/// 5-failure / 5-minute lockout per client address on top of it.
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

    public async Task<LoginResult> LoginAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Login requires an active HTTP request (static server rendering).");
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!passwordSource.IsConfigured)
        {
            logger.LogError("Login refused from {Client}: no password is configured (ArkAdmin:Password).", clientKey);
            return LoginResult.NotConfigured;
        }

        if (throttle.GetLockoutEnd(clientKey) is { } lockedUntil)
        {
            logger.LogWarning("Login refused from {Client}: locked out until {Until:u}.", clientKey, lockedUntil);
            return LoginResult.LockedOut(lockedUntil);
        }

        if (!passwordSource.Verify(password))
        {
            var lockout = throttle.RecordFailure(clientKey);
            if (lockout is null)
            {
                logger.LogWarning("Failed login from {Client}.", clientKey);
            }
            else
            {
                logger.LogWarning("Failed login from {Client}; locked out until {Until:u}.", clientKey, lockout);
            }

            await Task.Delay(_failureDelay, timeProvider, cancellationToken);
            return lockout is { } until ? LoginResult.LockedOut(until) : LoginResult.InvalidPassword;
        }

        throttle.RecordSuccess(clientKey);

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + SessionLifetime;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, ArkClaimTypes.OwnerName),
                new Claim(ArkClaimTypes.PasswordHash, passwordSource.CurrentHash!),
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
