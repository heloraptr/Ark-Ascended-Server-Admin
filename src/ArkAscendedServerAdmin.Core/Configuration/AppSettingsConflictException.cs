namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Thrown by <see cref="IAppSettingsStore.SaveAsync"/> when the snapshot being saved carries an older
/// <see cref="AppSettings.Version"/> than the store holds: someone else wrote first (a second tab, or a
/// background mutation). The caller reloads and tries again; nothing was written.
/// </summary>
public sealed class AppSettingsConflictException(long expected, long actual)
    : InvalidOperationException($"The settings changed since they were loaded (version {expected} is behind {actual}); reload the page and try again.")
{
    public long ExpectedVersion { get; } = expected;

    public long ActualVersion { get; } = actual;
}
