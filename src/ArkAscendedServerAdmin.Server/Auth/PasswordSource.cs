using ArkAscendedServerAdmin.Auth;
using Microsoft.Extensions.Options;

namespace ArkAscendedServerAdmin.Server.Auth;

/// <summary>
/// The live login password, as a hash. Follows <c>appsettings.json</c> reloads, so editing the password
/// invalidates every cookie on its next request and every circuit at its next revalidation.
/// </summary>
public sealed class PasswordSource : IDisposable
{
    private readonly IDisposable? _subscription;
    private volatile string? _hash;

    public PasswordSource(IOptionsMonitor<ArkAdminOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Apply(options.CurrentValue);
        _subscription = options.OnChange(Apply);
    }

    /// <summary>Hash of the configured password, or null when no password is configured.</summary>
    public string? CurrentHash => _hash;

    public bool IsConfigured => _hash is not null;

    public bool Verify(string password) =>
        IsConfigured && PasswordHash.Matches(PasswordHash.Compute(password), _hash);

    public void Dispose() => _subscription?.Dispose();

    private void Apply(ArkAdminOptions options) =>
        _hash = string.IsNullOrEmpty(options.Password) ? null : PasswordHash.Compute(options.Password);
}
