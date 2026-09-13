namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// Official ASA maps seeded on first run (DESIGN.md §13). The seed is idempotent on <see cref="Map.Key"/>,
/// so adding a map here reaches existing databases at the next service start; the owner can edit or add
/// rows from the Maps page without a redeploy. Story flag and release date are re-applied on every start
/// because they are facts about the map, not owner preferences; the display name stays editable.
/// </summary>
public static class OfficialMaps
{
    public static readonly IReadOnlyList<(string Key, string Name, bool IsStory, DateOnly ReleaseDate)> All =
    [
        ("TheIsland_WP", "The Island", true, new DateOnly(2023, 10, 25)),
        ("ScorchedEarth_WP", "Scorched Earth", true, new DateOnly(2024, 4, 1)),
        ("TheCenter_WP", "The Center", false, new DateOnly(2024, 6, 4)),
        ("BobsMissions_WP", "Club ARK", false, new DateOnly(2024, 6, 17)),
        ("Aberration_WP", "Aberration", true, new DateOnly(2024, 9, 4)),
        ("Extinction_WP", "Extinction", true, new DateOnly(2024, 12, 20)),
        ("Astraeos_WP", "Astraeos", false, new DateOnly(2025, 2, 13)),
        ("Ragnarok_WP", "Ragnarok", false, new DateOnly(2025, 6, 20)),
        ("Valguero_WP", "Valguero", false, new DateOnly(2025, 10, 7)),
        ("LostColony_WP", "Lost Colony", true, new DateOnly(2025, 12, 19)),
        ("Genesis_WP", "Genesis Part 1", true, new DateOnly(2026, 7, 3)),
    ];
}
