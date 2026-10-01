using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

public static class InstallServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SteamCMD runner, its download client, the first-run installer, and the startup
    /// SteamCMD reconciler (plan steps 11, 20). Requires <c>IConsoleService</c>, <c>IGameInstallChecker</c>,
    /// <c>IAppSettingsStore</c>, and <c>TimeProvider</c> to be registered as well.
    /// </summary>
    public static IServiceCollection AddArkSteamCmd(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(SteamCmdRunner.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton(new SteamCmdRetryPolicy());
        services.AddOptions<SteamCmdLauncherOptions>();
        services.AddSingleton<ISteamCmdProcessLauncher>(sp =>
            sp.GetRequiredService<IOptions<SteamCmdLauncherOptions>>().Value.SteamCmdLiveOutput
                ? new PseudoConsoleSteamCmdLauncher(sp.GetRequiredService<ILogger<PseudoConsoleSteamCmdLauncher>>())
                : new ProcessSteamCmdLauncher());
        services.AddSingleton<SteamCmdRunner>();
        services.AddSingleton<ISteamCmdRunner>(sp => sp.GetRequiredService<SteamCmdRunner>());
        services.AddSingleton<ISteamCmdProgressMonitor>(sp => sp.GetRequiredService<SteamCmdRunner>());
        services.AddSingleton<IGameInstaller, GameInstaller>();
        services.AddSingleton<ISteamCmdReconciler, SteamCmdReconciler>();

        return services;
    }
}
