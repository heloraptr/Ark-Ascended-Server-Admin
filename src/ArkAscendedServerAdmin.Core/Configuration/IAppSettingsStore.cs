namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Reads and writes the database-backed <see cref="AppSettings"/>. Implementations are singletons used from
/// background services and Blazor circuits alike, so they must be safe for concurrent use. Every write bumps
/// <see cref="AppSettings.Version"/>; a write that would change nothing does not count.
/// </summary>
public interface IAppSettingsStore
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a whole snapshot (the Settings page). Throws <see cref="ArgumentException"/> when validation fails
    /// and <see cref="AppSettingsConflictException"/> when the snapshot's <see cref="AppSettings.Version"/> is behind
    /// the store's, in which case nothing is written.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads, mutates, and saves under the store's lock (B0): the way background code changes one setting without
    /// racing the Settings page. <paramref name="mutate"/> must be pure (no file or network work; project to files
    /// afterwards) and may be called more than once. A mutation that returns an equal snapshot writes nothing and
    /// does not bump the version. Returns the snapshot now in the store. Throws <see cref="ArgumentException"/>
    /// when the mutated snapshot fails validation.
    /// </summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default);
}
