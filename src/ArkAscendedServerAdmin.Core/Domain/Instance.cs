namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// One dedicated-server process definition. Clustered when <see cref="ClusterId"/> is set, standalone
/// otherwise (a standalone instance owns its INI source text and launch flags directly).
/// </summary>
public sealed class Instance
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Filesystem-safe, immutable, unique across live instances and <c>Archive\</c> entries. Names
    /// <c>Instances\&lt;slug&gt;</c> and is passed as <c>AltSaveDirectoryName</c> (the re-attach key).
    /// </summary>
    public required string Slug { get; set; }

    public int? ClusterId { get; set; }

    public Cluster? Cluster { get; set; }

    public int MapId { get; set; }

    public Map? Map { get; set; }

    public required string SessionName { get; set; }

    public int GamePort { get; set; }

    public int RconPort { get; set; }

    public int MaxPlayers { get; set; } = 70;

    /// <summary>Instance-level EOS ids, unioned with the cluster's list at generation time.</summary>
    public string AdminWhitelist { get; set; } = string.Empty;

    public LaunchFlags LaunchFlags { get; set; } = new();

    /// <summary><see langword="null"/> uses the App Setting default.</summary>
    public int? BackupIntervalMinutes { get; set; }

    /// <summary><see langword="null"/> uses the App Setting default.</summary>
    public int? BackupRetention { get; set; }

    /// <summary>
    /// True ignores the cluster's <see cref="ScheduledAction"/> rows entirely, so only the instance's own
    /// rows apply (B3). Mirrors the Default/Inherit idea of launch flags at the list level rather than per row.
    /// </summary>
    public bool OverridesClusterSchedule { get; set; }

    /// <summary>
    /// True lets the manager relaunch the game server after it exits without a manager-initiated stop (B4). A crash
    /// loop ends in <see cref="InstanceState.Crashed"/>, which a manual Start clears. Off by default.
    /// </summary>
    public bool AutoRestart { get; set; }

    public int? LastPid { get; set; }

    /// <summary>When the manager issued the launch (wall clock).</summary>
    public DateTimeOffset? LastLaunchedAt { get; set; }

    /// <summary>The actual <c>Process.StartTime</c>, used with <see cref="LastPid"/> for identity on re-attach.</summary>
    public DateTimeOffset? LastProcessStartTime { get; set; }

    public InstanceState State { get; set; } = InstanceState.Stopped;

    public DateTimeOffset CreatedAt { get; set; }

    public List<InstanceMod> Mods { get; } = [];

    public List<IniDocument> IniDocuments { get; } = [];

    public List<ExtraOverride> ExtraOverrides { get; } = [];

    public List<BackupRecord> Backups { get; } = [];

    public List<RestoreRecord> Restores { get; } = [];

    public List<ScheduledAction> ScheduledActions { get; } = [];
}
