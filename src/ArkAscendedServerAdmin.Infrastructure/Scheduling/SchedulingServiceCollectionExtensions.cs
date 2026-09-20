using ArkAscendedServerAdmin.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Scheduling;

/// <summary>Registration for the scheduled-actions module (B3).</summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared RCON send path and the scheduled-action runner. Depends on the process manager, instance
    /// locks, maintenance gate, RCON client, generated-config writer, and console service being registered by their
    /// own modules.
    /// </summary>
    public static IServiceCollection AddArkScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRconOperations, RconOperations>();
        services.AddSingleton<ScheduledActionRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledActionRunner>());
        return services;
    }
}
