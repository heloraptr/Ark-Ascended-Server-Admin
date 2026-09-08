using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Server.Auth;
using ArkAscendedServerAdmin.Server.Commands;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace ArkAscendedServerAdmin.Server;

public static class ServiceExtensions
{
    public const string ApplicationName = "ArkAscendedServerAdmin";

    /// <summary>
    /// Resolves <c>DataRoot</c> from <see cref="ArkAdminOptions"/>: empty → <c>%ProgramData%</c> default,
    /// relative → against the content root, environment variables expanded.
    /// </summary>
    public static DataRootLayout ResolveDataRoot(ArkAdminOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = string.IsNullOrWhiteSpace(options.DataRoot)
            ? DataRootLayout.DefaultRoot
            : Environment.ExpandEnvironmentVariables(options.DataRoot.Trim());
        return new DataRootLayout(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured));
    }

    /// <summary>
    /// Cookie authentication for the single password (plan step 8): 12 h sliding expiry, secure cookie,
    /// password-hash claim validated on every request, circuits revalidated every 5 minutes, and a
    /// fallback policy that makes every endpoint require the cookie unless it is <c>[AllowAnonymous]</c>.
    /// </summary>
    public static IServiceCollection AddArkAuthentication(this IServiceCollection services, ArkAdminOptions options, DataRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(layout.Keys));

        services.AddHttpContextAccessor();
        services.AddSingleton<PasswordSource>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ArkCookieEvents>();
        services.AddScoped<ILoginService, LoginService>();
        services.AddScoped<IAuthorizationGuard, AuthorizationGuard>();
        services.AddScoped<AuthenticationStateProvider, CircuitAuthenticationStateProvider>();
        services.AddCascadingAuthenticationState();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = $"{ApplicationName}.Auth";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                // A cookie attribute only — the HTTPS guard middleware is what refuses HTTP. Relaxed in
                // development so a plain-HTTP dev run can keep its session.
                cookie.Cookie.SecurePolicy = options.AllowInsecureHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan = LoginService.SessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.LoginPath = "/login";
                cookie.LogoutPath = "/logout";
                cookie.AccessDeniedPath = "/login";
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.EventsType = typeof(ArkCookieEvents);
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    /// <summary>The scoped command facades the UI calls; every method re-checks authorization first.</summary>
    public static IServiceCollection AddArkCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ISettingsCommands, SettingsCommands>();
        services.AddScoped<IMaintenanceCommands, MaintenanceCommands>();
        services.AddScoped<IInstanceCommands, InstanceCommands>();
        services.AddScoped<IClusterCommands, ClusterCommands>();
        services.AddScoped<IConfigCommands, ConfigCommands>();
        services.AddScoped<IModCommands, ModCommands>();
        services.AddScoped<IPlayerCommands, PlayerCommands>();
        services.AddScoped<IMapCommands, MapCommands>();
        return services;
    }
}
