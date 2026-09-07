using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Maintenance;

/// <summary>Registration for the update-flow and instance-delete module (plan steps 11, 29, 30).</summary>
public static class MaintenanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the update flow (plan step 29), which also serves as the <see cref="IMaintenanceRecovery"/>
    /// startup step (plan step 11), and the instance delete job (plan step 30). Registered after
    /// <c>AddArkInfrastructure</c> this replaces the Phase 1 no-op recovery step.
    /// </summary>
    public static IServiceCollection AddArkMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());
        services.AddSingleton<IMaintenanceRecovery>(sp => sp.GetRequiredService<UpdateService>());
        services.AddSingleton<IInstanceDeleteService, InstanceDeleteService>();
        return services;
    }
}
