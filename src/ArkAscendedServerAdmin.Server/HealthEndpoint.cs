namespace ArkAscendedServerAdmin.Server;

/// <summary>
/// Anonymous liveness probe (release plan step A3): <c>GET /healthz</c> answers a fixed line of text with
/// no version and no state. The HTTPS guard and the readiness redirect both exempt it, so the installer
/// probe and any monitor get the same answer in every bind mode.
/// </summary>
public static class HealthEndpoint
{
    public const string Path = "/healthz";
    public const string Body = "ArkAscendedServerAdmin ok";

    public static IEndpointConventionBuilder MapHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(Path, () => Results.Text(Body)).AllowAnonymous();
    }
}
