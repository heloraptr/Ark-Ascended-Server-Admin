using System.Security.Claims;
using ArkAscendedServerAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// Runs on every cookie-authenticated request: rejects the principal when its password-hash claim no
/// longer matches the live password, and keeps the expiry claim equal to the ticket's expiry so the
/// circuit revalidator (which only ever sees the principal) can enforce cookie expiry too.
/// </summary>
public sealed class ArkCookieEvents(PasswordSource passwordSource, TimeProvider timeProvider) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal;

        if (principal is not null && context.Properties.ExpiresUtc is { } expiresUtc)
        {
            var encoded = PasswordClaimValidator.EncodeExpiry(expiresUtc);
            if (principal.FindFirst(ArkClaimTypes.ExpiresAt)?.Value != encoded)
            {
                var identity = new ClaimsIdentity(
                    principal.Claims.Where(c => c.Type != ArkClaimTypes.ExpiresAt).Append(new Claim(ArkClaimTypes.ExpiresAt, encoded)),
                    principal.Identity?.AuthenticationType);
                principal = new ClaimsPrincipal(identity);
                context.ReplacePrincipal(principal);
            }
        }

        if (!PasswordClaimValidator.IsValid(principal, passwordSource.CurrentHash, timeProvider.GetUtcNow()))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
