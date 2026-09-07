namespace ArkAscendedServerAdmin.Domain;

/// <summary>A launchable map. Official ASA maps are seeded on first run; rows stay editable.</summary>
public sealed class Map
{
    public int Id { get; set; }

    /// <summary>The map name passed on the command line, e.g. <c>TheIsland_WP</c>.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public bool IsOfficial { get; set; }
}
