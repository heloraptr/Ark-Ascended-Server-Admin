namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// A group of instances sharing a cluster directory (<c>-ClusterDirOverride</c>), INI source text,
/// mandatory mods, base launch flags, and an admin whitelist.
/// </summary>
public sealed class Cluster
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Filesystem-safe, immutable after creation; names <c>Clusters\&lt;slug&gt;</c>.</summary>
    public required string Slug { get; set; }

    /// <summary>Value passed as <c>-clusterid</c>; defaults to the slug.</summary>
    public required string ClusterKey { get; set; }

    /// <summary>EOS ids for <c>AllowedCheaterAccountIDs</c>, one per line.</summary>
    public string AdminWhitelist { get; set; } = string.Empty;

    public LaunchFlags LaunchFlags { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }

    public List<Instance> Instances { get; } = [];

    public List<ClusterMod> Mods { get; } = [];

    public List<IniDocument> IniDocuments { get; } = [];

    public List<ScheduledAction> ScheduledActions { get; } = [];
}
