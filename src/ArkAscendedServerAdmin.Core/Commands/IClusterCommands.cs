using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <summary>One row of the Clusters page; <paramref name="ModCount"/> counts enabled cluster mods only.</summary>
public sealed record ClusterListItem(int Id, string Name, string Slug, string ClusterKey, int InstanceCount, int ModCount);

public sealed record ClusterDetail(Cluster Cluster, IReadOnlyList<ModListItem> Mods, IReadOnlyList<InstanceSummary> Instances);

public sealed record ClusterEdit(string Name, string ClusterKey, string AdminWhitelist);

/// <summary>Scoped, guarded facade for the cluster pages.</summary>
public interface IClusterCommands
{
    Task<IReadOnlyList<ClusterListItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<ClusterDetail?> GetAsync(int clusterId, CancellationToken cancellationToken = default);

    /// <summary>The unresolved restore journal that replaces the cluster directory, if any (B2).</summary>
    Task<RestoreJournal?> GetRestoreJournalAsync(int clusterId, CancellationToken cancellationToken = default);

    /// <summary>Creates the row and the cluster directory and seeds the INI source from <paramref name="source"/>; returns the new id.</summary>
    Task<CommandResult<int>> CreateAsync(string name, ConfigSourceKind source, int? sourceId, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveAsync(int clusterId, ClusterEdit edit, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveLaunchFlagsAsync(int clusterId, LaunchFlags flags, CancellationToken cancellationToken = default);

    Task<CommandResult> SetModsAsync(int clusterId, IReadOnlyList<ModSelection> orderedMods, CancellationToken cancellationToken = default);

    /// <summary>Refused while any instance belongs to the cluster. The cluster directory on disk is left in place.</summary>
    Task<CommandResult> DeleteAsync(int clusterId, CancellationToken cancellationToken = default);
}
