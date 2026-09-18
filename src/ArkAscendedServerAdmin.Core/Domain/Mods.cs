namespace ArkAscendedServerAdmin.Domain;

/// <summary>A CurseForge mod the owner has added to the global library (DESIGN.md §8).</summary>
public sealed class ModLibraryEntry
{
    /// <summary>The CurseForge project id, which is also what <c>-mods</c> takes.</summary>
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Summary { get; set; }

    public string? ThumbnailUrl { get; set; }

    /// <summary>CurseForge <c>dateModified</c>; drives the "updated since last start" hint.</summary>
    public DateTimeOffset? DateModified { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Ordered cluster-wide mod assignment; mandatory for every member instance.</summary>
public sealed class ClusterMod
{
    public int ClusterId { get; set; }

    public Cluster? Cluster { get; set; }

    public int ModId { get; set; }

    /// <summary>False keeps the row in the list but leaves the mod out of <c>-mods</c> (issue #24).</summary>
    public bool Enabled { get; set; } = true;
    public ModLibraryEntry? Mod { get; set; }

    public int Order { get; set; }
}

/// <summary>Ordered instance-specific mod assignment, emitted after the cluster mods.</summary>
public sealed class InstanceMod
{
    public int InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public int ModId { get; set; }

    /// <summary>False keeps the row in the list but leaves the mod out of <c>-mods</c> (issue #24).</summary>
    public bool Enabled { get; set; } = true;
    public ModLibraryEntry? Mod { get; set; }

    public int Order { get; set; }
}
