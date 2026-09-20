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

    /// <summary>
    /// Reads one process row by pid (B0), any executable. Throws when the enumeration itself fails; the caller
    /// treats that as Unknown. A row whose identity fields WMI withheld comes back <see cref="ProcessRowStatus.Incomplete"/>.
    /// </summary>
    ProcessRowRead ReadRow(int pid);

    /// <summary>Every game process plus whether every row was read completely (B0); a projection may only run on a complete, empty snapshot.</summary>
    ProcessTableSnapshot Snapshot();
}

/// <summary>One enumeration of <c>ArkAscendedServer.exe</c> rows; <paramref name="Complete"/> is false when any row could not be read.</summary>
public sealed record ProcessTableSnapshot(IReadOnlyList<GameProcessInfo> Processes, bool Complete);

/// <summary>Outcome of <see cref="IGameProcessEnumerator.ReadRow"/>: the row when one was found, and whether pid, path, and creation date were all readable.</summary>
public sealed record ProcessRowRead(ProcessRowStatus Status, GameProcessInfo? Row)
{
    public static readonly ProcessRowRead Missing = new(ProcessRowStatus.Missing, null);
}

public enum ProcessRowStatus
{
    /// <summary>Pid, executable path, and creation date were all present.</summary>
    Complete,
    /// <summary>A row exists but an identity field could not be read; nothing can be concluded from it.</summary>
    Incomplete,
    /// <summary>The enumeration completed and no row has this pid.</summary>
    Missing,
}

/// <summary>What <see cref="IProcessManager.ProbeSessionAsync"/> could establish about a session's process (B0).</summary>
public enum SessionLiveness
{
    /// <summary>A complete row with this pid whose creation date matches the session's start time.</summary>
    Alive,
    /// <summary>No row with this pid, or a complete row whose creation date belongs to another process.</summary>
    Dead,
    /// <summary>The enumeration threw, the row was incomplete, or no session is registered; never a reason to launch.</summary>
    Unknown,
}

/// <summary>
/// One <c>ListPlayers</c> reply tagged with the session it came from (B0): the 15-second health probe raises one per
/// success, and the owner's on-demand list is recorded the same way. The player tracker consumes them.
/// <paramref name="SentAt"/> is taken before the command is sent, so a slow reply cannot look newer than evidence
/// that arrived while it was in flight.
/// </summary>
public sealed record ProbeObservation(int InstanceId, int Pid, DateTimeOffset ProcessStartTime, DateTimeOffset SentAt, string Reply);

/// <summary>
/// Posted once per confirmed process exit after the manager's own cleanup (B0); the crash policy decides whether to
/// relaunch. <paramref name="StopIntent"/> is true when the manager had accepted a stop for this session, and such an
/// exit is never a crash.
/// </summary>
public sealed record RecoveryRequest(int InstanceId, int Pid, DateTimeOffset ProcessStartTime, int? ExitCode, bool StopIntent, DateTimeOffset ExitedAt);

/// <summary>Result of a management request; a rejection carries the reason the UI shows verbatim.</summary>
public sealed record OperationOutcome(bool Succeeded, string? Error = null)
{
    public static readonly OperationOutcome Success = new(true);

    public static OperationOutcome Rejected(string reason) => new(false, reason);
}

