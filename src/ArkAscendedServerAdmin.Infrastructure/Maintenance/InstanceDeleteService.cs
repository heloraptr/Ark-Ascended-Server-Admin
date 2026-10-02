using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Maintenance;

/// <summary>
/// Instance delete (plan step 30). <see cref="DeleteAsync"/> only reports acceptance: it takes the instance
/// lock without waiting ("operation in progress" when held) and runs the job in the background under that lease — stop
/// with verified exit through <see cref="IProcessManager.StopUnderLeaseAsync"/> (the locks are not reentrant, B0; a
/// failed verification aborts the job with the reason), firewall rules, junctions,
/// archive or delete <c>Saved</c>, the backup archives if asked for, then the database rows. Completion is visible through the instance
/// disappearing (and a line on its console); the lock is released in a <c>finally</c>.
/// </summary>
public sealed class InstanceDeleteService(
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    IInstanceLocks locks,
    IRestoreJournals restoreJournals,
    DetachedJobs jobs,
    IProcessManager processManager,
    IFirewallRules firewall,
    IInstanceLayoutService layoutService,
    IConsoleService console,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<InstanceDeleteService> logger) : IInstanceDeleteService
{
    private readonly object _sync = new();
    private Task _lastJob = Task.CompletedTask;

    /// <summary>The most recently accepted delete job (a completed task when idle); tests and shutdown await it.</summary>
    public Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _lastJob;
            }
        }
    }

    public async Task<OperationOutcome> DeleteAsync(int instanceId, InstanceDeleteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string slug;
        int? clusterId;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var found = await db.Instances.AsNoTracking().Where(i => i.Id == instanceId).Select(i => new { i.Slug, i.ClusterId }).SingleOrDefaultAsync(cancellationToken);
            if (found is null)
            {
                return OperationOutcome.Rejected($"Instance {instanceId} does not exist.");
            }

            slug = found.Slug;
            clusterId = found.ClusterId;
        }

        // B2: an unresolved restore owns the world files and the safety copy under the backup folder; a cluster restore
        // in flight owns the shared directory. Deleting either out from under it is refused.
        if (restoreJournals.FindForInstance(instanceId) is { } journal)
        {
            return OperationOutcome.Rejected(journal.RefusalReason("this instance"));
        }

        if (clusterId is { } cluster && locks.IsClusterReserved(cluster))
        {
            return OperationOutcome.Rejected("The cluster is reserved by a restore; try again when it finishes.");
        }

        var lease = locks.TryAcquire(instanceId);
        if (lease is null)
        {
            return OperationOutcome.Rejected("An operation is in progress for this instance; try again when it finishes.");
        }

        // B4: the delete owns the instance now, so a crash pending for it is never relaunched; dismissed again just before
        // the lease is released, which also voids an exit that was still in cleanup meanwhile.
        processManager.DismissCrash(instanceId);
        var registration = jobs.TryBegin($"delete {slug}");
        if (registration is null)
        {
            processManager.DismissCrash(instanceId);
            lease.Dispose();
            return OperationOutcome.Rejected("The service is stopping; the delete was not started.");
        }

        // The job is detached from the caller (a closed browser tab must not abort a half-done delete), but the
        // caller waits for it so "Deleted" is only reported, and the instance list only reloaded, once the rows are gone.
        var job = Task.Run(() => RunAsync(instanceId, slug, options, lease, registration, lifetime.ApplicationStopping), CancellationToken.None);
        lock (_sync)
        {
            _lastJob = job;
        }

        return await job.WaitAsync(cancellationToken);
    }

    private async Task<OperationOutcome> RunAsync(int instanceId, string slug, InstanceDeleteOptions options, IInstanceLease lease, IDisposable registration, CancellationToken cancellationToken)
    {
        try
        {
            if (processManager.GetRuntime(instanceId).HasLiveProcess)
            {
                Announce(instanceId, "Delete: stopping the instance with verified exit.");
                var stop = await processManager.StopUnderLeaseAsync(lease, new StopOptions(RequireVerifiedExit: true), cancellationToken);
                if (!stop.Succeeded)
                {
                    Announce(instanceId, $"Delete aborted: the instance could not be stopped with a verified exit ({stop.Error}).", ConsoleLineKind.Error);
                    return OperationOutcome.Rejected($"The instance could not be stopped with a verified exit: {stop.Error}");
                }
            }

            firewall.RemoveInstanceRules(instanceId);
            await layoutService.RemoveJunctionsAsync(slug, cancellationToken);
            var archive = await layoutService.RetireAsync(slug, options.KeepWorldData, cancellationToken);
            if (options.DeleteBackups)
            {
                TryDeleteDirectory(layout.InstanceBackupDirectory(slug));
            }

            await DeleteRowsAsync(instanceId, cancellationToken);

            var world = archive is null ? "its world data was removed" : $"its world data was archived to {archive}";
            var backups = options.DeleteBackups
                ? "its backups were deleted"
                : $"its backups were kept in {layout.InstanceBackupDirectory(slug)}";
            Announce(instanceId, $"Instance '{slug}' deleted; {world} and {backups}.");
            return OperationOutcome.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Delete of instance {InstanceId} interrupted by shutdown.", instanceId);
            return OperationOutcome.Rejected("The delete was interrupted by a service shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete of instance {InstanceId} failed.", instanceId);
            Announce(instanceId, $"Delete failed: {ex.Message}", ConsoleLineKind.Error);
            return OperationOutcome.Rejected($"Delete failed: {ex.Message}");
        }
        finally
        {
            processManager.DismissCrash(instanceId);
            lease.Dispose();
            registration.Dispose();
        }
    }

    private async Task DeleteRowsAsync(int instanceId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.InstanceMods.Where(m => m.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
        await db.ExtraOverrides.Where(o => o.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
        await db.IniDocuments.Where(d => d.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
        await db.BackupRecords.Where(b => b.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
        await db.RestoreRecords.Where(r => r.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
        await db.KnownPlayers.Where(p => p.LastInstanceId == instanceId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.LastInstanceId, (int?)null).SetProperty(p => p.IsOnline, false), cancellationToken);
        await db.Instances.Where(i => i.Id == instanceId).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover archives are harmless; the owner can remove the folder by hand.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Announce(int instanceId, string text, ConsoleLineKind kind = ConsoleLineKind.Info)
    {
        logger.Log(kind == ConsoleLineKind.Error ? LogLevel.Error : LogLevel.Information, "Instance {InstanceId}: {Message}", instanceId, text);
        console.Append(ConsoleChannels.Instance(instanceId), new ConsoleLine(timeProvider.GetUtcNow(), text, kind));
    }
}
