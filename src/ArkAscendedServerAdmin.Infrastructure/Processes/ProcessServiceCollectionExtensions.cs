using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Rcon;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

public static class ProcessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the concurrency primitives (plan step 19), the RCON client (23), the WMI enumerator and
    /// process manager (21–25), and the firewall rules (26) as singletons. <see cref="IProcessReconciler"/>
    /// resolves to the process manager, replacing the Phase 1 no-op registered by
    /// <c>AddArkInfrastructure</c> whichever order the two are called in. Requires <c>HostConfiguration</c>,
    /// <c>IInstanceLayoutService</c>, <c>IGeneratedConfigWriter</c>, <c>IOutputSourceFactory</c>, and
    /// <c>IConsoleService</c> to be registered by their own extensions.
    /// </summary>
    public static IServiceCollection AddArkProcesses(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<InstanceLocks>();
        services.AddSingleton<IInstanceLocks>(sp => sp.GetRequiredService<InstanceLocks>());
        services.AddSingleton<MaintenanceGate>();
        services.AddSingleton<IMaintenanceGate>(sp => sp.GetRequiredService<MaintenanceGate>());
        services.AddSingleton<LaunchQueue>();
        services.AddSingleton<RecoveryRequests>();

        services.AddSingleton<IRconClient, CoreRconClient>();
        services.AddSingleton<IGameProcessEnumerator, WmiGameProcessEnumerator>();
        services.AddSingleton<IFirewallRules, FirewallRules>();

        services.AddSingleton<ProcessManager>();
        services.AddSingleton<IProcessManager>(sp => sp.GetRequiredService<ProcessManager>());
        services.AddSingleton<IProcessReconciler>(sp => sp.GetRequiredService<ProcessManager>());

        return services;
    }
}
