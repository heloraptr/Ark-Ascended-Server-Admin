using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
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
    TimeProvider timeProvider,
    ILogger<ClusterCommands> logger) : IClusterCommands
{
    public async Task<IReadOnlyList<ClusterListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Clusters.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ClusterListItem(c.Id, c.Name, c.Slug, c.ClusterKey, c.Instances.Count, c.Mods.Count))
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
            instances.Add(CommandSupport.ToSummary(instance, await CommandSupport.LastBackupAsync(db, instance.Id, cancellationToken)));
        }

        return new ClusterDetail(cluster, cluster.Mods.OrderBy(m => m.Order).Select(m => m.Mod!).ToList(), instances);
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

    public async Task<CommandResult> SetModsAsync(int clusterId, IReadOnlyList<int> orderedModIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedModIds);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cluster = await db.Clusters.Include(c => c.Mods).SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
        if (cluster is null)
        {
            return CommandResult.Fail("The cluster no longer exists.");
        }

        var ids = orderedModIds.Distinct().ToList();
        var known = await db.ModLibrary.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken);
        if (known.Count != ids.Count)
        {
            return CommandResult.Fail("One of the chosen mods is no longer in the library.");
        }

        if (await MapModGuard.FindProblemAsync(db, ids, cancellationToken) is { } mapModProblem)
        {
            return CommandResult.Fail(mapModProblem);
        }

        db.ClusterMods.RemoveRange(cluster.Mods);
        cluster.Mods.Clear();
        for (var order = 0; order < ids.Count; order++)
        {
            cluster.Mods.Add(new ClusterMod { ClusterId = clusterId, ModId = ids[order], Order = order });
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> DeleteAsync(int clusterId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
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
