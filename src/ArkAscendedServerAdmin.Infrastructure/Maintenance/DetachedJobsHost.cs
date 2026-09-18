using ArkAscendedServerAdmin.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Maintenance;

/// <summary>
/// Ties <see cref="DetachedJobs"/> to the host lifetime (B0): on stop it refuses new jobs and waits for the active
/// ones, logging any that outlast the wait. Its own hosted service because <c>StartupOrchestrator</c> has no
/// shutdown hook and is not given one.
/// </summary>
public sealed class DetachedJobsHost(DetachedJobs jobs, ILogger<DetachedJobsHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var active = jobs.ActiveNames;
        if (active.Count > 0)
        {
            logger.LogInformation("Waiting up to {Seconds} s for {Count} detached job(s): {Names}.", DetachedJobs.ShutdownWait.TotalSeconds, active.Count, string.Join(", ", active));
        }

        var remaining = await jobs.ShutdownAsync(cancellationToken);
        if (remaining.Count > 0)
        {
            logger.LogWarning("Shutting down with {Count} detached job(s) still running: {Names}.", remaining.Count, string.Join(", ", remaining));
        }
    }
}
