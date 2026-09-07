using ArkAscendedServerAdmin.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// Scoped <see cref="IAuthorizationGuard"/>: reads the caller's current <see cref="AuthenticationState"/>
/// (circuit or static render) and applies the password-hash + expiry rule against the live password.
/// </summary>
public sealed class AuthorizationGuard(
    AuthenticationStateProvider authenticationStateProvider,
    PasswordSource passwordSource,
    TimeProvider timeProvider) : IAuthorizationGuard
{
    public async ValueTask EnsureAuthorizedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        if (!PasswordClaimValidator.IsValid(state.User, passwordSource.CurrentHash, timeProvider.GetUtcNow()))
        {
            throw new NotAuthorizedException();
        }
    }
}
