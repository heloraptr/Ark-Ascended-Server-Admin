using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

public sealed record ModSearchHit(
    int Id,
    string Name,
    string Summary,
    string? ThumbnailUrl,
    string? Author,
    DateTimeOffset DateModified,
    long DownloadCount,
    bool InLibrary);

/// <summary>Which clusters and instances reference a library entry (a referenced entry cannot be removed).</summary>
public sealed record ModUsage(IReadOnlyList<string> Clusters, IReadOnlyList<string> Instances)
{
    public bool IsReferenced => Clusters.Count > 0 || Instances.Count > 0;
}

/// <summary>Scoped, guarded facade for the mod library page (DESIGN.md §8): CurseForge search with a key, manual ids without one.</summary>
public interface IModCommands
{
    Task<IReadOnlyList<ModLibraryEntry>> ListLibraryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, ModUsage>> GetUsageAsync(CancellationToken cancellationToken = default);

    Task<bool> IsApiKeyConfiguredAsync(CancellationToken cancellationToken = default);

    Task<CommandResult<IReadOnlyList<ModSearchHit>>> SearchAsync(string searchTerm, CancellationToken cancellationToken = default);

    /// <summary>Adds by CurseForge id, fetching name, summary, and thumbnail through the API.</summary>
    Task<CommandResult<ModLibraryEntry>> AddAsync(int modId, CancellationToken cancellationToken = default);

    /// <summary>Adds by id with an owner-typed name, for use without an API key.</summary>
    Task<CommandResult<ModLibraryEntry>> AddManualAsync(int modId, string name, CancellationToken cancellationToken = default);

    /// <summary>Re-fetches metadata for every entry (requires a key); returns how many changed.</summary>
    Task<CommandResult<int>> RefreshMetadataAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> RemoveAsync(int modId, CancellationToken cancellationToken = default);
}
