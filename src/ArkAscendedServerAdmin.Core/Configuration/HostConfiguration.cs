namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Read-only view of the <c>appsettings.json</c> values the UI is allowed to display. These are host
/// settings (change requires a service restart), as opposed to <see cref="AppSettings"/> which live in the
/// database and are edited at runtime. <see cref="IsPasswordConfigured"/> means a usable credential
/// (plaintext or a well-formed hash); <see cref="IsWindowsService"/> is the hosting-model label; and
/// <see cref="Version"/> is the assembly informational version (MinVer: <c>1.0.0+sha</c>) shown on the
/// Settings page and in the rail footer.
/// </summary>
public sealed record HostConfiguration(
    string DataRoot,
    IReadOnlyList<string> BindUrls,
    IReadOnlyList<string> KnownProxies,
    bool AllowInsecureHttp,
    bool IsPasswordConfigured,
    bool IsWindowsService,
    string Version);
