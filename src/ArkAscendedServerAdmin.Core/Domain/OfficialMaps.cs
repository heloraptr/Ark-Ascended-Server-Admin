namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// Official ASA maps seeded on first run (DESIGN.md §13). The seed is idempotent on <see cref="Map.Key"/>,
/// so adding a map here reaches existing databases at the next service start; the owner can edit or add
/// rows from the Maps page without a redeploy.
/// </summary>
public static class OfficialMaps
{
    public static readonly IReadOnlyList<(string Key, string Name)> All =
    [
        ("TheIsland_WP", "The Island"),
        ("ScorchedEarth_WP", "Scorched Earth"),
        ("TheCenter_WP", "The Center"),
        ("Aberration_WP", "Aberration"),
        ("Extinction_WP", "Extinction"),
        ("Astraeos_WP", "Astraeos"),
        ("Ragnarok_WP", "Ragnarok"),
        ("Valguero_WP", "Valguero"),
        ("LostColony_WP", "Lost Colony"),
        ("Genesis_WP", "Genesis Part 1"),
        ("BobsMissions_WP", "Club ARK")
    ];
}
