using ArkAscendedServerAdmin.Backups;
using Microsoft.Extensions.DependencyInjection;

namespace ArkAscendedServerAdmin.Infrastructure.Backups;

/// <summary>Registration for the backup module (plan step 28).</summary>
public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the backup job (plan step 28) and its scheduler. Depends on the process manager, instance
    /// locks, RCON client, generated-config writer, and console service being registered by their own modules.
    /// </summary>
    public static IServiceCollection AddArkBackups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<BackupScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());
        return services;
    }
}
