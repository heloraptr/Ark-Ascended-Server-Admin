using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>
/// One CurseForge search result. <paramref name="WebsiteUrl"/> is the mod's CurseForge page, null unless it passed
/// <see cref="CurseForge.CurseForgeLinks.SafeWebsiteUrl"/>.
/// </summary>
public sealed record ModSearchHit(
    int Id,
    string Name,
    string Summary,
    string? ThumbnailUrl,
    string? Author,
    DateTimeOffset DateModified,
    long DownloadCount,
    bool InLibrary,
    string? WebsiteUrl);

/// <summary>
/// The hits for one search and how many mods CurseForge said matched. Every fetched mod becomes a hit, so a
/// total above the hit count means the search stopped at its cap rather than running out of matches.
/// </summary>
public sealed record ModSearchHits(IReadOnlyList<ModSearchHit> Hits, int TotalCount)
{
    public static ModSearchHits None { get; } = new([], 0);

    public bool IsTruncated => TotalCount > Hits.Count;
}

/// <summary>
/// One library row as the Mods page reads it: the entry plus the mod badge (B8), which is set when the mod
/// was modified after the last launch of at least one instance that loads it.
/// </summary>
public sealed record ModLibraryView(ModLibraryEntry Mod, bool ChangedSinceLaunch);

/// <summary>Which clusters, instances, and custom maps reference a library entry (a referenced entry cannot be removed).</summary>
public sealed record ModUsage(IReadOnlyList<string> Clusters, IReadOnlyList<string> Instances, IReadOnlyList<string> Maps)
{
    public bool IsReferenced => Clusters.Count > 0 || Instances.Count > 0 || Maps.Count > 0;
}

/// <summary>Scoped, guarded facade for the mod library page (DESIGN.md §8): CurseForge search with a key, manual ids without one.</summary>
public interface IModCommands
{
    /// <summary>The library in name order, each entry with its "changed since the last launch" flag (B8).</summary>
    Task<IReadOnlyList<ModLibraryView>> ListLibraryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, ModUsage>> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>Mod id → map name for every custom map's own mod. These are added by choosing the map, never from the mod lists.</summary>
    Task<IReadOnlyDictionary<int, string>> GetMapModsAsync(CancellationToken cancellationToken = default);

    Task<bool> IsApiKeyConfiguredAsync(CancellationToken cancellationToken = default);

    Task<CommandResult<ModSearchHits>> SearchAsync(string searchTerm, CancellationToken cancellationToken = default);

    /// <summary>Adds by CurseForge id, fetching name, summary, and thumbnail through the API.</summary>
    Task<CommandResult<ModLibraryEntry>> AddAsync(int modId, CancellationToken cancellationToken = default);

    /// <summary>Adds by id with an owner-typed name, for use without an API key.</summary>
    Task<CommandResult<ModLibraryEntry>> AddManualAsync(int modId, string name, CancellationToken cancellationToken = default);

    /// <summary>Re-fetches metadata for every entry through <see cref="Mods.IModMetadataRefresher"/> (requires a key); returns how many changed.</summary>
    Task<CommandResult<int>> RefreshMetadataAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> RemoveAsync(int modId, CancellationToken cancellationToken = default);
}
