using ArkAscendedServerAdmin.Provisioning;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the junction layout service (plan step 27), the INI source store and the generated-config
    /// writer (plan step 16) as singletons. Expects <c>DataRootLayout</c>, <c>TimeProvider</c>, logging, and
    /// the <c>AppDbContext</c> factory to be registered by <c>AddArkInfrastructure</c>.
    /// </summary>
    public static IServiceCollection AddArkProvisioning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IInstanceLayoutService, InstanceLayoutService>();
        services.AddSingleton<IIniSourceStore, IniSourceStore>();
        services.AddSingleton<IGeneratedConfigWriter, GeneratedConfigWriter>();

        return services;
    }
}
