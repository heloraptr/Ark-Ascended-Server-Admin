namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Read-only view of the <c>appsettings.json</c> values the UI is allowed to display. These are host
/// settings (change requires a service restart), as opposed to <see cref="AppSettings"/> which live in the
/// database and are edited at runtime.
/// </summary>
public sealed record HostConfiguration(
    string DataRoot,
    IReadOnlyList<string> BindUrls,
    IReadOnlyList<string> KnownProxies,
    bool AllowInsecureHttp,
    bool IsPasswordConfigured,
    bool IsWindowsService);
