namespace ArkAscendedServerAdmin.Server;

/// <summary>
/// The <c>ArkAdmin</c> section of <c>appsettings.json</c>: host settings read at startup. Kestrel's bind
/// address lives in the standard <c>Kestrel:Endpoints</c> section and logging under <c>Logging</c>.
/// </summary>
public sealed class ArkAdminOptions
{
    public const string SectionName = "ArkAdmin";

    /// <summary>
    /// Root of all managed data. Empty means <c>%ProgramData%\ArkAscendedServerAdmin</c>; a relative
    /// path is resolved against the content root. Environment variables are expanded.
    /// </summary>
    public string? DataRoot { get; set; }

    /// <summary>The single login password. Empty disables login entirely (and logs an error at startup).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Development only: accept requests whose effective scheme is HTTP. In production every non-HTTPS
    /// request (after trusted-proxy forwarded headers) is answered with 403.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Addresses of reverse proxies whose <c>X-Forwarded-*</c> headers are trusted (the Nginx Proxy
    /// Manager host). Loopback is always trusted.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];
}
