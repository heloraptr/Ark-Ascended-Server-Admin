using ArkAscendedServerAdmin.CurseForge.Models.Mods;

namespace ArkAscendedServerAdmin.CurseForge.Models;

/// <summary>
/// The mods a search fetched plus the number of matches CurseForge reported. The search stops at a cap, so
/// <paramref name="TotalCount"/> can be larger than <paramref name="Mods"/>; callers use the gap to say the list was cut off.
/// </summary>
public sealed record ModSearchResult(IReadOnlyList<Mod> Mods, int TotalCount);
