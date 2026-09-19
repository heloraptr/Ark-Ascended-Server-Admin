using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Launch;
using ArkAscendedServerAdmin.Naming;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade for the cluster list and cluster detail pages.</summary>
public sealed class ClusterCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    IInstanceCommands instanceCommands,
    IInstanceLocks locks,
    IRestoreJournals restoreJournals,
    TimeProvider timeProvider,
    ILogger<ClusterCommands> logger) : IClusterCommands
{
    public async Task<IReadOnlyList<ClusterListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Clusters.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ClusterListItem(c.Id, c.Name, c.Slug, c.ClusterKey, c.Instances.Count, c.Mods.Count(m => m.Enabled)))
            .ToListAsync(cancellationToken);
    }

    public async Task<ClusterDetail?> GetAsync(int clusterId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cluster = await db.Clusters.AsNoTracking()
            .Include(c => c.Mods).ThenInclude(m => m.Mod)
            .Include(c => c.Instances).ThenInclude(i => i.Map)
            .Include(c => c.Instances).ThenInclude(i => i.Mods)
            .SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return null;
        }

        var instances = new List<InstanceSummary>();
        foreach (var instance in cluster.Instances.OrderBy(i => i.Name))
        {
            instances.Add(CommandSupport.ToSummary(instance, cluster.Mods, await CommandSupport.LastBackupAsync(db, instance.Id, cancellationToken)));
        }

        return new ClusterDetail(cluster, cluster.Mods.OrderBy(m => m.Order).Select(m => new ModListItem(m.Mod!, m.Enabled)).ToList(), instances);
    }

    public async Task<RestoreJournal?> GetRestoreJournalAsync(int clusterId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return restoreJournals.FindForCluster(clusterId);
    }

    public async Task<CommandResult<int>> CreateAsync(string name, ConfigSourceKind source, int? sourceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var problems = new List<string>(CommandSupport.ValidateName(name, "Cluster"));
        var trimmed = name?.Trim() ?? string.Empty;
        if (problems.Count == 0 && await db.Clusters.AnyAsync(c => c.Name.ToLower() == trimmed.ToLower(), cancellationToken))
        {
            problems.Add($"A cluster named '{trimmed}' already exists.");
        }

        if (problems.Count > 0)
        {
            return CommandResult<int>.Fail(problems);
        }

        var slug = Slug.Generate(trimmed, await CommandSupport.ReservedSlugsAsync(db, layout, cancellationToken));
        var cluster = new Cluster
        {
            Name = trimmed,
            Slug = slug,
            ClusterKey = slug,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Clusters.Add(cluster);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(layout.ClusterDirectory(slug));
            await ((InstanceCommands)instanceCommands).SeedIniAsync(IniOwner.ForCluster(cluster.Id), source, sourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Provisioning cluster {Slug} failed; removing the row again.", slug);
            db.Clusters.Remove(cluster);
            await db.SaveChangesAsync(cancellationToken);
            return CommandResult<int>.Fail($"The cluster folder could not be prepared: {ex.Message}");
        }

        return CommandResult<int>.Ok(cluster.Id);
    }

    public async Task<CommandResult> SaveAsync(int clusterId, ClusterEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cluster = await db.Clusters.SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return CommandResult.Fail("The cluster no longer exists.");
        }

        var name = edit.Name.Trim();
        var key = edit.ClusterKey.Trim();
        var problems = new List<string>(CommandSupport.ValidateName(edit.Name, "Cluster"));
        if (problems.Count == 0 && await db.Clusters.AnyAsync(c => c.Id != clusterId && c.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            problems.Add($"A cluster named '{name}' already exists.");
        }

        if (key.Length == 0)
        {
            problems.Add("Cluster id is required; it is passed as -clusterid so members can transfer between each other.");
        }
        else if (key.Length > 64)
        {
            problems.Add("Cluster id must be 64 characters or fewer.");
        }
        else if (ReservedKeys.ValidateTypedValue("ClusterKey", key) is { } keyProblem)
        {
            problems.Add(keyProblem);
        }
        else if (key.Any(char.IsWhiteSpace))
        {
            problems.Add("Cluster id must not contain spaces.");
        }

        if (problems.Count > 0)
        {
            return CommandResult.Fail(problems);
        }

        cluster.Name = name;
        cluster.ClusterKey = key;
        cluster.AdminWhitelist = CommandSupport.NormalizeWhitelist(edit.AdminWhitelist);
        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> SaveLaunchFlagsAsync(int clusterId, LaunchFlags flags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        await guard.EnsureAuthorizedAsync(cancellationToken);

        var problems = CommandSupport.ValidateLaunchFlags(flags);
        if (problems.Count > 0)
        {
            return CommandResult.Fail(problems);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cluster = await db.Clusters.SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return CommandResult.Fail("The cluster no longer exists.");
        }

        CommandSupport.CopyLaunchFlags(flags, cluster.LaunchFlags);
        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> SetModsAsync(int clusterId, IReadOnlyList<ModSelection> orderedMods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMods);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cluster = await db.Clusters.Include(c => c.Mods).SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return CommandResult.Fail("The cluster no longer exists.");
        }

        var mods = orderedMods.DistinctBy(m => m.ModId).ToList();
        var ids = mods.Select(m => m.ModId).ToList();
        var known = await db.ModLibrary.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken);
        if (known.Count != ids.Count)
        {
            return CommandResult.Fail("One of the chosen mods is no longer in the library.");
        }

        if (await MapModGuard.FindProblemAsync(db, ids, cancellationToken) is { } mapModProblem)
        {
            return CommandResult.Fail(mapModProblem);
        }

        // In place, not replaced: see InstanceCommands.SetModsAsync.
        var existing = cluster.Mods.ToDictionary(m => m.ModId);
        db.ClusterMods.RemoveRange(cluster.Mods.Where(m => !ids.Contains(m.ModId)));
        for (var order = 0; order < mods.Count; order++)
        {
            if (existing.TryGetValue(mods[order].ModId, out var row))
            {
                row.Order = order;
                row.Enabled = mods[order].Enabled;
            }
            else
            {
                cluster.Mods.Add(new ClusterMod { ClusterId = clusterId, ModId = mods[order].ModId, Enabled = mods[order].Enabled, Order = order });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> DeleteAsync(int clusterId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        if (locks.IsClusterReserved(clusterId))
        {
            return CommandResult.Fail("The cluster is reserved by a restore; try again when it finishes.");
        }

        if (restoreJournals.FindForCluster(clusterId) is { } journal)
        {
            return CommandResult.Fail(journal.RefusalReason("the cluster"));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cluster = await db.Clusters.Include(c => c.Mods).Include(c => c.IniDocuments).SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return CommandResult.Fail("The cluster no longer exists.");
        }

        var members = await db.Instances.AsNoTracking().Where(i => i.ClusterId == clusterId).Select(i => i.Name).ToListAsync(cancellationToken);
        if (members.Count > 0)
        {
            return CommandResult.Fail($"Move or delete its instances first: {string.Join(", ", members)}.");
        }

        db.IniDocuments.RemoveRange(cluster.IniDocuments);
        db.ClusterMods.RemoveRange(cluster.Mods);
        db.Clusters.Remove(cluster);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deleted cluster {Name}; its directory {Directory} was left in place.", cluster.Name, layout.ClusterDirectory(cluster.Slug));
        return CommandResult.Ok;
    }
}
