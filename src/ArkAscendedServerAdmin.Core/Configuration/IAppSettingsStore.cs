namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Reads and writes the database-backed <see cref="AppSettings"/>. Implementations are singletons used from
/// background services and Blazor circuits alike, so they must be safe for concurrent use.
/// </summary>
public interface IAppSettingsStore
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists every setting; throws <see cref="ArgumentException"/> when validation fails.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
