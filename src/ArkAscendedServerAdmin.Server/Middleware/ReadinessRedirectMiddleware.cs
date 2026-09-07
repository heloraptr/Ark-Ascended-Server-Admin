using ArkAscendedServerAdmin.Startup;

namespace ArkAscendedServerAdmin.Server.Middleware;

/// <summary>
/// Sends every page to <c>/setup</c> while the readiness pipeline has not reached <c>Ready</c>, with an
/// explicit allowlist so setup itself keeps working: the setup and login pages, the Blazor hub, framework
/// and static-web-asset paths, and anything with a file extension (static files). Readiness is also
/// enforced inside the services (public Start is refused while not Ready), never by routing alone.
/// </summary>
public sealed class ReadinessRedirectMiddleware(RequestDelegate next, IReadinessMonitor readiness)
{
    private static readonly string[] _allowedPrefixes =
        ["/setup", "/login", "/logout", "/Error", "/_blazor", "/_framework", "/_content"];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (readiness.Current.IsReady || IsAllowed(context.Request.Path))
        {
            return next(context);
        }

        context.Response.Redirect("/setup");
        return Task.CompletedTask;
    }

    internal static bool IsAllowed(PathString path)
    {
        if (Path.HasExtension(path.Value))
        {
            return true;
        }

        foreach (var prefix in _allowedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
