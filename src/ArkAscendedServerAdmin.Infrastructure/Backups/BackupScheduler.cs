using System.Collections.Concurrent;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Backups;

/// <summary>
/// The backup timer (plan step 28): once a minute, every instance whose newest record (any outcome) is
/// older than its interval is due. A due <see cref="InstanceState.Running"/> instance gets a backup;
/// a due <see cref="InstanceState.Unreachable"/> or <see cref="InstanceState.StartingUnconfirmed"/> one gets a
/// "skipped — RCON unreachable" record so the missed schedule is visible (plan step 22); anything else
/// waits. A backup never overlaps another for the same instance.
/// </summary>
public sealed class BackupScheduler(
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settingsStore,
    IProcessManager processManager,
    IBackupService backupService,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, Task> _inFlight = new();

    /// <summary>How often the scheduler looks for due instances.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, timeProvider, stoppingToken);
                await RunDueBackupsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Backup scheduler tick failed; it will try again next minute.");
            }
        }
    }

    /// <summary>One scheduler pass; exposed so tests can drive it without waiting for the timer.</summary>
    public async Task RunDueBackupsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        List<(int Id, int? Interval)> instances;
        var newest = new Dictionary<int, DateTimeOffset>();
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            instances = await db.Instances.AsNoTracking().Select(i => new ValueTuple<int, int?>(i.Id, i.BackupIntervalMinutes)).ToListAsync(cancellationToken);
            foreach (var (id, _) in instances)
            {
                // Ordered by id rather than CreatedAt: SQLite cannot order DateTimeOffset columns, and ids are monotonic.
                var last = await db.BackupRecords.AsNoTracking()
                    .Where(r => r.InstanceId == id)
                    .OrderByDescending(r => r.Id)
                    .Select(r => (DateTimeOffset?)r.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (last is { } created)
                {
                    newest[id] = created;
                }
            }
        }

        foreach (var (id, intervalMinutes) in instances)
        {
            var interval = TimeSpan.FromMinutes(intervalMinutes ?? settings.DefaultBackupIntervalMinutes);
            if (newest.TryGetValue(id, out var last) && now - last < interval)
            {
                continue;
            }

            var runtime = processManager.GetRuntime(id);
            if (runtime.State is not (InstanceState.Running or InstanceState.Unreachable or InstanceState.StartingUnconfirmed))
            {
                continue;
            }

            if (_inFlight.TryGetValue(id, out var running) && !running.IsCompleted)
            {
                continue;
            }

            _inFlight[id] = RunOneAsync(id, cancellationToken);
        }
    }

    private async Task RunOneAsync(int instanceId, CancellationToken cancellationToken)
    {
        try
        {
            // The service itself records "skipped — RCON unreachable" for the non-Running live states.
            await backupService.BackupNowAsync(instanceId, isManual: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ArgumentException or IOException)
        {
            logger.LogError(ex, "Scheduled backup of instance {InstanceId} threw.", instanceId);
        }
    }
}
