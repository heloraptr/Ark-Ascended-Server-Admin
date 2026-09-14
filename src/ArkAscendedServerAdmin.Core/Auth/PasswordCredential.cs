namespace ArkAscendedServerAdmin.Auth;

/// <summary>How the login credential was resolved from configuration.</summary>
public enum PasswordCredentialKind
{
    /// <summary>Neither key is set: every login is refused.</summary>
    None,

    /// <summary><c>ArkAdmin:Password</c> only; the snapshot is the unsalted SHA-256 of the plaintext.</summary>
    Plaintext,

    /// <summary><c>ArkAdmin:PasswordHash</c> (which wins when both are set); the snapshot is the stored hash string.</summary>
    Pbkdf2,

    /// <summary><c>ArkAdmin:PasswordHash</c> is present but not a valid hash string: every login is refused, no fallback.</summary>
    Malformed,
}

/// <summary>
/// One immutable resolution of <c>ArkAdmin:Password</c> and <c>ArkAdmin:PasswordHash</c> (release plan
/// step A1). <see cref="Verify"/> returns the <see cref="Snapshot"/> it verified against so the caller
/// issues the cookie claim from that exact value and never from a later read of the live credential.
/// </summary>
public sealed class PasswordCredential
{
    public static readonly PasswordCredential None = new(PasswordCredentialKind.None, null, bothConfigured: false);

    private PasswordCredential(PasswordCredentialKind kind, string? snapshot, bool bothConfigured)
    {
        Kind = kind;
        Snapshot = snapshot;
        BothConfigured = bothConfigured;
    }

    public PasswordCredentialKind Kind { get; }

    /// <summary>
    /// The value the password-hash cookie claim carries and <see cref="PasswordClaimValidator"/> compares
    /// against; null when no usable credential is configured, which makes every existing cookie invalid too.
    /// </summary>
    public string? Snapshot { get; }

    /// <summary>Both keys were set; the hash was used and <c>Password</c> ignored (worth a warning).</summary>
    public bool BothConfigured { get; }

    public bool IsUsable => Kind is PasswordCredentialKind.Plaintext or PasswordCredentialKind.Pbkdf2;

    public static PasswordCredential Resolve(string? password, string? passwordHash)
    {
        var hasPassword = !string.IsNullOrEmpty(password);
        if (!string.IsNullOrEmpty(passwordHash))
        {
            return Pbkdf2PasswordHash.IsWellFormed(passwordHash)
                ? new PasswordCredential(PasswordCredentialKind.Pbkdf2, passwordHash, hasPassword)
                : new PasswordCredential(PasswordCredentialKind.Malformed, null, hasPassword);
        }

        return hasPassword
            ? new PasswordCredential(PasswordCredentialKind.Plaintext, PasswordHash.Compute(password!), bothConfigured: false)
            : None;
    }

    /// <summary>The snapshot <paramref name="candidate"/> matched, or null when it did not (or nothing is usable).</summary>
    public string? Verify(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var matched = Kind switch
        {
            PasswordCredentialKind.Plaintext => PasswordHash.Matches(PasswordHash.Compute(candidate), Snapshot),
            PasswordCredentialKind.Pbkdf2 => Pbkdf2PasswordHash.Verify(candidate, Snapshot),
            _ => false,
        };
        return matched ? Snapshot : null;
    }
}
