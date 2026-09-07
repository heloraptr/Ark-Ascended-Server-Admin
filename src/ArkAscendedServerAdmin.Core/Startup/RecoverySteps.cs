namespace ArkAscendedServerAdmin.Startup;

/// <summary>
/// Startup step: for every instance, find the surviving <c>ArkAscendedServer.exe</c> (PID + start time,
/// else exact <c>AltSaveDirectoryName</c> token) and attach or mark it Stopped / Unknown (plan step 21).
/// Phase 4 supplies the WMI implementation; Phase 1 registers a no-op.
/// </summary>
public interface IProcessReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Startup step: wait for (then bound and kill) any <c>steamcmd.exe</c> still running from
/// <c>DataRoot\SteamCMD</c> so a resumed install never runs two SteamCMDs against one install.
/// </summary>
public interface ISteamCmdReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Startup step: resume the persisted <c>MaintenanceState</c> by phase (re-run the stop sweep, re-run
/// SteamCMD, or hand pending restarts to the launch queue). Returns without waiting for restarts to finish.
/// </summary>
public interface IMaintenanceRecovery
{
    Task ResumeAsync(CancellationToken cancellationToken);
}
