using ArkAscendedServerAdmin.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>
/// A custom map's own mod is added by choosing the map and loaded first; it is never part of a cluster or
/// instance mod list, so every list write refuses it here.
/// </summary>
internal static class MapModGuard
{
    public static async Task<string?> FindProblemAsync(AppDbContext db, IReadOnlyList<int> modIds, CancellationToken cancellationToken)
    {
        if (modIds.Count == 0)
        {
            return null;
        }

        var clashes = await db.Maps.AsNoTracking()
            .Where(m => m.ModId != null && modIds.Contains(m.ModId.Value))
            .OrderBy(m => m.Name)
            .Select(m => new { m.Name, ModId = m.ModId!.Value })
            .ToListAsync(cancellationToken);
        return clashes.Count == 0
            ? null
            : $"Map mods load automatically with their map and cannot be listed here: {string.Join(", ", clashes.Select(c => $"{c.ModId} ({c.Name})"))}.";
    }
}
