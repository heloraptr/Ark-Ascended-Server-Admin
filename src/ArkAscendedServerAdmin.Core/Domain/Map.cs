namespace ArkAscendedServerAdmin.Domain;

/// <summary>A launchable map. Official ASA maps are seeded on first run; rows stay editable.</summary>
public sealed class Map
{
    public int Id { get; set; }

    /// <summary>The map name passed on the command line, e.g. <c>TheIsland_WP</c>.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public bool IsOfficial { get; set; }

    /// <summary>An official map that carries the story line (The Island, Scorched Earth, ...). Only valid with <see cref="IsOfficial"/>.</summary>
    public bool IsStory { get; set; }

    /// <summary>The ASA release date; every official map has one, custom maps may not.</summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>
    /// The CurseForge project id of the mod that ships a custom map. Required for a custom map, absent for an
    /// official one. Every instance on the map loads it first, ahead of cluster and instance mods; it is never
    /// listed in those lists. Editing it is how a map moves to a re-released mod with a new id.
    /// </summary>
    public int? ModId { get; set; }

    /// <summary>"Official - Story", "Official - Non-Canon", or "Custom/Mod".</summary>
    public string TypeLabel => this switch
    {
        { IsOfficial: true, IsStory: true } => "Official - Story",
        { IsOfficial: true } => "Official - Non-Canon",
        _ => "Custom/Mod",
    };
}

/// <summary>The one order every map list uses: official story, official non-canon, custom; then release date with unknown dates last; then name.</summary>
public static class MapOrdering
{
    public static IEnumerable<Map> InDisplayOrder(this IEnumerable<Map> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        return maps
            .OrderByDescending(m => m.IsOfficial)
            .ThenByDescending(m => m.IsOfficial && m.IsStory)
            .ThenBy(m => m.ReleaseDate is null)
            .ThenBy(m => m.ReleaseDate)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }
}
