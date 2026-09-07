using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Infrastructure.CurseForge;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Install;
using ArkAscendedServerAdmin.Infrastructure.Startup;
using ArkAscendedServerAdmin.Install;
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
        services.TryAddSingleton<IGameInstaller, PlaceholderGameInstaller>();
        services.AddSingleton<NoOpRecoverySteps>();
        services.TryAddSingleton<IProcessReconciler>(sp => sp.GetRequiredService<NoOpRecoverySteps>());
        services.TryAddSingleton<ISteamCmdReconciler>(sp => sp.GetRequiredService<NoOpRecoverySteps>());
        services.TryAddSingleton<IMaintenanceRecovery>(sp => sp.GetRequiredService<NoOpRecoverySteps>());

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
