using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Startup;

/// <summary>
/// The startup readiness pipeline (plan step 11): data tree → migrate + seed → reconcile game processes →
/// reconcile SteamCMD → resume maintenance → verify install (install in the background if not) → Ready.
/// Publishes every transition through <see cref="ReadinessMonitor"/>. Runs once per service start; the
/// install step can be re-run through <see cref="IStartupControl"/> after a failure.
/// </summary>
public sealed class StartupOrchestrator(
    DataRootLayout layout,
    DatabaseInitializer databaseInitializer,
    IProcessReconciler processReconciler,
    ISteamCmdReconciler steamCmdReconciler,
    IMaintenanceRecovery maintenanceRecovery,
    IGameInstallChecker installChecker,
    IGameInstaller installer,
    ReadinessMonitor monitor,
    IHostApplicationLifetime lifetime,
    ILogger<StartupOrchestrator> logger) : BackgroundService, IStartupControl
{
    private readonly SemaphoreSlim _installGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            monitor.Publish(ReadinessPhase.Initializing, "Preparing the data directory");
            layout.EnsureDirectories();

            monitor.Publish(ReadinessPhase.Initializing, "Migrating and seeding the database");
            await databaseInitializer.InitializeAsync(stoppingToken);

            monitor.Publish(ReadinessPhase.Recovering, "Reconciling instance processes");
            await processReconciler.ReconcileAsync(stoppingToken);

            monitor.Publish(ReadinessPhase.Recovering, "Reconciling SteamCMD");
            await steamCmdReconciler.ReconcileAsync(stoppingToken);

            monitor.Publish(ReadinessPhase.Recovering, "Resuming interrupted maintenance");
            await maintenanceRecovery.ResumeAsync(stoppingToken);

            await VerifyOrInstallAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Startup pipeline cancelled by shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup pipeline failed.");
            monitor.Publish(ReadinessPhase.Failed, "Startup failed; check the log and restart the service", ex.Message);
        }
    }

    public async Task RetryInstallAsync(CancellationToken cancellationToken = default)
    {
        if (!monitor.Current.CanRetryInstall)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        await VerifyOrInstallAsync(linked.Token);
    }

    private async Task VerifyOrInstallAsync(CancellationToken cancellationToken)
    {
        if (!await _installGate.WaitAsync(0, cancellationToken))
        {
            return; // an install is already running
        }

        try
        {
            var status = installChecker.Check();
            if (!status.IsComplete)
            {
                monitor.Publish(ReadinessPhase.Installing, status.SteamCmdPresent ? "Installing the server" : "Downloading SteamCMD and installing the server");
                var result = await installer.InstallAsync(cancellationToken);
                if (!result.Succeeded)
                {
                    monitor.Publish(ReadinessPhase.InstallFailed, "Install failed", result.Error);
                    return;
                }

                status = installChecker.Check();
                if (!status.IsComplete)
                {
                    monitor.Publish(ReadinessPhase.InstallFailed, "Install finished but could not be verified", status.Detail);
                    return;
                }
            }

            monitor.Publish(ReadinessPhase.Ready, "Ready");
        }
        finally
        {
            _installGate.Release();
        }
    }
}
