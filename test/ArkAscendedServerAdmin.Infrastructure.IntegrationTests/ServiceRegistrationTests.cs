using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Infrastructure.Install;
using ArkAscendedServerAdmin.Infrastructure.Mods;
using ArkAscendedServerAdmin.Infrastructure.Scheduling;
using ArkAscendedServerAdmin.Infrastructure.Startup;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Mods;
using ArkAscendedServerAdmin.Networking;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Scheduling;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Configuration;
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
            typeof(IRconOperations), typeof(ScheduledActionRunner), typeof(IHostAddressProvider),
            typeof(ICurseForgeApi), typeof(IModMetadataRefresher), typeof(ModMetadataPoll),
        ];

        foreach (var type in required)
        {
            Assert.True(provider.GetService(type) is not null, $"{type.Name} is not registered.");
        }

        Assert.NotEmpty(provider.GetServices<IHostedService>());
    }

    /// <summary>Binds <c>ArkAdmin:SteamCmdLiveOutput</c> the way <c>Program.cs</c> does and checks which launcher the container hands out.</summary>
    [Theory]
    [InlineData(null, typeof(PseudoConsoleSteamCmdLauncher))]
    [InlineData("true", typeof(PseudoConsoleSteamCmdLauncher))]
    [InlineData("false", typeof(ProcessSteamCmdLauncher))]
    public void SteamCmdLiveOutput_SelectsTheLauncher(string? liveOutput, Type expected)
    {
        using var root = new TempDataRoot();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(liveOutput is null ? [] : [new("ArkAdmin:SteamCmdLiveOutput", liveOutput)])
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        services.AddSingleton(new HostConfiguration(root.Layout.Root, ["http://127.0.0.1:5000"], [], true, true, false, "0.0.0-test"));
        services.Configure<SteamCmdLauncherOptions>(configuration.GetSection("ArkAdmin"));
        services.AddArkInfrastructure(root.Layout);

        using var provider = services.BuildServiceProvider();

        Assert.IsType(expected, provider.GetRequiredService<ISteamCmdProcessLauncher>());
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
