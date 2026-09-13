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