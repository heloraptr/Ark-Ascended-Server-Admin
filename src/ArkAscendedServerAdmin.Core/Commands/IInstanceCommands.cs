using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Ports;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Commands;

public sealed record ClusterSummary(int Id, string Name, string Slug, string ClusterKey);

/// <summary>
/// One dashboard row; the live state comes from <see cref="IProcessManager"/>, not from here.
/// <paramref name="NextDeadline"/> is the next scheduled action across the rows that apply to the instance
/// (B3), null when nothing is scheduled. <paramref name="ModsChangedSinceLaunch"/> is the mod badge (B8):
/// a mod the instance loads was modified after the manager last launched it.
/// </summary>
public sealed record InstanceSummary(
    int Id,
    string Name,
    string Slug,
    int? ClusterId,
    string MapKey,
    string MapName,
    string SessionName,
    int GamePort,
    int RconPort,
    int MaxPlayers,
    int ModCount,
    BackupRecord? LastBackup,
    DateTimeOffset? NextDeadline,
    bool ModsChangedSinceLaunch = false);

public sealed record DashboardData(IReadOnlyList<ClusterSummary> Clusters, IReadOnlyList<InstanceSummary> Instances);

/// <summary>
/// The instance with its cluster, map, mods (with library entries), and extra overrides loaded, plus the
/// cluster's mods in cluster order (empty for a standalone instance). <paramref name="MapMod"/> is a custom
/// map's own mod, loaded ahead of every other and never part of the two lists.
/// <paramref name="ModsChangedSinceLaunch"/> carries the mod badge for the instance header (B8).
/// </summary>
public sealed record InstanceDetail(
    Instance Instance,
    IReadOnlyList<ModListItem> ClusterMods,
    IReadOnlyList<ModListItem> InstanceMods,
    ModLibraryEntry? MapMod = null,
    bool ModsChangedSinceLaunch = false);

/// <summary>Where a new standalone instance's or cluster's INI source text starts from.</summary>
public enum ConfigSourceKind
{
    GameDefaults,
    Blank,
    CopyFromInstance,
    CopyFromCluster,
}

/// <summary>Everything the instance wizard collects (plan step 31).</summary>
public sealed record InstanceDraft
{
    public string Name { get; init; } = string.Empty;

    public int? ClusterId { get; init; }

    public int MapId { get; init; }

    public string SessionName { get; init; } = string.Empty;

    public int MaxPlayers { get; init; } = 70;

    public int GamePort { get; init; }

    public int RconPort { get; init; }

    /// <summary>Ignored for a clustered instance: the cluster owns the INI source.</summary>
    public ConfigSourceKind ConfigSource { get; init; } = ConfigSourceKind.GameDefaults;

    /// <summary>The instance or cluster id to copy from when <see cref="ConfigSource"/> is a copy.</summary>
    public int? ConfigSourceId { get; init; }

    /// <summary>
    /// Written as <c>ServerAdminPassword</c> under <c>[ServerSettings]</c> into the seeded
    /// <c>GameUserSettings.ini</c> of a standalone instance so the first start can succeed. Blank keeps
    /// whatever the chosen source has; ignored for a clustered instance (the cluster's INI owns it).
    /// </summary>
    public string AdminPassword { get; init; } = string.Empty;

    public IReadOnlyList<ModSelection> Mods { get; init; } = [];

    public LaunchFlags LaunchFlags { get; init; } = new();

    public string AdminWhitelist { get; init; } = string.Empty;

    public int? BackupIntervalMinutes { get; init; }

    public int? BackupRetention { get; init; }
}

/// <summary>
/// The editable fields on the instance settings tab (the slug and map are fixed after creation).
/// <paramref name="OverridesClusterSchedule"/> makes the instance ignore its cluster's scheduled actions (B3).
/// <paramref name="AutoRestart"/> relaunches the server after an unexpected exit (B4).
/// </summary>
public sealed record InstanceEdit(
    string Name,
    string SessionName,
    int MaxPlayers,
    int GamePort,
    int RconPort,
    string AdminWhitelist,
    int? BackupIntervalMinutes,
    int? BackupRetention,
    bool OverridesClusterSchedule,
    bool AutoRestart);

public sealed record PortSuggestion(int GamePort, int RconPort);

public sealed record BulkOutcome(int InstanceId, string Name, OperationOutcome Outcome);

/// <param name="CommandLine">The launch arguments as they would be passed, one line.</param>
/// <param name="Warnings">Reserved keys replaced in the INI source, validation notes.</param>
/// <param name="Problem">Set when the launch would be refused (missing ServerAdminPassword, invalid typed values).</param>
public sealed record LaunchPreview(string CommandLine, IReadOnlyList<string> Warnings, string? Problem);

