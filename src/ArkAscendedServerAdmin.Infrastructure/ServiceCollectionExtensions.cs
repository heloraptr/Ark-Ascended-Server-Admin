using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Consoles;
using ArkAscendedServerAdmin.Infrastructure.CurseForge;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Install;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Infrastructure.Networking;
using ArkAscendedServerAdmin.Infrastructure.Players;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Infrastructure.Startup;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Networking;
using ArkAscendedServerAdmin.Startup;
using ArkAscendedServerAdmin.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArkAscendedServerAdmin.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage, install verification, the readiness pipeline, and the CurseForge client. Every
    /// service here is a singleton or hosted service — nothing is tied to a Blazor circuit.
    /// </summary>
    public static IServiceCollection AddArkInfrastructure(this IServiceCollection services, DataRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(layout);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(layout);

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={layout.DatabasePath}"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<IConfigBackupExporter, SqliteConfigBackupExporter>();

        services.AddSingleton<IGameInstallChecker, GameInstallChecker>();
        services.AddSingleton<IHostAddressProvider, HostAddressProvider>();

        // Phase 4 areas, each registered by its own extension. Order matters only in that the process
        // manager and the maintenance services resolve the consoles, SteamCMD, and provisioning services.
        services.AddArkConsoles();
        services.AddArkSteamCmd();
        services.AddArkProvisioning();
        services.AddArkProcesses();
        services.AddArkBackups();
        services.AddArkPlayers();
        services.AddArkMaintenance();
        services.AddArkScheduling();

        services.AddSingleton<ReadinessMonitor>();
        services.AddSingleton<IReadinessMonitor>(sp => sp.GetRequiredService<ReadinessMonitor>());
        services.AddSingleton<StartupOrchestrator>();
        services.AddSingleton<IStartupControl>(sp => sp.GetRequiredService<StartupOrchestrator>());
        services.AddHostedService(sp => sp.GetRequiredService<StartupOrchestrator>());

        services.AddCurseForgeApi();

        return services;
    }

    private static IServiceCollection AddCurseForgeApi(this IServiceCollection services)
    {
        var options = new ApiOptions();
        services.AddSingleton(options);
        services.AddTransient<CurseForgeApiKeyHandler>();
        services.AddHttpClient<ICurseForgeApi, CurseForgeApi>(client => client.BaseAddress = new Uri(options.BaseUrl))
            .AddHttpMessageHandler<CurseForgeApiKeyHandler>();
        return services;
    }
}
