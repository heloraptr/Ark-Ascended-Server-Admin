using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Mods;

/// <summary>
/// The daily metadata poll (B8): once a day it re-fetches CurseForge metadata for the mod library so the
/// "changed since the last launch" badge appears without anyone pressing Refresh. It runs only when a
/// CurseForge API key is configured, and it has no interactive user, so it calls
/// <see cref="IModMetadataRefresher"/> directly rather than the guarded mod facade.
/// </summary>
public sealed class ModMetadataPoll(
    IServiceScopeFactory scopeFactory,
    IAppSettingsStore settings,
    TimeProvider timeProvider,
    ILogger<ModMetadataPoll> logger) : BackgroundService
{
    /// <summary>How long the poll waits between refreshes.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken);
                await RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed poll is logged and tomorrow's tries again; letting it escape would stop the service.
                logger.LogError(ex, "Mod metadata poll failed; it will try again tomorrow.");
            }
        }
    }

    /// <summary>One poll pass; exposed so tests can drive it without waiting a day.</summary>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace((await settings.GetAsync(cancellationToken)).CurseForgeApiKey))
        {
            logger.LogDebug("Mod metadata poll skipped: no CurseForge API key is configured.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var refresher = scope.ServiceProvider.GetRequiredService<IModMetadataRefresher>();
        var result = await refresher.RefreshAsync(cancellationToken);
        if (!result.Succeeded)
        {
            logger.LogWarning("Mod metadata poll could not refresh the library: {Problem}", result.Error);
            return;
        }

        logger.LogInformation("Mod metadata poll updated {Count} library entries.", result.Value);
    }
}
