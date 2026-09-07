using ArkAscendedServerAdmin.Consoles;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Consoles;

public static class ConsoleServiceCollectionExtensions
{
    /// <summary>Registers the console buffer (plan step 22) and the log-tail output source factory (plan step 14).</summary>
    public static IServiceCollection AddArkConsoles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConsoleService, InMemoryConsoleService>();
        services.AddSingleton<IOutputSourceFactory, LogTailOutputSourceFactory>();

        return services;
    }
}
