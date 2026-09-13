using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ports;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Commands;

public sealed record ClusterSummary(int Id, string Name, string Slug, string ClusterKey);

/// <summary>One dashboard row; the live state comes from <see cref="IProcessManager"/>, not from here.</summary>
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
    BackupRecord? LastBackup);

public sealed record DashboardData(IReadOnlyList<ClusterSummary> Clusters, IReadOnlyList<InstanceSummary> Instances);

/// <summary>
/// The instance with its cluster, map, mods (with library entries), and extra overrides loaded, plus the
/// cluster's mods in cluster order (empty for a standalone instance).
/// </summary>
/// <summary><paramref name="MapMod"/> is a custom map's own mod, loaded ahead of every other and never part of the two lists.</summary>
public sealed record InstanceDetail(Instance Instance, IReadOnlyList<ModLibraryEntry> ClusterMods, IReadOnlyList<ModLibraryEntry> InstanceMods, ModLibraryEntry? MapMod = null);

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

    public IReadOnlyList<int> ModIds { get; init; } = [];

    public LaunchFlags LaunchFlags { get; init; } = new();

    public string AdminWhitelist { get; init; } = string.Empty;

    public int? BackupIntervalMinutes { get; init; }

    public int? BackupRetention { get; init; }
}

/// <summary>The editable fields on the instance settings tab (the slug and map are fixed after creation).</summary>
public sealed record InstanceEdit(
    string Name,
    string SessionName,
    int MaxPlayers,
    int GamePort,
    int RconPort,
    string AdminWhitelist,
    int? BackupIntervalMinutes,
    int? BackupRetention);

public sealed record PortSuggestion(int GamePort, int RconPort);

public sealed record BulkOutcome(int InstanceId, string Name, OperationOutcome Outcome);

/// <param name="CommandLine">The launch arguments as they would be passed, one line.</param>
/// <param name="Warnings">Reserved keys replaced in the INI source, validation notes.</param>
/// <param name="Problem">Set when the launch would be refused (missing ServerAdminPassword, invalid typed values).</param>
public sealed record LaunchPreview(string CommandLine, IReadOnlyList<string> Warnings, string? Problem);

/// <summary>
/// Scoped command facade for everything an instance page or the dashboard does. Every method first
/// awaits <see cref="Auth.IAuthorizationGuard.EnsureAuthorizedAsync"/>. Long operations (start, stop,
/// backup, delete) complete when the underlying job completes; callers run them off the render path.
/// </summary>
public interface IInstanceCommands
{
    Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<InstanceDetail?> GetAsync(int instanceId, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<BackupRecord>> GetBackupsAsync(int instanceId, CancellationToken cancellationToken = default);

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
    Task<OperationOutcome> DeleteAsync(int instanceId, bool keepWorldData, CancellationToken cancellationToken = default);

    /// <summary>Sends one RCON command from the console input and returns the reply; both are echoed into the console.</summary>
    Task<CommandResult<string>> SendRconAsync(int instanceId, string command, CancellationToken cancellationToken = default);

    Task<PortSuggestion> SuggestPortsAsync(CancellationToken cancellationToken = default);

    /// <summary>Conflicts against every other instance and the web port; <paramref name="instanceName"/> is skipped so an existing instance can re-check itself.</summary>
    Task<IReadOnlyList<PortConflict>> CheckPortsAsync(string instanceName, int gamePort, int rconPort, CancellationToken cancellationToken = default);

    /// <summary>Creates the row, the junction tree, and (standalone) the INI source files; returns the new id.</summary>
    Task<CommandResult<int>> CreateAsync(InstanceDraft draft, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveAsync(int instanceId, InstanceEdit edit, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveLaunchFlagsAsync(int instanceId, LaunchFlags flags, CancellationToken cancellationToken = default);

    Task<CommandResult> SetModsAsync(int instanceId, IReadOnlyList<int> orderedModIds, CancellationToken cancellationToken = default);

    /// <summary>Builds the command line and generates the INI in memory (nothing is written) so the owner can see what a start would do.</summary>
    Task<CommandResult<LaunchPreview>> PreviewLaunchAsync(int instanceId, CancellationToken cancellationToken = default);
}