/// <param name="SkipCountdown">Skip the pre-stop broadcast countdown entirely.</param>
/// <param name="RequireVerifiedExit">Update and Delete need <c>HasExited</c> to be observed; a stop that had to kill still verifies.</param>
/// <param name="Deadline">
/// The absolute instant <c>doexit</c> goes out (B0). The countdown broadcasts the minutes remaining until it, so tick
/// delays never accumulate into drift. Null means the manager derives it from <c>PreStopBroadcastMinutes</c> when the
/// stop is accepted; ignored when <see cref="SkipCountdown"/> is set.
/// </param>
public sealed record StopOptions(bool SkipCountdown = false, bool RequireVerifiedExit = false, DateTimeOffset? Deadline = null);

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
/// <see cref="InstanceState.IdentityUnpersisted"/>. <see cref="ExitRequested"/> is true once a stop job is past
/// the countdown and has asked the server to exit (or has nothing to ask and is waiting for the graceful
/// timeout); the UI turns "Stop now" into a disabled "Closing…" at that point.
/// </summary>
public sealed record InstanceRuntime(
    int InstanceId,
    InstanceState State,
    int? Pid,
    DateTimeOffset? ProcessStartTime,
    StartupMarker? LastMarker,
    DateTimeOffset? LastRconSuccessAt,
    string? Detail,
    bool ExitRequested = false)
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

    /// <summary>Raised on a background thread after every successful health probe; see <see cref="ProbeObservation"/>.</summary>
    event Action<ProbeObservation>? ProbeObserved;

    /// <summary>
    /// One targeted process-table read for the instance's registered session (B0). Alive and Dead are conclusions;
    /// Unknown (no session, enumeration failure, incomplete row) is not, and never justifies a launch.
    /// </summary>
    Task<SessionLiveness> ProbeSessionAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the launch queue's projection reservation exclusively (B0): waits for in-flight launches, blocks new ones,
    /// then refuses unless no session is registered and a complete process-table snapshot shows no game process at
    /// all. The lease in a held result is the caller's to release; a refusal carries the reason and holds nothing.
    /// </summary>
    Task<ProjectionReservationResult> TryReserveProjectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues a launch and completes when the process has started and its identity is persisted, or
    /// with the rejection reason (not Ready, update in progress, operation in progress, port conflict,
    /// missing ServerAdminPassword, drained queue, Process.Start failure).
    /// </summary>
    Task<OperationOutcome> StartAsync(int instanceId, LaunchKind kind, CancellationToken cancellationToken);

    /// <summary>Runs the stop sequence (plan step 24) and completes when the process has exited or been killed.</summary>
    Task<OperationOutcome> StopAsync(int instanceId, StopOptions options, CancellationToken cancellationToken);

    /// <summary>Stop with the countdown derived from <c>PreStopBroadcastMinutes</c> now, then a queued start, all under one instance lease.</summary>
    Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Restart under one lease (B0): countdown to <paramref name="deadline"/>, <c>doexit</c> when it passes, a verified
    /// stop, then a queued start. The lease is held until the start completes or the stop fails, so nothing else can
    /// slip in between.
    /// </summary>
    Task<OperationOutcome> RestartWithCountdownAsync(int instanceId, DateTimeOffset deadline, CancellationToken cancellationToken);

    /// <summary>
    /// The stop sequence under a lease the caller already holds (B0): delete and restore own the lock for their whole
    /// job, and the locks are not reentrant, so they cannot call <see cref="StopAsync"/>. The lease stays the caller's
    /// to release. Throws when the lease is already released.
    /// </summary>
    Task<OperationOutcome> StopUnderLeaseAsync(IInstanceLease lease, StopOptions options, CancellationToken cancellationToken);

    /// <summary>"Stop now": ends a running stop job's countdown. False when no countdown is in progress.</summary>
    bool TrySkipCountdown(int instanceId);

    /// <summary>
    /// Broadcasts a countdown to <paramref name="deadline"/> on the instance's live session and completes when it
    /// passes (B3): the same loop the stop countdown uses, so the minutes are computed from the clock and the
    /// remaining time, never by adding elapsed minutes. <paramref name="messageTemplate"/> is a format string whose
    /// <c>{0}</c> becomes "in N minute(s)" for each announcement and "now" for the last one. Takes no lock; the caller
    /// holds the instance lease when the countdown must not be interrupted. Rejected, without broadcasting, when the
    /// instance has no live process or no RCON credentials. <see cref="TrySkipCountdown"/> ends it early.
    /// </summary>
    Task<OperationOutcome> BroadcastCountdownAsync(int instanceId, DateTimeOffset deadline, string messageTemplate, CancellationToken cancellationToken);

    /// <summary>Re-attempts persisting <c>LastPid</c> / <c>LastProcessStartTime</c> for an <see cref="InstanceState.IdentityUnpersisted"/> instance.</summary>
    Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken);
}

/// <summary>A held instance lock; disposing releases it once. Not reentrant: the holder passes it to work that needs it (B0).</summary>
public interface IInstanceLease : IDisposable
{
    int InstanceId { get; }

    bool IsReleased { get; }
}

/// <summary>One lock per instance serializing Start/Stop/Restart/Backup/Delete (plan step 19).</summary>
public interface IInstanceLocks
{
    /// <summary>Returns the held lock, or null when another operation holds it ("operation in progress"). Never waits.</summary>
    IInstanceLease? TryAcquire(int instanceId);

    /// <summary>Waits for the lock; used by scheduled work that may queue behind an operation.</summary>
    Task<IInstanceLease> AcquireAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves a whole cluster (B0, used by restore): while held, creating a member, deleting the cluster, and launching
    /// any member are refused. Returns null when the cluster is already reserved. Independent of the per-instance locks.
    /// </summary>
    IDisposable? TryReserveCluster(int clusterId);

    bool IsClusterReserved(int clusterId);
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

/// <summary>Outcome of <see cref="IProcessManager.TryReserveProjectionAsync"/>: the exclusive lease, or why it was refused.</summary>
public sealed record ProjectionReservationResult(IDisposable? Lease, string? RefusalReason)
{
    public bool Held => Lease is not null;
}

/// <summary>
/// Step one of every launch (B0 handoff): before a launch takes the projection reservation shared, it asks the
/// synchronizer to run one cycle and waits for it, holding nothing. The manager-wide list synchronizer (B5)
/// implements it; until then <see cref="NoProjectionSynchronizer"/> returns at once.
/// </summary>
public interface IProjectionSynchronizer
{
    Task RunCycleAsync(CancellationToken cancellationToken);
}

public sealed class NoProjectionSynchronizer : IProjectionSynchronizer
{
    public Task RunCycleAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
