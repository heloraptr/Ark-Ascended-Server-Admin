using ArkAscendedServerAdmin.Auth;
using Microsoft.Extensions.Options;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// The live login credential, resolved from <c>ArkAdmin:Password</c> or <c>ArkAdmin:PasswordHash</c>
/// (release plan step A1; the hash wins when both are set, a malformed hash refuses everything). Follows
/// <c>appsettings.json</c> reloads, so editing the password invalidates every cookie on its next request
/// and every circuit at its next revalidation. Every resolution is logged: the diagnostics for a missing,
/// doubled, or malformed credential appear at startup and again on each reload.
/// </summary>
public sealed class PasswordSource : IDisposable
{
    private readonly ILogger<PasswordSource> _logger;
    private readonly IDisposable? _subscription;
    private volatile PasswordCredential _credential = PasswordCredential.None;

    public PasswordSource(IOptionsMonitor<ArkAdminOptions> options, ILogger<PasswordSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Apply(options.CurrentValue);
        _subscription = options.OnChange(Apply);
    }

    /// <summary>
    /// The value the password-hash claim must carry (SHA-256 hex for a plaintext password, the stored
    /// pbkdf2 string for a hash), or null when nothing usable is configured.
    /// </summary>
    public string? CurrentHash => _credential.Snapshot;

    public PasswordCredentialKind Kind => _credential.Kind;

    public bool IsConfigured => _credential.IsUsable;

    /// <summary>
    /// The credential snapshot <paramref name="password"/> matched, or null. The caller issues the cookie
    /// claim from the returned value: a reload between this call and the sign-in cannot hand an
    /// old-password login the new password's claim.
    /// </summary>
    public string? Verify(string password) => _credential.Verify(password);

    public void Dispose() => _subscription?.Dispose();

    private void Apply(ArkAdminOptions options)
    {
        var credential = PasswordCredential.Resolve(options.Password, options.PasswordHash);
        _credential = credential;

        if (credential.BothConfigured)
        {
            _logger.LogWarning("Both ArkAdmin:Password and ArkAdmin:PasswordHash are set; the hash is used and Password is ignored.");
        }

        switch (credential.Kind)
        {
            case PasswordCredentialKind.None:
                _logger.LogError("No login password is configured (ArkAdmin:PasswordHash or ArkAdmin:Password in appsettings.json). Every login will be refused.");
                break;
            case PasswordCredentialKind.Malformed:
                _logger.LogError("ArkAdmin:PasswordHash is not a valid pbkdf2$<iterations>$<salt>$<hash> string. Every login will be refused; generate one with --hash-password.");
                break;
        }
    }
}
