using System.Globalization;
using System.Security.Claims;

namespace ArkAscendedServerAdmin.Auth;

/// <summary>Claim types issued by the login flow.</summary>
public static class ArkClaimTypes
{
    /// <summary>Hash of the password the session was created with (see <see cref="PasswordHash"/>).</summary>
    public const string PasswordHash = "ark:pwh";

    /// <summary>Unix seconds at which the authenticating cookie expires; kept in sync by the cookie handler.</summary>
    public const string ExpiresAt = "ark:exp";

    /// <summary>The only account name there is.</summary>
    public const string OwnerName = "owner";
}

/// <summary>
/// The one rule every cookie request, every circuit revalidation, and every command facade applies: the
/// principal must be authenticated, its password-hash claim must match the live password, and its cookie
/// must not have expired.
/// </summary>
public static class PasswordClaimValidator
{
    public static bool IsValid(ClaimsPrincipal? principal, string? currentPasswordHash, DateTimeOffset now)
    {
        if (principal?.Identity is not { IsAuthenticated: true } || string.IsNullOrEmpty(currentPasswordHash))
        {
            return false;
        }

        if (!PasswordHash.Matches(principal.FindFirst(ArkClaimTypes.PasswordHash)?.Value, currentPasswordHash))
        {
            return false;
        }

        var expiresAt = principal.FindFirst(ArkClaimTypes.ExpiresAt)?.Value;
        if (expiresAt is null)
        {
            return false;
        }

        return long.TryParse(expiresAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            && DateTimeOffset.FromUnixTimeSeconds(unixSeconds) > now;
    }

    public static string EncodeExpiry(DateTimeOffset expiresAt) =>
        expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
