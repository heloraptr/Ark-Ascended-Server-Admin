using System.Globalization;
using System.Security.Cryptography;

namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// The storage hash for <c>ArkAdmin:PasswordHash</c> (release plan step A1):
/// <c>pbkdf2$&lt;iterations&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c>, PBKDF2-SHA256 with 600 000
/// iterations, a 16-byte salt, and a 32-byte hash. Parsing is strict: a value that is not exactly this
/// shape is refused rather than repaired, because a malformed hash must refuse every login.
/// </summary>
public static class Pbkdf2PasswordHash
{
    public const string Prefix = "pbkdf2";
    public const int Iterations = 600_000;
    public const int MinIterations = 10_000;
    public const int MaxIterations = 5_000_000;
    public const int SaltLength = 16;
    public const int HashLength = 32;

    private const char _separator = '$';

    /// <summary>Hashes <paramref name="password"/> with a fresh random salt and the default iteration count.</summary>
    public static string Hash(string password) => Hash(password, Iterations);

    /// <summary>Hashes with an explicit iteration count (tests use a low one to stay fast).</summary>
    public static string Hash(string password, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt, iterations);
        return string.Join(
            _separator,
            Prefix,
            iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="value"/> parses as a complete, in-range hash string.</summary>
    public static bool IsWellFormed(string? value) => TryParse(value, out _, out _, out _);

    /// <summary>
    /// True when <paramref name="password"/> produces <paramref name="storedHash"/>. A malformed
    /// <paramref name="storedHash"/> never matches.
    /// </summary>
    public static bool Verify(string password, string? storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(storedHash, out var iterations, out var salt, out var hash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), hash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashLength);

    private static bool TryParse(string? value, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var fields = value.Split(_separator);
        if (fields.Length != 4 || !string.Equals(fields[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // NumberStyles.None: digits only, so "+600000", " 600000", and "600_000" are all refused.
        if (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations < MinIterations
            || iterations > MaxIterations)
        {
            return false;
        }

        return TryDecode(fields[2], SaltLength, out salt) && TryDecode(fields[3], HashLength, out hash);
    }

    /// <summary>
    /// Decodes a base64 field that must hold exactly <paramref name="expectedLength"/> bytes. The character
    /// count is checked first so a field of the wrong length is refused before decoding, and whitespace
    /// (which <see cref="Convert"/> would otherwise skip) makes the decoded length come up short.
    /// </summary>
    private static bool TryDecode(string field, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (field.Length != (expectedLength + 2) / 3 * 4)
        {
            return false;
        }

        var buffer = new byte[expectedLength];
        if (!Convert.TryFromBase64String(field, buffer, out var written) || written != expectedLength)
        {
            return false;
        }

        bytes = buffer;
        return true;
    }
}
