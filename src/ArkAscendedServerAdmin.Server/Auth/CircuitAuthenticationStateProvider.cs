using ArkAscendedServerAdmin.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// Re-checks a live circuit's principal every five minutes against the same rule the cookie handler
/// applies (password-hash claim + expiry). On failure the circuit's state becomes anonymous, the layout
/// forces navigation to <c>/login</c>, and the circuit is torn down.
/// </summary>
public sealed class CircuitAuthenticationStateProvider(
    PasswordSource passwordSource,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        return Task.FromResult(PasswordClaimValidator.IsValid(authenticationState.User, passwordSource.CurrentHash, timeProvider.GetUtcNow()));
    }
}
