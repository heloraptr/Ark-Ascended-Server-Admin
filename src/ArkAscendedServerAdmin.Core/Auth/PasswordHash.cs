using System.Security.Cryptography;
using System.Text;

namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// The password-hash claim value for a plaintext <c>ArkAdmin:Password</c>: lower-case hex SHA-256 of the
/// configured password. It is not a storage hash (the password itself lives in <c>appsettings.json</c>);
/// it exists so a cookie or circuit issued under an old password can be recognized and refused after the
/// password changes. With <c>ArkAdmin:PasswordHash</c> the claim is the stored
/// <see cref="Pbkdf2PasswordHash"/> string instead; <see cref="PasswordCredential"/> picks.
/// </summary>
public static class PasswordHash
{
    public static string Compute(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    /// <summary>Constant-time comparison of two hash strings.</summary>
    public static bool Matches(string? candidate, string? expected)
    {
        if (candidate is null || expected is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
    }
}
