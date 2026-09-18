using System.Net;
using System.Reflection;
using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Components.Layout;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Infrastructure;
using ArkAscendedServerAdmin.Server;
using ArkAscendedServerAdmin.Server.Auth;
using ArkAscendedServerAdmin.Server.Components;
using ArkAscendedServerAdmin.Server.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using Radzen;

// `--hash-password` runs before the host exists (release plan step A1): no configuration is read and no
// DataRoot is touched, so a broken appsettings.json cannot stop the installer from hashing. Stdin in,
// hash on stdout, nothing else.
if (args is [HashPasswordCommand.Argument])
{
    return HashPasswordCommand.Run(Console.OpenStandardInput(), Console.Out, Console.Error);
}

var isWindowsService = WindowsServiceHelpers.IsWindowsService();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts with System32 as its working directory; anchor the content root to the
    // executable so appsettings.json and wwwroot resolve. Left alone under `dotnet run` so static web
    // assets keep working from the project directory.
    ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null,
});

builder.Services.AddWindowsService(options => options.ServiceName = ServiceExtensions.ApplicationName);

// ---- appsettings.json (host settings; change requires a restart) ----------------------------------
var arkOptions = builder.Configuration.GetSection(ArkAdminOptions.SectionName).Get<ArkAdminOptions>() ?? new ArkAdminOptions();
builder.Services.Configure<ArkAdminOptions>(builder.Configuration.GetSection(ArkAdminOptions.SectionName));

var layout = ServiceExtensions.ResolveDataRoot(arkOptions, builder.Environment.ContentRootPath);
layout.EnsureDirectories(); // the Data Protection key ring needs its directory before the host builds

var bindUrls = builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
    .Select(endpoint => endpoint["Url"])
    .Where(url => !string.IsNullOrWhiteSpace(url))
    .Select(url => url!)
    .ToList();

// MinVer stamps the informational version (1.0.0+sha, or 0.0.0-preview.0.N+sha off a tag) on every
// assembly; it is shown on Settings and in the rail footer (release plan step A4).
var version = typeof(ServiceExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? typeof(ServiceExtensions).Assembly.GetName().Version?.ToString()
    ?? "unknown";

builder.Services.AddSingleton(new HostConfiguration(
    layout.Root,
    bindUrls,
    arkOptions.KnownProxies,
    arkOptions.AllowInsecureHttp,
    PasswordCredential.Resolve(arkOptions.Password, arkOptions.PasswordHash).IsUsable,
    isWindowsService,
    version));

// ---- services ------------------------------------------------------------------------------------
builder.Services.AddArkInfrastructure(layout);
// B0: the detached-jobs registry waits up to 60 s for restore and delete jobs on stop; the host's budget must exceed that.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));
builder.Services.AddArkAuthentication(arkOptions, layout);
builder.Services.AddArkCommands();

builder.Services.AddRadzenComponents();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

var logger = app.Logger;
logger.LogInformation("DataRoot: {DataRoot}", layout.Root);
logger.LogInformation("Version: {Version}", version);

// Resolving the singleton now, rather than at the first login, puts its credential diagnostics (no
// password, both keys set, malformed hash) in the startup log where the installer's event-log tail sees them.
_ = app.Services.GetRequiredService<PasswordSource>();

if (arkOptions.AllowInsecureHttp)
{
    logger.LogWarning("ArkAdmin:AllowInsecureHttp is enabled: plain-HTTP requests are accepted. Development only.");
}

// ---- pipeline ------------------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// 1. Trust X-Forwarded-* only from loopback and the configured proxies, so Request.IsHttps reflects the
//    scheme the reverse proxy terminated.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
foreach (var proxy in arkOptions.KnownProxies)
{
    if (IPAddress.TryParse(proxy, out var address))
    {
        forwarded.KnownProxies.Add(address);
    }
    else
    {
        logger.LogWarning("Ignoring ArkAdmin:KnownProxies entry '{Proxy}': not an IP address.", proxy);
    }
}

app.UseForwardedHeaders(forwarded);

// 2. Fail closed on anything that is not HTTPS after forwarded-header processing.
app.UseMiddleware<HttpsGuardMiddleware>(arkOptions.AllowInsecureHttp);

// 3. Everything goes to /setup until the readiness pipeline reaches Ready (allowlist inside).
app.UseMiddleware<ReadinessRedirectMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealth();

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MainLayout).Assembly);

app.Run();
return 0;