/// <summary>
/// What the instance page's Connection card shows (B9): the addresses and ports players connect to, and whether
/// the instance's firewall rule is in place. Built from the box's own network interfaces and the stored
/// settings; nothing here contacts the network.
/// </summary>
/// <param name="GamePort">The UDP game port players connect to.</param>
/// <param name="RconPort">The TCP RCON port; shown for reference, never part of an <c>open</c> string.</param>
/// <param name="LanAddresses">The box's non-loopback IPv4 addresses on interfaces that are up, in interface order; may be empty.</param>
/// <param name="PublicAddress"><see cref="Configuration.AppSettings.PublicAddress"/>, trimmed; empty when not set.</param>
/// <param name="FirewallRuleName">The name the app gives the instance's inbound rules, <c>ArkAscendedServerAdmin-&lt;tag&gt;-&lt;id&gt;</c>.</param>
/// <param name="FirewallRuleExists">True or false when the firewall answered; null when it could not be read.</param>
public sealed record ConnectionView(
    int GamePort,
    int RconPort,
    IReadOnlyList<string> LanAddresses,
    string PublicAddress,
    string FirewallRuleName,
    bool? FirewallRuleExists)
{
    /// <summary>Whether a public address is set, so the card has an extra <c>open</c> string to offer.</summary>
    public bool HasPublicAddress => PublicAddress.Length > 0;

    /// <summary>The console command a player types to join through <paramref name="address"/>.</summary>
    public string OpenCommand(string address) => $"open {address}:{GamePort}";
}

/// <summary>
/// Scoped command facade for everything an instance page or the dashboard does. Every method first
/// awaits <see cref="Auth.IAuthorizationGuard.EnsureAuthorizedAsync"/>. Long operations (start, stop,
/// backup, delete) complete when the underlying job completes; callers run them off the render path.
/// </summary>
public interface IInstanceCommands
{
    Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<InstanceDetail?> GetAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Connection card's data: the box's addresses, the ports, the public address setting, and the firewall
    /// rule check. Null when the instance does not exist. Reads the network interfaces on every call, so the page
    /// asks once per load.
    /// </summary>
    Task<ConnectionView?> GetConnectionAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<BackupRecord>> GetBackupsAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Restore attempts and recoveries, newest first (B2).</summary>
    Task<IReadOnlyList<RestoreRecord>> GetRestoresAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>The unresolved restore journal that references the instance, if any (B2).</summary>
    Task<RestoreJournal?> GetRestoreJournalAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>What the restore dialog shows before it asks (B2).</summary>
    Task<RestoreInspection> InspectRestoreAsync(int instanceId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Runs the restore to completion (B2); the dialog is finished and the instance stopped before this is called.</summary>
    Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken = default);

    /// <summary>Copies the safety copy of an interrupted restore back and removes its journal (B2).</summary>
    Task<OperationOutcome> RecoverRestoreAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>Removes an interrupted restore's journal and leaves the files as they are (B2).</summary>
    Task<OperationOutcome> DiscardRestoreJournalAsync(string operationId, CancellationToken cancellationToken = default);

    Task<OperationOutcome> StartAsync(int instanceId, CancellationToken cancellationToken = default);

    Task<OperationOutcome> StopAsync(int instanceId, bool skipCountdown, CancellationToken cancellationToken = default);

    Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>"Stop now" on a stop that is counting down; false when no countdown is in progress.</summary>
    Task<bool> SkipCountdownAsync(int instanceId, CancellationToken cancellationToken = default);

    Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkOutcome>> StartManyAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkOutcome>> StopManyAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken = default);

    Task<CommandResult<BackupRecord>> BackupNowAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Runs the delete job to completion (plan step 30); the dialog is finished before this is called.</summary>
    Task<OperationOutcome> DeleteAsync(int instanceId, InstanceDeleteOptions options, CancellationToken cancellationToken = default);

    /// <summary>Sends one RCON command from the console input and returns the reply; both are echoed into the console.</summary>
    Task<CommandResult<string>> SendRconAsync(int instanceId, string command, CancellationToken cancellationToken = default);

    Task<PortSuggestion> SuggestPortsAsync(CancellationToken cancellationToken = default);

    /// <summary>Conflicts against every other instance and the web port; <paramref name="instanceName"/> is skipped so an existing instance can re-check itself.</summary>
    Task<IReadOnlyList<PortConflict>> CheckPortsAsync(string instanceName, int gamePort, int rconPort, CancellationToken cancellationToken = default);

    /// <summary>Creates the row, the junction tree, and (standalone) the INI source files; returns the new id.</summary>
    Task<CommandResult<int>> CreateAsync(InstanceDraft draft, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveAsync(int instanceId, InstanceEdit edit, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveLaunchFlagsAsync(int instanceId, LaunchFlags flags, CancellationToken cancellationToken = default);

    Task<CommandResult> SetModsAsync(int instanceId, IReadOnlyList<ModSelection> orderedMods, CancellationToken cancellationToken = default);

    /// <summary>Builds the command line and generates the INI in memory (nothing is written) so the owner can see what a start would do.</summary>
    Task<CommandResult<LaunchPreview>> PreviewLaunchAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>The instance's own schedule rows plus, unless it overrides the cluster schedule, its cluster's rows flagged <see cref="ScheduledActionView.Inherited"/> (B3).</summary>
    Task<CommandResult<IReadOnlyList<ScheduledActionView>>> ListScheduledActionsAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Whole-list save of the instance's own rows (B3): a known id updates in place, id zero inserts, rows left out are deleted; inherited rows are refused.</summary>
    Task<CommandResult> SaveScheduledActionsAsync(int instanceId, IReadOnlyList<ScheduledActionEdit> rows, CancellationToken cancellationToken = default);

    /// <summary>The instance's scheduled action runs, newest first (B3).</summary>
    Task<CommandResult<IReadOnlyList<ScheduledActionRunView>>> ListScheduledActionRunsAsync(int instanceId, int take = 10, CancellationToken cancellationToken = default);
}
