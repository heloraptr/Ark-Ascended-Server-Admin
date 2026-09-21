using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.CurseForge.Models.Mods;
using ArkAscendedServerAdmin.CurseForge.Models.Services;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Mods;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Mods;

/// <summary>
/// The CurseForge metadata refresh (B8), lifted out of the mod command facade so the daily poll can run it
/// without an interactive user. Every library entry is looked up in one call; only rows whose fields
/// really differ are written, and the count of those rows is what the Mods page reports.
/// </summary>
public sealed class ModMetadataRefresher(
    IDbContextFactory<AppDbContext> contextFactory,
    IAppSettingsStore settings,
    ICurseForgeApi curseForge,
    ILogger<ModMetadataRefresher> logger) : IModMetadataRefresher
{
    public async Task<CommandResult<int>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace((await settings.GetAsync(cancellationToken)).CurseForgeApiKey))
        {
            return CommandResult<int>.Fail(ModMetadata.NoApiKeyMessage);
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
            return CommandResult<int>.Fail(ModMetadata.DescribeApiFailure(ex));
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
