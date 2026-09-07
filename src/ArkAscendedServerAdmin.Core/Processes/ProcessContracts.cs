using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>One <c>ArkAscendedServer.exe</c> as reported by WMI <c>Win32_Process</c> (plan step 21).</summary>
/// <param name="Pid">The process id.</param>
/// <param name="ExecutablePath">The junction path for managed instances (Spike B); null when WMI withholds it.</param>
/// <param name="CommandLine">The full command line; its map string carries the <c>AltSaveDirectoryName</c> token.</param>
/// <param name="CreationTime">Equals <c>Process.StartTime</c> to the millisecond.</param>
public sealed record GameProcessInfo(int Pid, string? ExecutablePath, string? CommandLine, DateTimeOffset CreationTime);

/// <summary>Enumerates every running <c>ArkAscendedServer.exe</c> on the box, managed or not.</summary>
public interface IGameProcessEnumerator
{
    IReadOnlyList<GameProcessInfo> Enumerate();
}

/// <summary>Result of a management request; a rejection carries the reason the UI shows verbatim.</summary>
public sealed record OperationOutcome(bool Succeeded, string? Error = null)
{
    public static readonly OperationOutcome Success = new(true);

    public static OperationOutcome Rejected(string reason) => new(false, reason);
}

/// <param name="SkipCountdown">Skip the pre-stop broadcast countdown entirely.</param>
/// <param name="RequireVerifiedExit">Update and Delete need <c>HasExited</c> to be observed; a stop that had to kill still verifies.</param>
public sealed record StopOptions(bool SkipCountdown = false, bool RequireVerifiedExit = false);

public enum LaunchKind
{
    /// <summary>Owner-initiated; refused until the readiness pipeline is <c>Ready</c>.</summary>
    User,
    /// <summary>Update-recovery launch (plan step 11); allowed before <c>Ready</c> once the install is verified and the gate is free.</summary>
    Recovery,
}

/// <summary>
/// Live view of one instance's process, owned by the process manager and mirrored into
/// <see cref="Instance.State"/>. <see cref="Detail"/> carries the human-readable reason for
/// <see cref="InstanceState.Unreachable"/>, <see cref="InstanceState.Unknown"/>, and
/// <see cref="InstanceState.IdentityUnpersisted"/>.
/// </summary>
public sealed record InstanceRuntime(
    int InstanceId,
    InstanceState State,
    int? Pid,
    DateTimeOffset? ProcessStartTime,
    StartupMarker? LastMarker,
    DateTimeOffset? LastRconSuccessAt,
    string? Detail)
{
    /// <summary>True for every state that has (or may have) a live process behind it.</summary>
    public bool HasLiveProcess => State is InstanceState.Starting
        or InstanceState.StartingUnconfirmed
        or InstanceState.Running
        or InstanceState.Unreachable
        or InstanceState.Stopping
        or InstanceState.IdentityUnpersisted;
}

/// <summary>
/// The process manager (plan steps 19, 21–25): the only component that launches, attaches to, probes,
/// stops, and kills game servers. All methods are safe to call from any thread and never block on a
/// Blazor circuit. Start goes through the global launch queue (stagger delay) and takes the maintenance
/// gate shared; Stop runs as a background job under the instance lock.
/// </summary>
public interface IProcessManager
{
    InstanceRuntime GetRuntime(int instanceId);

    IReadOnlyList<InstanceRuntime> GetAllRuntimes();

    /// <summary>Raised on a background thread whenever an instance's runtime changes.</summary>
    event Action<InstanceRuntime>? RuntimeChanged;

    /// <summary>
    /// Enqueues a launch and completes when the process has started and its identity is persisted, or
    /// with the rejection reason (not Ready, update in progress, operation in progress, port conflict,
    /// missing ServerAdminPassword, drained queue, Process.Start failure).
    /// </summary>
    Task<OperationOutcome> StartAsync(int instanceId, LaunchKind kind, CancellationToken cancellationToken);

    /// <summary>Runs the stop sequence (plan step 24) and completes when the process has exited or been killed.</summary>
    Task<OperationOutcome> StopAsync(int instanceId, StopOptions options, CancellationToken cancellationToken);

    Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>"Stop now": ends a running stop job's countdown. False when no countdown is in progress.</summary>
    bool TrySkipCountdown(int instanceId);

    /// <summary>Re-attempts persisting <c>LastPid</c> / <c>LastProcessStartTime</c> for an <see cref="InstanceState.IdentityUnpersisted"/> instance.</summary>
    Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken);
}

/// <summary>One lock per instance serializing Start/Stop/Restart/Backup/Delete (plan step 19).</summary>
public interface IInstanceLocks
{
    /// <summary>Returns the held lock, or null when another operation holds it ("operation in progress"). Never waits.</summary>
    IDisposable? TryAcquire(int instanceId);

    /// <summary>Waits for the lock; used by scheduled work that may queue behind an operation.</summary>
    Task<IDisposable> AcquireAsync(int instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// The maintenance gate (plan step 19). Install/update take it exclusively as their very first action and
/// hold it through SteamCMD verification; launches hold it shared from before <c>Process.Start</c> until
/// the identity is persisted. Acquiring exclusively drains the launch queue (queued launches complete
/// rejected with "update in progress") and waits for in-flight launches to finish registration.
/// </summary>
public interface IMaintenanceGate
{
    Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken);

    /// <summary>Returns the held shared lease, or null while the gate is held exclusively.</summary>
    IDisposable? TryAcquireShared();

    bool IsHeldExclusively { get; }
}
