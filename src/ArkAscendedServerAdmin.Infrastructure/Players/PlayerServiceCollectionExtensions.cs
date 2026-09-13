using ArkAscendedServerAdmin.Players;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Players;

public static class PlayerServiceCollectionExtensions
{
    /// <summary>Registers the player tracker; it subscribes to the consoles when the host starts, before the orchestrator attaches to anything.</summary>
    public static IServiceCollection AddArkPlayers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PlayerTracker>();
        services.AddSingleton<IPlayerTracker>(sp => sp.GetRequiredService<PlayerTracker>());
        services.AddHostedService(sp => sp.GetRequiredService<PlayerTracker>());
        return services;
    }
}
