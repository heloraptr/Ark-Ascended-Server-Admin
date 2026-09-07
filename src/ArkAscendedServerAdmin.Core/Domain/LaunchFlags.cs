namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// Typed <c>-Flag</c> command-line options (DESIGN.md §6). Used as the cluster base and as the instance
/// override: a <see langword="null"/> means "inherit from the cluster" on an instance and "off / unset"
/// on a cluster or standalone instance. Phase 3's launch-argument builder resolves the two layers.
/// </summary>
public sealed class LaunchFlags
{
    public bool? NoBattlEye { get; set; }

    /// <summary>Crossplay platform list, e.g. <c>ALL</c>, <c>PC</c>, <c>PC+XSX</c>.</summary>
    public string? ServerPlatform { get; set; }

    public bool? ExclusiveJoin { get; set; }

    public bool? NoWildBabies { get; set; }

    public bool? PreventSpawnAnimations { get; set; }

    public bool? UseStore { get; set; }

    public bool? ConvertToStore { get; set; }

    public bool? ServerGameLog { get; set; }

    public bool? ServerGameLogIncludeTribeLogs { get; set; }

    public bool? ServerRconOutputTribeLogs { get; set; }

    public string? ActiveEvent { get; set; }

    /// <summary>Free-text additional arguments; tokenized and validated against the reserved-key list.</summary>
    public string? AdditionalArgs { get; set; }
}
