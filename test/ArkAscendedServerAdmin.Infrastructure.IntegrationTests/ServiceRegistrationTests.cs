using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Infrastructure.Startup;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Scheduling;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests;

/// <summary>
/// Builds the real container the way the host does and resolves every public service, so a missing or
/// circular registration fails here instead of at service start.
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void RunsOnWindows() => Assert.True(OperatingSystem.IsWindows());

    [Fact]
    public void AddArkInfrastructure_ResolvesEveryPublicService()
    {
        using var root = new TempDataRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        services.AddSingleton(new HostConfiguration(root.Layout.Root, ["http://127.0.0.1:5000"], [], true, true, false, "0.0.0-test"));
        services.AddArkInfrastructure(root.Layout);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Type[] required =
        [
            typeof(IConsoleService), typeof(IOutputSourceFactory),
            typeof(IGameInstallChecker), typeof(IGameInstaller), typeof(ISteamCmdRunner), typeof(ISteamCmdReconciler),
            typeof(IInstanceLayoutService), typeof(IIniSourceStore), typeof(IGeneratedConfigWriter),
            typeof(IProcessManager), typeof(IProcessReconciler), typeof(IInstanceLocks), typeof(IMaintenanceGate),
            typeof(IPlayerTracker), typeof(IBackupService), typeof(IUpdateService), typeof(IInstanceDeleteService), typeof(IMaintenanceRecovery),
            typeof(IReadinessMonitor), typeof(IStartupControl), typeof(StartupOrchestrator),
            typeof(IRconOperations), typeof(ScheduledActionRunner),
        ];

        foreach (var type in required)
        {
            Assert.True(provider.GetService(type) is not null, $"{type.Name} is not registered.");
        }

        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
