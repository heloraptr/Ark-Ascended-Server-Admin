using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Launch;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade for the Maps page. A custom map's mod is put in the library when the map is saved.</summary>
public sealed class MapCommands(IAuthorizationGuard guard, IDbContextFactory<AppDbContext> contextFactory, IModCommands mods) : IMapCommands
{
    public async Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var maps = await db.Maps.AsNoTracking().ToListAsync(cancellationToken);
        return maps.InDisplayOrder().ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Instances.AsNoTracking()
            .GroupBy(i => i.MapId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
    }

    public async Task<CommandResult<Map>> SaveAsync(Map map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        await guard.EnsureAuthorizedAsync(cancellationToken);

        var key = map.Key?.Trim() ?? string.Empty;
        var name = map.Name?.Trim() ?? string.Empty;
        var problems = new List<string>();
        if (key.Length == 0)
        {
            problems.Add("Map key is required; it is the name passed on the command line, e.g. TheIsland_WP.");
        }
        else if (key.Length > 100)
        {
            problems.Add("Map key must be 100 characters or fewer.");
        }
        else if (key.Any(char.IsWhiteSpace))
        {
            problems.Add("Map key must not contain spaces.");
        }
        else if (ReservedKeys.ValidateTypedValue("Map key", key) is { } keyProblem)
        {
            problems.Add(keyProblem);
        }
        else if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            // The key becomes a folder name under the instance's save directory, so anything that could
            // step out of it (separators, dots, a drive) is refused; official keys are letters, digits and _.
            problems.Add("Map key may contain only letters, digits and underscores, e.g. TheIsland_WP.");
        }

        if (name.Length == 0)
        {
            problems.Add("Map name is required.");
        }
        else if (name.Length > 100)
        {
            problems.Add("Map name must be 100 characters or fewer.");
        }

        if (map.IsStory && !map.IsOfficial)
        {
            problems.Add("Only an official map can be a story map.");
        }

        if (map.IsOfficial && map.ModId is not null)
        {
            problems.Add("An official map has no map mod; clear the mod id or untick Official.");
        }
        else if (!map.IsOfficial && map.ModId is not > 0)
        {
            problems.Add("A custom map needs the CurseForge project id of the mod that ships it.");
        }

        if (problems.Count > 0)
        {
            return CommandResult<Map>.Fail(problems);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Maps.AnyAsync(m => m.Id != map.Id && m.Key.ToLower() == key.ToLower(), cancellationToken))
        {
            return CommandResult<Map>.Fail($"A map with key '{key}' already exists.");
        }

        if (map.ModId is { } modId && !await db.ModLibrary.AnyAsync(m => m.Id == modId, cancellationToken))
        {
            // The map mod lives in the library like any other; metadata comes from CurseForge when a key is set.
            var added = await mods.IsApiKeyConfiguredAsync(cancellationToken)
                ? await mods.AddAsync(modId, cancellationToken)
                : CommandResult<ModLibraryEntry>.Fail("No API key.");
            if (!added.Succeeded)
            {
                added = await mods.AddManualAsync(modId, name, cancellationToken);
            }

            if (!added.Succeeded)
            {
                return CommandResult<Map>.Fail(added.Errors);
            }
        }

        Map row;
        if (map.Id == 0)
        {
            row = new Map { Key = key, Name = name };
            db.Maps.Add(row);
        }
        else
        {
            row = await db.Maps.SingleOrDefaultAsync(m => m.Id == map.Id, cancellationToken)
                ?? throw new InvalidOperationException("The map was deleted while you were editing it.");
            row.Key = key;
            row.Name = name;
        }

        row.IsOfficial = map.IsOfficial;
        row.IsStory = map.IsOfficial && map.IsStory;
        row.ReleaseDate = map.ReleaseDate;
        row.ModId = map.IsOfficial ? null : map.ModId;

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult<Map>.Ok(row);
    }

    public async Task<CommandResult> DeleteAsync(int mapId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var users = await db.Instances.AsNoTracking().Where(i => i.MapId == mapId).Select(i => i.Name).ToListAsync(cancellationToken);
        if (users.Count > 0)
        {
            return CommandResult.Fail($"Instances still use this map: {string.Join(", ", users)}.");
        }

        var row = await db.Maps.SingleOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (row is not null)
        {
            db.Maps.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return CommandResult.Ok;
    }
}
