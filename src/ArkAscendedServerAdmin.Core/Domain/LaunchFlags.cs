namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// Typed <c>-Flag</c> command-line options (DESIGN.md §6). Used as the cluster base and as the instance
/// override: a <see langword="null"/> means "inherit from the cluster" on an instance and "off / unset"
/// on a cluster or standalone instance. Phase 3's launch-argument builder resolves the two layers
/// (<see cref="Launch.LaunchFlagResolver"/>) and emits the flags (<see cref="Launch.LaunchArgumentBuilder"/>).
/// </summary>
public sealed class LaunchFlags
{
    /// <summary>
    /// BattlEye is off by default (plan step 17): <see langword="null"/> and <see langword="true"/> both
    /// emit <c>-NoBattlEye</c>; only <see langword="false"/> enables BattlEye by omitting the flag.
    /// </summary>
    public bool? NoBattlEye { get; set; }

    /// <summary>Crossplay platform list, e.g. <c>ALL</c>, <c>PC</c>, <c>PC+XSX</c>.</summary>
    public string? ServerPlatform { get; set; }

    public bool? ExclusiveJoin { get; set; }

    public bool? NoWildBabies { get; set; }

    public bool? PreventSpawnAnimations { get; set; }

    public bool? UseStore { get; set; }

    public bool? ConvertToStore { get; set; }

    /// <summary>
    /// The manager defaults the server game log on (plan step 17) because the log tail is the console's
    /// output source: <see langword="null"/> and <see langword="true"/> both emit <c>-servergamelog</c>;
    /// only <see langword="false"/> omits it.
    /// </summary>
    public bool? ServerGameLog { get; set; }

    public bool? ServerGameLogIncludeTribeLogs { get; set; }

    public bool? ServerRconOutputTribeLogs { get; set; }

    public string? ActiveEvent { get; set; }

    /// <summary>
    /// Free-text additional arguments; tokenized and validated against the reserved-key list by
    /// <see cref="Launch.AdditionalArgs"/>. In the resolved flags this is the cluster text followed by the
    /// instance text.
    /// </summary>
    public string? AdditionalArgs { get; set; }
}
