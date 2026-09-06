using ArkAscendedServerAdmin.CurseForge;
using ArkAscendedServerAdmin.CurseForge.Models.Services;

namespace ArkAscendedServerAdmin.Server;

public static class ServiceExtensions
{
    /// <summary>
    /// Registers the CurseForge client from the <c>CurseForge</c> configuration section. Phase 1 moves the
    /// API key into App Settings (database); until then it is read from configuration.
    /// </summary>
    public static IServiceCollection AddCurseForgeApi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ApiOptions();
        configuration.GetSection("CurseForge").Bind(options);
        services.AddSingleton(options);

        services.AddHttpClient<ICurseForgeApi, CurseForgeApi>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
            }
        });

        return services;
    }
}
