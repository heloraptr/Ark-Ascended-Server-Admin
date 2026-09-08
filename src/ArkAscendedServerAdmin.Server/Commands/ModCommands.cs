using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade for the mod library: CurseForge search when a key is configured, manual ids otherwise.</summary>
public sealed class ModCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settings,
    ICurseForgeApi curseForge,
    TimeProvider timeProvider,
    ILogger<ModCommands> logger) : IModCommands
{
    public const string NoApiKeyMessage = "Add a CurseForge API key on the Settings page to search. Mods can still be added by id.";

    public async Task<IReadOnlyList<ModLibraryEntry>> ListLibraryAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ModLibrary.AsNoTracking().OrderBy(m => m.Name).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, ModUsage>> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var clusterUse = await db.ClusterMods.AsNoTracking().Select(m => new { m.ModId, m.Cluster!.Name }).ToListAsync(cancellationToken);
        var instanceUse = await db.InstanceMods.AsNoTracking().Select(m => new { m.ModId, m.Instance!.Name }).ToListAsync(cancellationToken);

        var usage = new Dictionary<int, ModUsage>();
        foreach (var id in clusterUse.Select(u => u.ModId).Concat(instanceUse.Select(u => u.ModId)).Distinct())
        {
            usage[id] = new ModUsage(
                clusterUse.Where(u => u.ModId == id).Select(u => u.Name).OrderBy(n => n).ToList(),
                instanceUse.Where(u => u.ModId == id).Select(u => u.Name).OrderBy(n => n).ToList());
        }

        return usage;
    }

    public async Task<bool> IsApiKeyConfiguredAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace((await settings.GetAsync(cancellationToken)).CurseForgeApiKey);
    }

    public async Task<CommandResult<IReadOnlyList<ModSearchHit>>> SearchAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        if (!await IsApiKeyConfiguredAsync(cancellationToken))
        {
            return CommandResult<IReadOnlyList<ModSearchHit>>.Fail(NoApiKeyMessage);
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return CommandResult<IReadOnlyList<ModSearchHit>>.Ok([]);
        }

        try
        {
            var mods = await curseForge.SearchModsAsync(searchTerm.Trim(), cancellationToken: cancellationToken);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = mods.Select(m => m.Id).ToList();
            var inLibrary = await db.ModLibrary.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToHashSetAsync(cancellationToken);
            return CommandResult<IReadOnlyList<ModSearchHit>>.Ok(mods.Select(m => ToHit(m, inLibrary.Contains(m.Id))).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CurseForge search for '{Term}' failed.", searchTerm);
            return CommandResult<IReadOnlyList<ModSearchHit>>.Fail(DescribeApiFailure(ex));
        }
    }

    public async Task<CommandResult<ModLibraryEntry>> AddAsync(int modId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        if (modId <= 0)
        {
            return CommandResult<ModLibraryEntry>.Fail("A CurseForge mod id is a positive number.");
        }

        if (!await IsApiKeyConfiguredAsync(cancellationToken))
        {
            return CommandResult<ModLibraryEntry>.Fail(NoApiKeyMessage);
        }

        Mod mod;
        try
        {
            mod = await curseForge.GetModAsync(modId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CurseForge lookup of mod {ModId} failed.", modId);
            return CommandResult<ModLibraryEntry>.Fail(DescribeApiFailure(ex));
        }

        return await UpsertAsync(mod.Id, mod.Name, mod.Summary, NullIfEmpty(mod.Logo.ThumbnailUrl), mod.DateModified, cancellationToken);
    }

    public async Task<CommandResult<ModLibraryEntry>> AddManualAsync(int modId, string name, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        var problems = new List<string>();
        if (modId <= 0)
        {
            problems.Add("A CurseForge mod id is a positive number.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("Give the mod a name so it can be recognized in the lists.");
        }

        return problems.Count > 0
            ? CommandResult<ModLibraryEntry>.Fail(problems)
            : await UpsertAsync(modId, name.Trim(), null, null, null, cancellationToken);
    }

    public async Task<CommandResult<int>> RefreshMetadataAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        if (!await IsApiKeyConfiguredAsync(cancellationToken))
        {
            return CommandResult<int>.Fail(NoApiKeyMessage);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.ModLibrary.ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return CommandResult<int>.Ok(0);
        }

        List<Mod> mods;
        try
        {
            mods = await curseForge.GetModsAsync(entries.Select(e => e.Id), pcOnly: false, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CurseForge metadata refresh failed.");
            return CommandResult<int>.Fail(DescribeApiFailure(ex));
        }

        var changed = 0;
        foreach (var mod in mods)
        {
            var entry = entries.SingleOrDefault(e => e.Id == mod.Id);
            if (entry is null)
            {
                continue;
            }

            var modified = new DateTimeOffset(DateTime.SpecifyKind(mod.DateModified, DateTimeKind.Utc));
            if (entry.Name != mod.Name || entry.Summary != mod.Summary || entry.ThumbnailUrl != NullIfEmpty(mod.Logo.ThumbnailUrl) || entry.DateModified != modified)
            {
                entry.Name = mod.Name;
                entry.Summary = mod.Summary;
                entry.ThumbnailUrl = NullIfEmpty(mod.Logo.ThumbnailUrl);
                entry.DateModified = modified;
                changed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult<int>.Ok(changed);
    }

    public async Task<CommandResult> RemoveAsync(int modId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        var usage = (await GetUsageAsync(cancellationToken)).GetValueOrDefault(modId);
        if (usage is { IsReferenced: true })
        {
            var users = usage.Clusters.Select(c => $"cluster {c}").Concat(usage.Instances.Select(i => $"instance {i}"));
            return CommandResult.Fail($"Remove it from {string.Join(", ", users)} first.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ModLibrary.SingleOrDefaultAsync(m => m.Id == modId, cancellationToken);
        if (entry is not null)
        {
            db.ModLibrary.Remove(entry);
            await db.SaveChangesAsync(cancellationToken);
        }

        return CommandResult.Ok;
    }

    private async Task<CommandResult<ModLibraryEntry>> UpsertAsync(int id, string name, string? summary, string? thumbnailUrl, DateTime? dateModified, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ModLibrary.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (entry is null)
        {
            entry = new ModLibraryEntry { Id = id, Name = name, AddedAt = timeProvider.GetUtcNow() };
            db.ModLibrary.Add(entry);
        }

        entry.Name = name;
        entry.Summary = summary ?? entry.Summary;
        entry.ThumbnailUrl = thumbnailUrl ?? entry.ThumbnailUrl;
        if (dateModified is { } modified)
        {
            entry.DateModified = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult<ModLibraryEntry>.Ok(entry);
    }

    private static ModSearchHit ToHit(Mod mod, bool inLibrary) =>
        new(
            mod.Id,
            mod.Name,
            mod.Summary,
            NullIfEmpty(mod.Logo.ThumbnailUrl),
            mod.Authors.FirstOrDefault()?.Name,
            new DateTimeOffset(DateTime.SpecifyKind(mod.DateModified, DateTimeKind.Utc)),
            mod.DownloadCount,
            inLibrary);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DescribeApiFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
            ? "CurseForge rejected the API key. Check it on the Settings page."
            : $"CurseForge could not be reached: {ex.Message}";
}
