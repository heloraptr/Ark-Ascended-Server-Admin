namespace ArkAscendedServerAdmin.Server.Middleware;

/// <summary>
/// Refuses any request whose effective scheme is not HTTPS with 403. Runs right after forwarded-header
/// processing, so a request through the trusted reverse proxy counts as HTTPS while a direct HTTP hit on a
/// LAN rebind fails closed. <c>ArkAdmin:AllowInsecureHttp</c> (development only) disables it.
/// </summary>
public sealed class HttpsGuardMiddleware(RequestDelegate next, bool allowInsecureHttp)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (allowInsecureHttp || context.Request.IsHttps)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("HTTPS is required. Connect through the reverse proxy.", context.RequestAborted);
    }
}
