namespace ArkAscendedServerAdmin.Domain;

/// <summary>Persisted lifecycle state of an instance (plan steps 19, 21, 22).</summary>
public enum InstanceState
{
    Stopped,
    Starting,
    /// <summary>Alive longer than the startup bound without a successful RCON probe.</summary>
    StartingUnconfirmed,
    Running,
    /// <summary>Alive, but RCON keeps failing (check ServerAdminPassword / RCONPort).</summary>
    Unreachable,
    Stopping,
    /// <summary>More than one process matched at reconciliation; no automatic action is taken.</summary>
    Unknown,
    /// <summary>Process started but PID / start time could not be persisted after bounded retries.</summary>
    IdentityUnpersisted,
}

/// <summary>Which of the two ASA configuration files a document or override targets.</summary>
public enum IniFile
{
    Game,
    GameUserSettings,
}

public enum MaintenancePhase
{
    None,
    Installing,
    Stopping,
    Updating,
    Restarting,
}

public enum BackupOutcome
{
    Success,
    Skipped,
    Failed,
}

/// <summary>Outcome of one restore attempt (B2); <see cref="RolledBack"/> means the previous files are back in place.</summary>
public enum RestoreOutcome
{
    Success,
    Failed,
    RolledBack,
}

/// <summary>What a <see cref="ScheduledAction"/> does when its deadline arrives (B3).</summary>
public enum ScheduledActionKind
{
    /// <summary>A countdown to the deadline, then a stop and relaunch.</summary>
    Restart,
    /// <summary>One RCON command sent at the deadline; it has no warning countdown.</summary>
    RconCommand,
    /// <summary>A countdown to the deadline, then <c>DestroyWildDinos</c>.</summary>
    DinoWipe,
}

/// <summary>
/// Outcome of one <see cref="ScheduledActionRun"/>. <see cref="Started"/> is the claim written when the
/// action fires; the runner turns it into one of the others when the operation returns, and marks any row
/// still <see cref="Started"/> at service start as <see cref="Interrupted"/>.
/// </summary>
public enum ScheduledActionOutcome
{
    Started,
    Succeeded,
    Failed,
    /// <summary>The action did not run that day; <see cref="ScheduledActionRun.Reason"/> says why.</summary>
    Skipped,
    /// <summary>The service stopped while the action was in flight.</summary>
    Interrupted,
}
