using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.Infrastructure.CurseForge;
using ArkAscendedServerAdmin.Mods;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Mods;

/// <summary>Registration for the mod library module: the CurseForge client, the metadata refresh, and its daily poll (B8).</summary>
public static class ModServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CurseForge HTTP client with the API-key handler, the metadata refresher the mod facade
    /// calls, and the daily poll that calls the refresher on its own. The refresher is transient because it
    /// takes the typed HTTP client; the poll resolves one per pass from its own scope.
    /// </summary>
    public static IServiceCollection AddArkMods(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ApiOptions();
        services.AddSingleton(options);
        services.AddTransient<CurseForgeApiKeyHandler>();
        services.AddHttpClient<ICurseForgeApi, CurseForgeApi>(client => client.BaseAddress = new Uri(options.BaseUrl))
            .AddHttpMessageHandler<CurseForgeApiKeyHandler>();

        services.AddTransient<IModMetadataRefresher, ModMetadataRefresher>();
        services.AddSingleton<ModMetadataPoll>();
        services.AddHostedService(sp => sp.GetRequiredService<ModMetadataPoll>());
        return services;
    }
}
