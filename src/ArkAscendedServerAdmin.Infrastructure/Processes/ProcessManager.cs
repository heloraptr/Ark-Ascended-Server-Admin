using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Launch;
using ArkAscendedServerAdmin.Ports;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Processes;

/// <summary>
/// The process manager (plan steps 19, 21–25): the only component that launches, attaches to, probes,
/// stops, and kills game servers. Runtime state lives in memory and is mirrored best-effort into
/// <see cref="Instance.State"/>. Game servers are detached children: every background loop (log tail,
/// RCON probe, liveness poll) is tied to <see cref="IHostApplicationLifetime.ApplicationStopping"/>, never
/// to a caller's token, and a service stop never kills the game.
/// </summary>
/// <remarks>
/// Coordination between the stop job and the liveness loop: the liveness loop is the single owner of the
/// "process exited" transition (console line, Stopped state, database identity clear, loop shutdown) and
/// completes the session's exit signal last; the stop job only waits on that signal — after an acknowledged
/// <c>doexit</c> for the graceful timeout, after <c>Kill</c> for the verification bound — so both paths return a
/// verified exit. A <c>doexit</c> the server did not take (refused, timed out, or rejected) is followed by the kill
/// at once: a healthy server answers it with <c>Exiting...</c> within milliseconds and saves only after that, so
/// nothing is in flight to wait for (this is what makes a stop during startup take seconds, not the timeout). A
/// process without RCON credentials still gets the graceful wait, since nothing was sent to it. The stop intent is
/// recorded on the session the moment a stop is accepted, under the caller's lease and before the job is
/// dispatched, and the job marks the stop as manager-initiated again before <c>doexit</c>; either flag is how the
/// liveness loop tells the normal exit code -1 from a crash.
/// <para>
/// Ownership (B0): the per-instance locks are not reentrant, so the public operations take the lease and the
/// <c>*Core</c> methods run under one. <see cref="StopUnderLeaseAsync"/> lets delete and restore stop under the
/// lease they already hold, and <see cref="RestartWithCountdownAsync"/> keeps one lease across the countdown, the
/// verified stop, and the queued start. Countdowns target an absolute deadline rather than counting elapsed
/// minutes, so tick delays never accumulate into drift.
/// </para>
/// </remarks>
public sealed class ProcessManager : IProcessManager, IProcessReconciler
{
    /// <summary>RCON <c>ListPlayers</c> cadence while a process is alive (plan step 22).</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);

    /// <summary><c>HasExited</c> polling cadence; the <c>Exited</c> event only fires for processes this service started.</summary>
    public static readonly TimeSpan LivenessInterval = TimeSpan.FromSeconds(2);

    /// <summary>Starting without a successful probe for this long becomes StartingUnconfirmed (launched) or Unreachable (attached).</summary>
    public static readonly TimeSpan StartupBound = TimeSpan.FromMinutes(10);

    /// <summary>A remainder this short at the top of a countdown pass is timer skew, not a minute worth announcing.</summary>
    private static readonly TimeSpan _countdownSlack = TimeSpan.FromSeconds(1);

    /// <summary>The stop countdown's broadcast; <c>{0}</c> is "in N minute(s)" or "now" (see <see cref="BroadcastCountdownAsync"/>).</summary>
    private const string StopCountdownTemplate = "Server shutting down {0}.";

    /// <summary>How long a stop waits for <c>HasExited</c> after <c>Kill</c> before reporting an unverified exit.</summary>
    public static readonly TimeSpan ExitVerificationBound = TimeSpan.FromSeconds(30);

    /// <summary>Identity persistence: one attempt plus three retries over ~10 s (plan step 19).</summary>
    public static readonly IReadOnlyList<TimeSpan> PersistRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    public const string OperationInProgress = "operation in progress";

    /// <summary>An automatic restart whose instance had <see cref="Instance.AutoRestart"/> turned off before the launch committed (B4).</summary>
    public const string AutoRestartTurnedOff = "Automatic restart was turned off for this instance.";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DataRootLayout _paths;
    private readonly IAppSettingsStore _settings;
    private readonly HostConfiguration _host;
    private readonly IReadinessMonitor _readiness;
    private readonly IInstanceLocks _locks;
    private readonly IRestoreJournals _restoreJournals;
    private readonly IMaintenanceGate _gate;
    private readonly LaunchQueue _queue;
    private readonly RecoveryRequests _recovery;
    private readonly IProjectionSynchronizer _synchronizer;
    private readonly IRconClient _rcon;
    private readonly IGameProcessEnumerator _enumerator;
    private readonly IGameProcessStarter _starter;
    private readonly IFirewallRules _firewall;
    private readonly IInstanceLayoutService _layout;
    private readonly IGeneratedConfigWriter _configWriter;
    private readonly IOutputSourceFactory _outputs;
    private readonly IConsoleService _console;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private readonly ILogger<ProcessManager> _logger;

    private readonly ConcurrentDictionary<int, InstanceRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly ConcurrentDictionary<int, InstanceTelemetry> _telemetry = new();

    public ProcessManager(
        IDbContextFactory<AppDbContext> dbFactory,
        DataRootLayout paths,
        IAppSettingsStore settings,
        HostConfiguration host,
        IReadinessMonitor readiness,
        IInstanceLocks locks,
        IRestoreJournals restoreJournals,
        IMaintenanceGate gate,
        LaunchQueue queue,
        RecoveryRequests recovery,
        IProjectionSynchronizer synchronizer,
        IRconClient rcon,
        IGameProcessEnumerator enumerator,
        IGameProcessStarter starter,
        IFirewallRules firewall,
        IInstanceLayoutService layout,
        IGeneratedConfigWriter configWriter,
        IOutputSourceFactory outputs,
        IConsoleService console,
        TimeProvider time,
        IHostApplicationLifetime lifetime,
        ILogger<ProcessManager> logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        _dbFactory = dbFactory;
        _paths = paths;
        _settings = settings;
        _host = host;
        _readiness = readiness;
        _locks = locks;
        _restoreJournals = restoreJournals;
        _gate = gate;
        _queue = queue;
        _recovery = recovery;
        _synchronizer = synchronizer;
        _rcon = rcon;
        _enumerator = enumerator;
        _starter = starter;
        _firewall = firewall;
        _layout = layout;
        _configWriter = configWriter;
        _outputs = outputs;
        _console = console;
        _time = time;
        _lifetime = lifetime.ApplicationStopping;
        _logger = logger;
    }

    /// <summary>Pause points for the race tests (B4); never set in production.</summary>
    internal ProcessManagerTestHooks TestHooks { get; } = new();

    public event Action<InstanceRuntime>? RuntimeChanged;

    public event Action<ProbeObservation>? ProbeObserved;

    public event Action<int, InstanceTelemetry?>? TelemetryChanged;

    public InstanceTelemetry? GetTelemetry(int instanceId) =>
        _telemetry.TryGetValue(instanceId, out var sample) ? sample : null;

    public InstanceRuntime GetRuntime(int instanceId) =>
        _runtimes.TryGetValue(instanceId, out var runtime) ? runtime : Default(instanceId);

    public IReadOnlyList<InstanceRuntime> GetAllRuntimes() =>
        _runtimes.Values.OrderBy(runtime => runtime.InstanceId).ToList();

    // ---- start (plan steps 19, 25; B0 owned lifecycle) ---------------------------------------------

    public async Task<OperationOutcome> StartAsync(int instanceId, LaunchKind kind, CancellationToken cancellationToken)
    {
        var lease = _locks.TryAcquire(instanceId);
        if (lease is null)
        {
            return OperationOutcome.Rejected(OperationInProgress);
        }

        try
        {
            if (kind != LaunchKind.AutoRestart)
            {
                ResetCrashRecovery(instanceId);
            }

            return await StartCoreAsync(instanceId, kind, cancellationToken);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// A start that is not automatic (B4) takes over from crash recovery under its lease, before any check that could
    /// reject it: the pending exit is answered, the automatic-restart count goes back to 0, and Crashed becomes Stopped,
    /// even when the start is then refused. Skipped while a process is live or a session is registered, where the start
    /// is refused anyway and the count still belongs to the running session.
    /// </summary>
    private void ResetCrashRecovery(int instanceId)
    {
        if (_sessions.ContainsKey(instanceId))
        {
            return;
        }

        UpdateIf(instanceId, runtime => runtime.HasLiveProcess
            ? null
            : runtime with
            {
                State = runtime.State == InstanceState.Crashed ? InstanceState.Stopped : runtime.State,
                Detail = runtime.State == InstanceState.Crashed ? null : runtime.Detail,
                PendingCrash = null,
                AutoRestarts = 0,
            });
    }

    /// <summary>
    /// The launch under a lease the caller holds: readiness and gate checks, then the queue. Refused while any
    /// session is registered for the instance, whatever its displayed state, so a stop that ended without a
    /// verified exit can never be followed by a second process. <paramref name="autoRestarts"/> is the count the
    /// new session's runtime starts with (B4): 0 for every launch except an automatic restart.
    /// </summary>
    private async Task<OperationOutcome> StartCoreAsync(int instanceId, LaunchKind kind, CancellationToken cancellationToken, int autoRestarts = 0, LaunchReport? report = null)
    {
        if (kind is LaunchKind.User or LaunchKind.AutoRestart && !_readiness.Current.IsReady)
        {
            return OperationOutcome.Rejected($"The service is not ready yet ({_readiness.Current.Phase}: {_readiness.Current.Message}).");
        }

        if (_gate.IsHeldExclusively)
        {
            return OperationOutcome.Rejected(MaintenanceGate.UpdateInProgress);
        }

        var runtime = GetRuntime(instanceId);
        if (_sessions.ContainsKey(instanceId) || runtime.HasLiveProcess)
        {
            return OperationOutcome.Rejected($"The instance is already {Describe(runtime.State)}.");
        }

        return await _queue.EnqueueAsync(instanceId, kind, token => LaunchAsync(instanceId, kind, autoRestarts, report, token), cancellationToken);
    }

    /// <summary>
    /// The launch callback run by the queue worker; the token is the queue's lifetime, not the caller's. For an
    /// automatic restart the instance row read here is the commitment point (B4): <see cref="Instance.AutoRestart"/>
    /// turned off before it cancels the relaunch with <see cref="AutoRestartTurnedOff"/>; turned off after it, the
    /// launch runs like any other and can be stopped.
    /// </summary>
    private async Task<OperationOutcome> LaunchAsync(int instanceId, LaunchKind kind, int autoRestarts, LaunchReport? report, CancellationToken cancellationToken)
    {
        var channel = ConsoleChannels.Instance(instanceId);
        Instance? instance;
        List<PortOwner> others;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            instance = await db.Instances
                .AsNoTracking()
                .Include(i => i.Cluster).ThenInclude(c => c!.Mods)
                .Include(i => i.Mods)
                .Include(i => i.Map)
                .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
            if (instance is null)
            {
                return OperationOutcome.Rejected($"Instance {instanceId} no longer exists.");
            }

            if (kind == LaunchKind.AutoRestart && !instance.AutoRestart)
            {
                return OperationOutcome.Rejected(AutoRestartTurnedOff);
            }

            others = await db.Instances
                .AsNoTracking()
                .Where(i => i.Id != instanceId)
                .Select(i => new PortOwner(i.Name, i.GamePort, i.RconPort))
                .ToListAsync(cancellationToken);
        }

        if (instance.ClusterId is { } clusterId && _locks.IsClusterReserved(clusterId))
        {
            report?.HeldByRestore = true;
            return OperationOutcome.Rejected(InstanceLocks.ClusterReservedByRestore);
        }

        if (_restoreJournals.FindForInstance(instanceId) is { } journal)
        {
            report?.HeldByRestore = true;
            return OperationOutcome.Rejected(journal.RefusalReason("this instance"));
        }

        var slug = instance.Slug;
        await _layout.EnsureAsync(slug, cancellationToken);

        // Handoff (B0): (1) let the synchronizer run a cycle first, holding nothing; (2) hold the projection reservation
        // shared from here through session registration, so a projection can never race this launch.
        await _synchronizer.RunCycleAsync(cancellationToken);
        using var reservation = await _queue.Reservation.AcquireSharedAsync(cancellationToken);

        var generated = await _configWriter.WriteAsync(instanceId, cancellationToken);
        foreach (var warning in generated.Warnings)
        {
            Append(channel, $"Config: {warning}", ConsoleLineKind.Warning);
        }

        var rconEndpoint = RconCredentials.TryRead(generated.GameUserSettingsIni, out var credentialProblem);
        if (rconEndpoint is null)
        {
            return OperationOutcome.Rejected(credentialProblem ?? "RCON credentials could not be read from the generated GameUserSettings.ini.");
        }

        var settings = await _settings.GetAsync(cancellationToken);
        var conflicts = FindPortConflicts(instance, others, settings);
        if (conflicts.Count > 0)
        {
            return OperationOutcome.Rejected("Port conflict: " + string.Join(" ", conflicts.Select(conflict => conflict.Reason)));
        }

        try
        {
            _firewall.EnsureInstanceRules(instanceId, instance.GamePort);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Firewall rules for instance {InstanceId} could not be reconciled; launching anyway.", instanceId);
            Append(channel, $"Firewall: {ex.Message} The server starts anyway; open UDP {instance.GamePort}-{instance.GamePort + 1} manually if players cannot join.", ConsoleLineKind.Warning);
        }

        LaunchArguments arguments;
        try
        {
            arguments = LaunchArgumentBuilder.Build(BuildLaunchRequest(instance));
        }
        catch (LaunchValidationException ex)
        {
            return OperationOutcome.Rejected(ex.Message);
        }

        var lease = _gate.TryAcquireShared();
        if (lease is null)
        {
            return OperationOutcome.Rejected(MaintenanceGate.UpdateInProgress);
        }

        try
        {
            var executable = _paths.InstanceExecutable(slug);
            Process process;
            try
            {
                process = StartProcess(executable, arguments);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Process.Start failed for instance {InstanceId} ({Executable}).", instanceId, executable);
                Append(channel, $"Process.Start failed: {ex.Message}", ConsoleLineKind.Error);
                return OperationOutcome.Rejected($"Process.Start failed: {ex.Message}");
            }

            var startTime = ReadStartTime(process) ?? _time.GetUtcNow();
            var session = Register(instance, process, startTime, attached: false, rconEndpoint, autoRestarts);
            report?.Registered = true;
            session.LaunchedAt = _time.GetUtcNow();
            Append(channel, $"Launched pid {process.Id}: {executable} {arguments.ToDisplayString()}", ConsoleLineKind.Info);
            _logger.LogInformation("Instance {InstanceId} ({Slug}) launched as pid {Pid}.", instanceId, slug, process.Id);

            var persistError = await PersistIdentityWithRetriesAsync(session, cancellationToken);
            StartLoops(session);
            if (persistError is not null)
            {
                // The liveness loop already runs: a process that exited meanwhile keeps the Stopped its exit published.
                UpdateForSession(session, runtime => runtime with { State = InstanceState.IdentityUnpersisted, Detail = persistError });
                Append(channel, $"The server is running (pid {process.Id}) but its identity could not be saved: {persistError}", ConsoleLineKind.Error);
                return OperationOutcome.Rejected($"The server started (pid {process.Id}) but its identity could not be saved after {PersistRetryDelays.Count} retries: {persistError} Use 'Retry persist'.");
            }

            return OperationOutcome.Success;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private Process StartProcess(string executable, LaunchArguments arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? _paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return _starter.Start(startInfo);
    }

    private LaunchRequest BuildLaunchRequest(Instance instance)
    {
        var cluster = instance.Cluster;
        var map = instance.Map ?? throw new LaunchValidationException([$"Instance '{instance.Name}' has no map."]);
        return new LaunchRequest(
            map.Key,
            instance.Slug,
            instance.GamePort,
            instance.MaxPlayers,
            cluster?.ClusterKey,
            cluster is null ? null : _paths.ClusterDirectory(cluster.Slug),
            cluster is null ? [] : cluster.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).Select(mod => mod.ModId).ToList(),
            instance.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).Select(mod => mod.ModId).ToList(),
            LaunchFlagResolver.Resolve(cluster?.LaunchFlags, instance.LaunchFlags),
            map.ModId);
    }

    private IReadOnlyList<PortConflict> FindPortConflicts(Instance instance, IEnumerable<PortOwner> others, AppSettings settings)
    {
        var candidate = new PortOwner(instance.Name, instance.GamePort, instance.RconPort);
        HashSet<int>? udp = null;
        HashSet<int>? tcp = null;
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            udp = properties.GetActiveUdpListeners().Select(endpoint => endpoint.Port).ToHashSet();
            tcp = properties.GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "OS listener tables are unavailable; checking ports against defined instances only.");
        }

        var hostPorts = KestrelPorts.Parse(_host.BindUrls);
        var conflicts = new PortAllocator(settings)
            .FindConflicts(candidate, others, hostPorts.Count > 0 ? hostPorts[0] : 0, udp, tcp)
            .ToList();

        foreach (var hostPort in hostPorts.Skip(1))
        {
            foreach (var port in new[] { candidate.GamePort, candidate.GamePort + 1, candidate.RconPort })
            {
                if (port == hostPort)
                {
                    conflicts.Add(new PortConflict(port, $"Port {port} is used by the web UI."));
                }
            }
        }

        return conflicts;
    }

    // ---- identity persistence (plan step 19) ------------------------------------------------------

    /// <summary>
    /// Returns null on success (or when the process exited meanwhile, which leaves nothing to persist), else the last
    /// storage error after the bounded retries.
    /// </summary>
    private async Task<string?> PersistIdentityWithRetriesAsync(Session session, CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 0; attempt <= PersistRetryDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(PersistRetryDelays[attempt - 1], _time, cancellationToken);
            }

            try
            {
                await PersistIdentityAsync(session, cancellationToken);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Persisting identity for instance {InstanceId} failed (attempt {Attempt}).", session.InstanceId, attempt + 1);
            }
        }

        return lastError;
    }

    /// <summary>
    /// Writes the session's identity into its row under the session's write mutex (B4), so it lands before the exit
    /// cleanup clears it, never after; once the exit has begun it writes nothing.
    /// </summary>
    private async Task PersistIdentityAsync(Session session, CancellationToken cancellationToken)
    {
        await session.WriteMutex.WaitAsync(cancellationToken);
        try
        {
            if (session.Exiting)
            {
                return;
            }

            await WriteIdentityAsync(session, cancellationToken);
        }
        finally
        {
            session.WriteMutex.Release();
        }
    }

    private async Task WriteIdentityAsync(Session session, CancellationToken cancellationToken)
    {
        var state = session.ProbeSucceeded ? InstanceState.Running : InstanceState.Starting;
        int? pid = session.Pid;
        DateTimeOffset? startTime = session.StartTime;
        DateTimeOffset? launchedAt = session.LaunchedAt;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = launchedAt is null
            ? await db.Instances.Where(i => i.Id == session.InstanceId).ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.LastPid, pid)
                    .SetProperty(i => i.LastProcessStartTime, startTime)
                    .SetProperty(i => i.State, state),
                cancellationToken)
            : await db.Instances.Where(i => i.Id == session.InstanceId).ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.LastPid, pid)
                    .SetProperty(i => i.LastProcessStartTime, startTime)
                    .SetProperty(i => i.LastLaunchedAt, launchedAt)
                    .SetProperty(i => i.State, state),
                cancellationToken);
        if (updated == 0)
        {
            throw new InvalidOperationException($"Instance {session.InstanceId} no longer exists in the database.");
        }
    }

    public async Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken)
    {
        var instanceLock = _locks.TryAcquire(instanceId);
        if (instanceLock is null)
        {
            return OperationOutcome.Rejected(OperationInProgress);
        }

        try
        {
            if (!_sessions.TryGetValue(instanceId, out var session) || GetRuntime(instanceId).State != InstanceState.IdentityUnpersisted)
            {
                return OperationOutcome.Rejected("The instance is not waiting for its identity to be persisted.");
            }

            var error = await PersistIdentityWithRetriesAsync(session, cancellationToken);
            if (error is not null)
            {
                UpdateForSession(session, runtime => runtime with { Detail = error });
                return OperationOutcome.Rejected(error);
            }

            var state = session.ProbeSucceeded ? InstanceState.Running : InstanceState.Starting;
            if (!UpdateForSession(session, runtime => runtime with { State = state, Detail = null }))
            {
                return OperationOutcome.Rejected("The instance is not running.");
            }

            Append(ConsoleChannels.Instance(instanceId), $"Identity persisted (pid {session.Pid}).", ConsoleLineKind.Info);
            return OperationOutcome.Success;
        }
        finally
        {
            instanceLock.Dispose();
        }
    }

    // ---- reconciliation (plan step 21) ------------------------------------------------------------

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        List<Instance> instances;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            instances = await db.Instances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(cancellationToken);
        }

        IReadOnlyList<GameProcessInfo> candidates;
        try
        {
            candidates = _enumerator.Enumerate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Process enumeration failed; every instance is marked Unknown.");
            foreach (var instance in instances)
            {
                await SetStateAsync(instance.Id, InstanceState.Unknown, $"Process enumeration failed: {ex.Message}", cancellationToken);
            }

            return;
        }

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.ContainsKey(instance.Id))
            {
                continue;
            }

            var request = new ProcessMatchRequest(instance.Slug, _paths.Instances, instance.LastPid, instance.LastProcessStartTime);
            switch (ProcessMatcher.Match(request, candidates))
            {
                case ProcessMatch.Attach attach:
                    await AttachAsync(instance, attach.Process, cancellationToken);
                    break;
                case ProcessMatch.Ambiguous ambiguous:
                    var pids = string.Join(", ", ambiguous.Candidates.Select(candidate => candidate.Pid.ToString(CultureInfo.InvariantCulture)));
                    _logger.LogWarning("Instance {InstanceId} ({Slug}) matches {Count} processes (pids {Pids}); marked Unknown.", instance.Id, instance.Slug, ambiguous.Candidates.Count, pids);
                    await SetStateAsync(instance.Id, InstanceState.Unknown, $"{ambiguous.Candidates.Count} running processes claim this instance (pids {pids}); stop the extra ones by hand and restart the service.", cancellationToken);
                    break;
                default:
                    await SetStateAsync(instance.Id, InstanceState.Stopped, null, cancellationToken);
                    break;
            }
        }
    }

    private async Task AttachAsync(Instance instance, GameProcessInfo info, CancellationToken cancellationToken)
    {
        var channel = ConsoleChannels.Instance(instance.Id);
        Process process;
        try
        {
            process = Process.GetProcessById(info.Pid);
            if (process.HasExited)
            {
                process.Dispose();
                await SetStateAsync(instance.Id, InstanceState.Stopped, null, cancellationToken);
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _logger.LogInformation(ex, "Pid {Pid} for instance {InstanceId} vanished before attach.", info.Pid, instance.Id);
            await SetStateAsync(instance.Id, InstanceState.Stopped, null, cancellationToken);
            return;
        }

        var startTime = ReadStartTime(process) ?? info.CreationTime;
        RconEndpoint? rconEndpoint = null;
        string? problem;
        var generatedText = await _configWriter.ReadGeneratedGameUserSettingsAsync(instance.Slug, cancellationToken);
        if (generatedText is null)
        {
            problem = "the generated GameUserSettings.ini is missing.";
        }
        else
        {
            rconEndpoint = RconCredentials.TryRead(generatedText, out problem);
        }

        var session = Register(instance, process, startTime, attached: true, rconEndpoint);
        Append(channel, $"re-attached — log history (pid {process.Id}, started {startTime.ToLocalTime():yyyy-MM-dd HH:mm:ss})", ConsoleLineKind.Info);
        _logger.LogInformation("Re-attached instance {InstanceId} ({Slug}) to pid {Pid}.", instance.Id, instance.Slug, process.Id);

        if (rconEndpoint is null)
        {
            var detail = $"Cannot probe RCON: {problem} Check ServerAdminPassword / RCONPort; the process is still watched for exit.";
            Append(channel, detail, ConsoleLineKind.Warning);
            Update(instance.Id, runtime => runtime with { State = InstanceState.Unreachable, Detail = detail });
        }

        try
        {
            await PersistIdentityAsync(session, cancellationToken);
            await MirrorStateAsync(instance.Id, GetRuntime(instance.Id).State, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist the re-attached identity of instance {InstanceId}.", instance.Id);
        }

        StartLoops(session);
    }

    // ---- stop (plan step 24; B0 owned lifecycle) -----------------------------------------------

    /// <summary>A <c>doexit</c> this far past its deadline gets a console line saying how late it was.</summary>
    public static readonly TimeSpan LateExitTolerance = TimeSpan.FromSeconds(5);

    public async Task<OperationOutcome> StopAsync(int instanceId, StopOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lease = _locks.TryAcquire(instanceId);
        if (lease is null)
        {
            return OperationOutcome.Rejected(OperationInProgress);
        }

        if (!TryAcceptStop(instanceId, out var session, out var rejection))
        {
            lease.Dispose();
            return rejection;
        }

        // The job owns the lease and runs to completion on the host lifetime; the caller only waits.
        var job = Task.Run(async () =>
        {
            try
            {
                return await RunStopJobAsync(session, options);
            }
            finally
            {
                lease.Dispose();
            }
        });

        return await job.WaitAsync(cancellationToken);
    }

    public Task<OperationOutcome> StopUnderLeaseAsync(IInstanceLease lease, StopOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(options);
        if (lease.IsReleased)
        {
            throw new InvalidOperationException($"The lease for instance {lease.InstanceId} has already been released.");
        }

        if (!TryAcceptStop(lease.InstanceId, out var session, out var rejection))
        {
            return Task.FromResult(rejection);
        }

        return Task.Run(() => RunStopJobAsync(session, options)).WaitAsync(cancellationToken);
    }

    public async Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        var deadline = _time.GetUtcNow() + TimeSpan.FromMinutes(settings.PreStopBroadcastMinutes);
        return await RestartWithCountdownAsync(instanceId, deadline, cancellationToken);
    }

    public async Task<OperationOutcome> RestartWithCountdownAsync(int instanceId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var lease = _locks.TryAcquire(instanceId);
        if (lease is null)
        {
            return OperationOutcome.Rejected(OperationInProgress);
        }

        if (!TryAcceptStop(instanceId, out var session, out var rejection))
        {
            lease.Dispose();
            return rejection;
        }

        // One lease across the countdown, the verified stop, and the queued start; the job owns it, the caller only waits.
        var job = Task.Run(async () =>
        {
            try
            {
                var stopped = await RunStopJobAsync(session, new StopOptions(Deadline: deadline));
                return stopped.Succeeded ? await StartCoreAsync(instanceId, LaunchKind.User, _lifetime) : stopped;
            }
            finally
            {
                lease.Dispose();
            }
        });

        return await job.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Records the stop intent on the live session synchronously, under the caller's lease, before any job is dispatched.
    /// Decided under the session lock against the exit path (B4): a stop accepted before the exit was observed is carried
    /// by the exit as its intent. A stop that finds the exit already begun is accepted too, because the owner asked for the
    /// server to be down and it is: the crash is dismissed under the caller's lease (the exit captured its epoch in the
    /// same critical section that set <c>Exiting</c>, so the dismissal always voids it), and the job only waits for the
    /// exit signal (see <see cref="RunStopJobAsync"/>). The session stays registered until the exit is published, so
    /// a stop during the cleanup always finds it.
    /// </summary>
    private bool TryAcceptStop(int instanceId, out Session session, out OperationOutcome rejection)
    {
        rejection = OperationOutcome.Rejected("The instance is not running.");
        if (!_sessions.TryGetValue(instanceId, out session!) || !GetRuntime(instanceId).HasLiveProcess)
        {
            return false;
        }

        bool exiting;
        lock (session.Sync)
        {
            exiting = session.Exiting;
            session.StopIntent = true;
        }

        if (exiting)
        {
            DismissCrash(instanceId);
        }

        rejection = OperationOutcome.Success;
        return true;
    }

    private async Task<OperationOutcome> RunStopJobAsync(Session session, StopOptions options)
    {
        var token = _lifetime;
        var channel = ConsoleChannels.Instance(session.InstanceId);
        try
        {
            // Accepted while the process's exit was already being cleaned up (B4): no countdown, doexit, kill, or Stopping.
            if (session.Exiting)
            {
                return await ExitedOnItsOwnAsync(session, token);
            }

            var settings = await _settings.GetAsync(token);
            var timeout = TimeSpan.FromSeconds(settings.RconCommandTimeoutSeconds);
            var deadline = options.SkipCountdown
                ? null
                : options.Deadline ?? (settings.PreStopBroadcastMinutes > 0 ? _time.GetUtcNow() + TimeSpan.FromMinutes(settings.PreStopBroadcastMinutes) : null);

            // The process can exit on its own after the stop was accepted (B4). Its exit carries the stop intent and owns the
            // cleanup; the job then neither puts the dead instance back into Stopping nor broadcasts, sends doexit, or kills.
            if (!await SetSessionStateAsync(session, InstanceState.Stopping, null, token))
            {
                return await ExitedOnItsOwnAsync(session, token);
            }

            var killNow = false;
            if (session.Rcon is { } rcon)
            {
                if (deadline is { } countdownDeadline)
                {
                    await CountdownAsync(session, rcon, countdownDeadline, StopCountdownTemplate, timeout, token);
                }

                if (session.Exited.Task.IsCompleted)
                {
                    return OperationOutcome.Success;
                }

                // No explicit saveworld: doexit saves the world itself ("Saving world..." twice in the log before "Closing by request", captured 2026-09-13).
                session.StopRequested = true;
                UpdateForSession(session, runtime => runtime with { ExitRequested = true });
                var acknowledged = await TryRconAsync(session, rcon, RconCommands.DoExit, timeout, token);
                if (deadline is { } expected && _time.GetUtcNow() - expected is { } late && late > LateExitTolerance)
                {
                    Append(channel, $"doexit went out {late.TotalSeconds:0} s after the deadline (a slow reply or a busy transport held it up).", ConsoleLineKind.Warning);
                }

                if (!acknowledged)
                {
                    // The server replies "Exiting..." in about 20 ms and only then saves and exits (spike, 2026-09-07); a doexit that
                    // was refused, timed out, or rejected was not taken, so there is no save in flight for the graceful wait to protect.
                    // Typical during startup, when the RCON listener is not up yet (#27).
                    killNow = true;
                    Append(channel, $"doexit was not acknowledged; killing pid {session.Pid} now instead of waiting {settings.GracefulStopTimeoutSeconds} s.", ConsoleLineKind.Warning);
                    _logger.LogWarning("Instance {InstanceId}: doexit was not acknowledged; killing pid {Pid} now instead of waiting {Seconds} s.", session.InstanceId, session.Pid, settings.GracefulStopTimeoutSeconds);
                }
            }
            else
            {
                session.StopRequested = true;
                UpdateForSession(session, runtime => runtime with { ExitRequested = true });
                Append(channel, "No RCON credentials for this process; skipping doexit and waiting for the graceful timeout before killing.", ConsoleLineKind.Warning);
            }

            var graceful = TimeSpan.FromSeconds(settings.GracefulStopTimeoutSeconds);
            if (killNow || !await WaitForExitAsync(session, graceful, token))
            {
                if (session.Exited.Task.IsCompleted)
                {
                    return OperationOutcome.Success;
                }

                if (!killNow)
                {
                    Append(channel, $"The server did not exit within {settings.GracefulStopTimeoutSeconds} s; killing pid {session.Pid}.", ConsoleLineKind.Warning);
                    _logger.LogWarning("Instance {InstanceId} did not exit within {Seconds} s; killing pid {Pid}.", session.InstanceId, settings.GracefulStopTimeoutSeconds, session.Pid);
                }

                try
                {
                    session.Process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
                {
                    _logger.LogError(ex, "Kill failed for pid {Pid}.", session.Pid);
                    Append(channel, $"Kill failed: {ex.Message}", ConsoleLineKind.Error);
                }

                if (!await WaitForExitAsync(session, ExitVerificationBound, token))
                {
                    // The session stays registered and supervised with the intent set: a later exit is still a requested one.
                    var detail = $"Pid {session.Pid} is still alive after kill; its exit could not be verified.";
                    UpdateForSession(session, runtime => runtime with { Detail = detail });
                    return OperationOutcome.Rejected(detail);
                }
            }

            return OperationOutcome.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return OperationOutcome.Rejected("The service is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop job for instance {InstanceId} failed.", session.InstanceId);
            Append(channel, $"Stop failed: {ex.Message}", ConsoleLineKind.Error);
            return OperationOutcome.Rejected(ex.Message);
        }
    }

    public async Task<OperationOutcome> BroadcastCountdownAsync(int instanceId, DateTimeOffset deadline, string messageTemplate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);
        if (!_sessions.TryGetValue(instanceId, out var session) || !GetRuntime(instanceId).HasLiveProcess)
        {
            return OperationOutcome.Rejected("The instance is not running.");
        }

        if (session.Rcon is not { } rcon)
        {
            return OperationOutcome.Rejected("No RCON credentials for this process, so nothing can be broadcast.");
        }

        var settings = await _settings.GetAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
        await CountdownAsync(session, rcon, deadline, messageTemplate, TimeSpan.FromSeconds(settings.RconCommandTimeoutSeconds), linked.Token);
        return OperationOutcome.Success;
    }

    /// <summary>
    /// Broadcasts the minutes remaining until <paramref name="deadline"/> and returns when it passes (or the countdown
    /// is skipped). Each wait runs to the next whole-minute mark before the deadline, computed from the clock, so a
    /// slow reply never pushes <c>doexit</c> later than the deadline plus that one reply. The stop sequence and
    /// <see cref="BroadcastCountdownAsync"/> share this loop; <paramref name="messageTemplate"/> supplies the words
    /// around "in N minute(s)" and, for the last broadcast, "now". A process that exits during the countdown ends it at
    /// once without the last broadcast (B4): there is nobody left to tell.
    /// </summary>
    private async Task CountdownAsync(Session session, RconEndpoint rcon, DateTimeOffset deadline, string messageTemplate, TimeSpan timeout, CancellationToken token)
    {
        using var skip = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.SkipCountdown = skip;
        try
        {
            while (!skip.IsCancellationRequested)
            {
                if (session.Exited.Task.IsCompleted)
                {
                    return;
                }

                var remaining = deadline - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                if (remaining <= _countdownSlack)
                {
                    // A timer can wake a few milliseconds before the clock reaches the deadline; wait out the remainder
                    // rather than announce the same minute twice.
                    await Task.Delay(remaining, _time, token);
                    continue;
                }

                var minutes = Math.Max(1, (int)Math.Ceiling((remaining - TimeSpan.FromSeconds(1)).TotalMinutes));
                var phrase = $"in {minutes} minute{(minutes == 1 ? string.Empty : "s")}";
                await TryRconAsync(session, rcon, RconCommands.Broadcast(string.Format(CultureInfo.InvariantCulture, messageTemplate, phrase)), timeout, token);
                var wait = remaining - TimeSpan.FromMinutes(minutes - 1);
                var delay = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, _time, skip.Token);
                await Task.WhenAny(delay, session.Exited.Task);
                if (session.Exited.Task.IsCompleted)
                {
                    await skip.CancelAsync();
                    return;
                }

                try
                {
                    await delay;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Append(ConsoleChannels.Instance(session.InstanceId), "Countdown skipped.", ConsoleLineKind.Info);
                    break;
                }
            }

            if (session.Exited.Task.IsCompleted)
            {
                return;
            }

            await TryRconAsync(session, rcon, RconCommands.Broadcast(string.Format(CultureInfo.InvariantCulture, messageTemplate, "now")), timeout, token);
        }
        finally
        {
            session.SkipCountdown = null;
        }
    }

    public bool TrySkipCountdown(int instanceId)
    {
        if (!_sessions.TryGetValue(instanceId, out var session) || session.SkipCountdown is not { } skip)
        {
            return false;
        }

        try
        {
            if (skip.IsCancellationRequested)
            {
                return false;
            }

            skip.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends one command and returns whether the server answered it. A failure is logged and reported on the console so
    /// the caller can fall through to its next step; the stop job uses the result to choose between the graceful wait
    /// (<c>doexit</c> acknowledged) and an immediate kill (not acknowledged).
    /// </summary>
    private async Task<bool> TryRconAsync(Session session, RconEndpoint rcon, string command, TimeSpan timeout, CancellationToken token)
    {
        var channel = ConsoleChannels.Instance(session.InstanceId);
        Append(channel, $"RCON: {command}", ConsoleLineKind.Info);
        try
        {
            var reply = await _rcon.ExecuteAsync(rcon, command, timeout, token);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                Append(channel, reply.Trim(), ConsoleLineKind.Output);
            }

            return true;
        }
        catch (RconException ex)
        {
            _logger.LogWarning(ex, "RCON '{Command}' failed for instance {InstanceId} ({Failure}).", command, session.InstanceId, ex.Failure);
            Append(channel, $"RCON '{command}' failed ({ex.Failure}): {ex.Message} Continuing with the next step.", ConsoleLineKind.Warning);
            return false;
        }
    }

    private async Task<bool> WaitForExitAsync(Session session, TimeSpan bound, CancellationToken token)
    {
        try
        {
            await session.Exited.Task.WaitAsync(bound, _time, token);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ---- sessions and loops (plan step 22) --------------------------------------------------------

    /// <summary>
    /// Registers the session and replaces the runtime record with a fresh one. The new record starts with
    /// <paramref name="autoRestarts"/> (B4: 0 for every launch and attach except an automatic restart), no pending crash,
    /// and the previous record's <see cref="InstanceRuntime.RecoveryEpoch"/>.
    /// </summary>
    private Session Register(Instance instance, Process process, DateTimeOffset startTime, bool attached, RconEndpoint? rcon, int autoRestarts = 0)
    {
        var session = new Session(instance.Id, instance.Slug, process, startTime, attached, rcon, _time.GetUtcNow(), _lifetime);
        _sessions[instance.Id] = session;
        Update(instance.Id, current => new InstanceRuntime(instance.Id, InstanceState.Starting, process.Id, startTime, null, null, null, AutoRestarts: autoRestarts, RecoveryEpoch: current.RecoveryEpoch));
        return session;
    }

    private void StartLoops(Session session)
    {
        session.OutputTask = Task.Run(() => RunOutputAsync(session));
        if (session.Rcon is not null)
        {
            session.ProbeTask = Task.Run(() => RunProbeAsync(session));
        }

        session.LivenessTask = Task.Run(() => RunLivenessAsync(session));
    }

    private async Task RunOutputAsync(Session session)
    {
        var token = session.Cancellation.Token;
        var channel = ConsoleChannels.Instance(session.InstanceId);
        try
        {
            var settings = await _settings.GetAsync(token);
            var logPath = _paths.InstanceLogPath(session.Slug);

            // Attach: follow from the end with backfill. Fresh launch: the previous session's log still exists
            // for ~1.2 s until the game renames it (Spike A), so start at its end rather than replaying it; the
            // rotation reset then reads the new file from offset 0. With no old log, read from the top.
            var startAtEnd = session.IsAttached || File.Exists(logPath);
            var options = new OutputSourceOptions(startAtEnd, session.IsAttached ? settings.ConsoleBackfillLines : 0);
            var source = _outputs.ForLogFile(logPath);
            await source.RunAsync(options, line => OnOutputLine(session, channel, line), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The log tail for instance {InstanceId} stopped.", session.InstanceId);
            Append(channel, $"Log tail stopped: {ex.Message}", ConsoleLineKind.Warning);
        }
    }

    private ValueTask OnOutputLine(Session session, string channel, OutputLine line)
    {
        if (!session.Noise.ShouldShow(line.Text))
        {
            return ValueTask.CompletedTask;
        }

        _console.Append(channel, new ConsoleLine(line.ObservedAt, line.Text, line.IsBackfill ? ConsoleLineKind.Backfill : ConsoleLineKind.Output));
        if (!line.IsBackfill && StartupMarkers.Classify(line.Text) is { } marker)
        {
            UpdateForSession(session, runtime => runtime with { LastMarker = marker });
        }

        return ValueTask.CompletedTask;
    }

    private async Task RunProbeAsync(Session session)
    {
        var token = session.Cancellation.Token;
        var endpoint = session.Rcon!;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var settings = await _settings.GetAsync(token);
                var timeout = TimeSpan.FromSeconds(settings.RconCommandTimeoutSeconds);
                var sentAt = _time.GetUtcNow();
                var reply = await _rcon.ExecuteAsync(endpoint, RconCommands.ListPlayers, timeout, token);
                await OnProbeSucceededAsync(session, token);
                Raise(ProbeObserved, new ProbeObservation(session.InstanceId, session.Pid, session.StartTime, sentAt, reply));
            }
            catch (RconException ex)
            {
                await OnProbeFailedAsync(session, ex, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The RCON probe for instance {InstanceId} threw.", session.InstanceId);
            }

            try
            {
                await Task.Delay(ProbeInterval, _time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task OnProbeSucceededAsync(Session session, CancellationToken token)
    {
        session.ProbeSucceeded = true;
        var now = _time.GetUtcNow();
        var promoted = false;
        var applied = UpdateForSession(session, runtime =>
        {
            promoted = runtime.State is InstanceState.Starting or InstanceState.StartingUnconfirmed or InstanceState.Unreachable;
            return promoted
                ? runtime with { State = InstanceState.Running, Detail = null, LastRconSuccessAt = now }
                : runtime with { LastRconSuccessAt = now };
        });

        if (applied && promoted)
        {
            await MirrorForSessionAsync(session, InstanceState.Running, token);
        }
    }

    private async Task OnProbeFailedAsync(Session session, RconException failure, CancellationToken token)
    {
        _logger.LogDebug(failure, "RCON probe for instance {InstanceId} failed ({Failure}).", session.InstanceId, failure.Failure);
        if (GetRuntime(session.InstanceId).State != InstanceState.Starting || _time.GetUtcNow() - session.RegisteredAt < StartupBound)
        {
            return;
        }

        var minutes = (int)StartupBound.TotalMinutes;
        var (state, detail) = session.IsAttached
            ? (InstanceState.Unreachable, $"RCON has not answered in {minutes} minutes since re-attach ({failure.Failure}: {failure.Message}). Check ServerAdminPassword / RCONPort.")
            : (InstanceState.StartingUnconfirmed, $"No successful RCON probe {minutes} minutes after launch ({failure.Failure}: {failure.Message}); still probing.");
        var applied = UpdateIfForSession(session, runtime => runtime.State == InstanceState.Starting ? runtime with { State = state, Detail = detail } : null);
        if (applied)
        {
            await MirrorForSessionAsync(session, state, token);
        }
    }

    private async Task RunLivenessAsync(Session session)
    {
        var token = session.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            bool exited;
            try
            {
                exited = session.Process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // HasExited itself is inconclusive; ask the process table. Dead is a confirmed exit, Alive keeps
                // supervising, Unknown keeps the session registered and supervised and is logged once (B0).
                var liveness = await ProbeSessionCoreAsync(session);
                exited = liveness == SessionLiveness.Dead;
                if (liveness == SessionLiveness.Unknown && !session.ProbeUnknownLogged)
                {
                    session.ProbeUnknownLogged = true;
                    _logger.LogWarning(ex, "HasExited failed for pid {Pid} and the process table could not settle it; still supervising.", session.Pid);
                }
            }

            if (exited)
            {
                await HandleExitAsync(session);
                return;
            }

            SampleTelemetry(session);

            try
            {
                await Task.Delay(LivenessInterval, _time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One resource reading per liveness tick (B7). The first tick of a session only records the baseline, because a
    /// CPU share needs two <c>TotalProcessorTime</c> readings; every later tick refreshes the baseline over the tick
    /// that just passed and publishes at most every <see cref="TelemetrySampler.PublishInterval"/>. A process that
    /// cannot be read is skipped without a log line: the next tick either reads it or sees the exit.
    /// </summary>
    private void SampleTelemetry(Session session)
    {
        long workingSet;
        TimeSpan processorTime;
        try
        {
            session.Process.Refresh();
            workingSet = session.Process.WorkingSet64;
            processorTime = session.Process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var previousTotal = session.ProcessorTime;
        var previousAt = session.ProcessorTimeAt;
        session.ProcessorTime = processorTime;
        session.ProcessorTimeAt = now;
        if (previousTotal is not { } lastTotal || previousAt is not { } lastAt)
        {
            return;
        }

        if (!TelemetrySampler.ShouldPublish(session.TelemetryPublishedAt, now, TelemetrySampler.PublishInterval))
        {
            return;
        }

        var percent = TelemetrySampler.CpuPercent(lastTotal, processorTime, now - lastAt, Environment.ProcessorCount);
        var sample = new InstanceTelemetry(session.InstanceId, workingSet, percent, now);
        session.TelemetryPublishedAt = now;
        _telemetry[session.InstanceId] = sample;
        RaiseTelemetry(session.InstanceId, sample);
    }

    /// <summary>
    /// The single owner of the "process exited" transition. Ordered so the instance is not launchable until the cleanup
    /// is done (B4): the database write runs while the session is still registered and the runtime still shows a live
    /// state, so a Start is refused until the one atomic update that publishes Stopped, clears the identity, and installs
    /// the <see cref="InstanceRuntime.PendingCrash"/> marker. The stop intent and the recovery epoch are captured under
    /// the session lock first, so a stop is either carried by this exit or, once the exit has begun, dismisses it; a
    /// dismissal issued after the capture always voids the marker. Takes no lease and awaits nothing after the exit signal.
    /// </summary>
    private async Task HandleExitAsync(Session session)
    {
        var instanceId = session.InstanceId;
        bool stopIntent;
        int epoch;
        lock (session.Sync)
        {
            session.Exiting = true;
            stopIntent = session.StopIntent || session.StopRequested;
            epoch = GetRuntime(instanceId).RecoveryEpoch;
            TestHooks.InsideExitCapture?.Invoke(instanceId);
        }

        var channel = ConsoleChannels.Instance(instanceId);
        var exitCode = ReadExitCode(session.Process);
        var codeText = exitCode is { } code ? $" (code {code.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var codeForLog = exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var request = new RecoveryRequest(instanceId, session.Pid, session.StartTime, exitCode, stopIntent, _time.GetUtcNow(), _gate.IsHeldExclusively);
        var detail = stopIntent ? null : $"Exited unexpectedly{codeText} at {request.ExitedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.";

        // Nothing here may skip the publication, the exit signal, or the post below: a failed or cancelled database write
        // leaves only a stale row, while a skipped publication would leave a dead session registered forever.
        try
        {
            // Every session-scoped database write holds this mutex; one in flight lands before the final Stopped, never after.
            await session.WriteMutex.WaitAsync(CancellationToken.None);
            try
            {
                await MirrorStateAsync(instanceId, InstanceState.Stopped, CancellationToken.None, clearIdentity: true);
            }
            finally
            {
                session.WriteMutex.Release();
            }

            if (stopIntent)
            {
                Append(channel, $"Server exited{codeText}.", ConsoleLineKind.Info);
                _logger.LogInformation("Instance {InstanceId} pid {Pid} exited after a manager-initiated stop (code {Code}).", instanceId, session.Pid, codeForLog);
            }
            else
            {
                Append(channel, $"Server exited unexpectedly{codeText}.", ConsoleLineKind.Warning);
                _logger.LogWarning("Instance {InstanceId} pid {Pid} exited without a manager-initiated stop (code {Code}).", instanceId, session.Pid, codeForLog);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The exit cleanup of instance {InstanceId} (pid {Pid}) failed part-way; publishing the exit anyway.", instanceId, session.Pid);
        }

        // The session stays registered until the exit is published, so a stop issued during the cleanup always finds it
        // (and the Exiting flag) rather than an instance that looks live with no session behind it.
        var eligible = !stopIntent && !request.DuringMaintenance;
        PublishExit(instanceId, request, epoch, eligible, detail);
        _sessions.TryRemove(new KeyValuePair<int, Session>(instanceId, session));
        _telemetry.TryRemove(instanceId, out _);
        RaiseTelemetry(instanceId, null);

        session.Cancellation.Cancel();
        session.Process.Dispose();
        session.Exited.TrySetResult();

        // After the cleanup and the exit signal, never before: nothing in this path waits on recovery.
        _recovery.TryPost(request);
    }

    /// <summary>The exit's one publishing update (B4); internal so the epoch race can be exercised directly.</summary>
    internal void PublishExit(int instanceId, RecoveryRequest request, int capturedEpoch, bool eligible, string? detail) =>
        Update(instanceId, runtime => ExitedRuntime(runtime, request, capturedEpoch, eligible, detail));

    /// <summary>
    /// The record an exit publishes (B4): Stopped, no identity, and the marker only for an eligible exit (no stop intent,
    /// no maintenance) whose captured epoch still equals the record's. Compared against the record being replaced, so a
    /// dismissal that bumped the epoch after the capture always wins.
    /// </summary>
    internal static InstanceRuntime ExitedRuntime(InstanceRuntime current, RecoveryRequest request, int capturedEpoch, bool eligible, string? detail) =>
        current with
        {
            State = InstanceState.Stopped,
            Pid = null,
            ProcessStartTime = null,
            Detail = detail,
            ExitRequested = false,
            PendingCrash = eligible && current.RecoveryEpoch == capturedEpoch ? request : null,
        };

    // ---- crash recovery (B4) ----------------------------------------------------------------------

    public async Task<CrashRecovery> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instanceId = request.InstanceId;
        var lease = _locks.TryAcquire(instanceId);
        if (lease is null)
        {
            return CrashRecovery.Busy;
        }

        try
        {
            var runtime = GetRuntime(instanceId);
            if (!ReferenceEquals(runtime.PendingCrash, request) || _sessions.ContainsKey(instanceId) || runtime.State != InstanceState.Stopped)
            {
                return CrashRecovery.Stale;
            }

            bool? autoRestart;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                autoRestart = await db.Instances.AsNoTracking()
                    .Where(i => i.Id == instanceId)
                    .Select(i => (bool?)i.AutoRestart)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            if (autoRestart is null)
            {
                AnswerCrash(request, runtime => runtime);
                return CrashRecovery.Stale;
            }

            if (autoRestart is false)
            {
                return AnswerCrash(request, runtime => runtime) ? CrashRecovery.Disabled : CrashRecovery.Stale;
            }

            var uptime = request.ExitedAt - request.ProcessStartTime;
            if (CrashLoopRule.Next(runtime.AutoRestarts, uptime) is not CrashLoopDecision.Restart restart)
            {
                return AnswerCrash(request, current => current with { State = InstanceState.Crashed, Detail = CrashLoopRule.GaveUpMessage })
                    ? new CrashRecovery(CrashRecoveryStatus.GaveUp, runtime.AutoRestarts)
                    : CrashRecovery.Stale;
            }

            Append(ConsoleChannels.Instance(instanceId), $"Restarting after unexpected exit ({restart.Attempt}/{CrashLoopRule.MaxRestarts}).", ConsoleLineKind.Info);
            _logger.LogInformation("Instance {InstanceId}: automatic restart {Attempt}/{Max} after pid {Pid} exited unexpectedly.", instanceId, restart.Attempt, CrashLoopRule.MaxRestarts, request.Pid);
            var report = new LaunchReport();
            var outcome = await StartCoreAsync(instanceId, LaunchKind.AutoRestart, cancellationToken, restart.Attempt, report);
            if (outcome.Succeeded)
            {
                return new CrashRecovery(CrashRecoveryStatus.Launched, restart.Attempt);
            }

            var reason = outcome.Error ?? "the launch was refused";
            if (report.Registered)
            {
                // A process was registered for this attempt (its identity could not be saved): the relaunch happened, and
                // Register cleared the marker, so nothing is answered here; a later exit of that process posts its own request.
                return new CrashRecovery(CrashRecoveryStatus.Launched, restart.Attempt, reason);
            }

            if (reason == AutoRestartTurnedOff)
            {
                return AnswerCrash(request, current => current)
                    ? CrashRecovery.Disabled
                    : new CrashRecovery(CrashRecoveryStatus.Refused, restart.Attempt, reason);
            }

            // An update, a service shutdown, or a restore (its cluster reservation or an unresolved journal) owns the
            // instance: not a refusal, so the instance stays Stopped.
            if (report.HeldByRestore || reason == MaintenanceGate.UpdateInProgress || _lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return AnswerCrash(request, current => current)
                    ? new CrashRecovery(CrashRecoveryStatus.Skipped, restart.Attempt, reason)
                    : new CrashRecovery(CrashRecoveryStatus.Refused, restart.Attempt, reason);
            }

            AnswerCrash(request, current => current with { State = InstanceState.Crashed, Detail = $"Automatic restart was refused: {reason}" });
            return new CrashRecovery(CrashRecoveryStatus.Refused, restart.Attempt, reason);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Answers <paramref name="request"/> with one conditional update: applied, and the marker cleared, only while the
    /// record's <see cref="InstanceRuntime.PendingCrash"/> is still this very request and no process is live.
    /// </summary>
    private bool AnswerCrash(RecoveryRequest request, Func<InstanceRuntime, InstanceRuntime> answer) =>
        UpdateIf(request.InstanceId, runtime => ReferenceEquals(runtime.PendingCrash, request) && !runtime.HasLiveProcess
            ? answer(runtime) with { PendingCrash = null }
            : null);

    public void DismissCrash(int instanceId) =>
        UpdateIf(instanceId, runtime => runtime with { PendingCrash = null, RecoveryEpoch = runtime.RecoveryEpoch + 1 });

    // ---- session probe (B0) -----------------------------------------------------------------------

    public Task<SessionLiveness> ProbeSessionAsync(int instanceId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(instanceId, out var session))
        {
            return Task.FromResult(SessionLiveness.Unknown);
        }

        return Task.Run(() => ProbeSessionCoreAsync(session), cancellationToken);
    }

    /// <summary>
    /// One targeted process-table read. Alive needs a complete row whose creation date matches the session's start
    /// time within <see cref="ProcessMatcher.StartTimeTolerance"/>; Dead is a completed read with no such row, or a
    /// complete row that belongs to a reused pid; everything else is Unknown.
    /// </summary>
    private async Task<SessionLiveness> ProbeSessionCoreAsync(Session session)
    {
        ProcessRowRead read;
        try
        {
            read = await Task.Run(() => _enumerator.ReadRow(session.Pid));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The process-table read for pid {Pid} failed.", session.Pid);
            return SessionLiveness.Unknown;
        }

        return read.Status switch
        {
            ProcessRowStatus.Missing => SessionLiveness.Dead,
            ProcessRowStatus.Complete when (read.Row!.CreationTime - session.StartTime).Duration() <= ProcessMatcher.StartTimeTolerance => SessionLiveness.Alive,
            ProcessRowStatus.Complete => SessionLiveness.Dead,
            _ => SessionLiveness.Unknown,
        };
    }

    /// <summary>The two-argument twin of <see cref="Raise{T}"/>, for <see cref="TelemetryChanged"/>.</summary>
    private void RaiseTelemetry(int instanceId, InstanceTelemetry? sample)
    {
        try
        {
            TelemetryChanged?.Invoke(instanceId, sample);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A TelemetryChanged subscriber threw.");
        }
    }

    /// <summary>Subscriber exceptions are logged, never propagated into the loop that raised the event.</summary>
    private void Raise<T>(Action<T>? handlers, T payload)
    {
        try
        {
            handlers?.Invoke(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A {Event} subscriber threw.", typeof(T).Name);
        }
    }

    // ---- projection reservation (B0) ---------------------------------------------------------------

    public async Task<ProjectionReservationResult> TryReserveProjectionAsync(CancellationToken cancellationToken)
    {
        // Exclusive first: once held, no launch can enter its shared phase, so a check that finds no session and no
        // process is guaranteed that none is being created.
        var lease = await _queue.Reservation.AcquireExclusiveAsync(cancellationToken);
        string reason;
        try
        {
            if (!_sessions.IsEmpty)
            {
                var ids = string.Join(", ", _sessions.Keys.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));
                reason = $"a session is registered for instance {ids}";
            }
            else
            {
                ProcessTableSnapshot snapshot;
                try
                {
                    snapshot = await Task.Run(() => _enumerator.Snapshot(), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    snapshot = new ProcessTableSnapshot([], false);
                    _logger.LogWarning(ex, "Process enumeration failed while reserving for a projection.");
                }

                if (!snapshot.Complete)
                {
                    reason = "the process table could not be read completely";
                }
                else if (snapshot.Processes.Count > 0)
                {
                    var pids = string.Join(", ", snapshot.Processes.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture)));
                    reason = $"{snapshot.Processes.Count} game process(es) running (pids {pids})";
                }
                else
                {
                    return new ProjectionReservationResult(lease, null);
                }
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        lease.Dispose();
        _logger.LogInformation("Projection deferred: {Reason}.", reason);
        return new ProjectionReservationResult(null, reason);
    }

    // ---- runtime bookkeeping ----------------------------------------------------------------------

    private static InstanceRuntime Default(int instanceId) => new(instanceId, InstanceState.Stopped, null, null, null, null, null);

    private InstanceRuntime Update(int instanceId, Func<InstanceRuntime, InstanceRuntime> update)
    {
        var updated = _runtimes.AddOrUpdate(instanceId, _ => update(Default(instanceId)), (_, current) => update(current));
        RaiseRuntimeChanged(updated);
        return updated;
    }

    /// <summary>
    /// A compare-and-apply update (B4): <paramref name="change"/> sees the current record and returns its replacement,
    /// or null to leave it alone; it is re-run when the record changed underneath it. False when nothing was applied,
    /// including for an instance that has no record yet.
    /// </summary>
    private bool UpdateIf(int instanceId, Func<InstanceRuntime, InstanceRuntime?> change)
    {
        while (_runtimes.TryGetValue(instanceId, out var current))
        {
            if (change(current) is not { } next)
            {
                return false;
            }

            if (_runtimes.TryUpdate(instanceId, next, current))
            {
                RaiseRuntimeChanged(next);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A write on behalf of <paramref name="session"/> (B4): applied only while the record still carries the session's
    /// identity, which the exit's publishing update clears, so a late probe, stop-job, or identity-retry write can never
    /// put a dead instance back into a live state. Reports whether it applied.
    /// </summary>
    private bool UpdateForSession(Session session, Func<InstanceRuntime, InstanceRuntime> change) =>
        UpdateIfForSession(session, change);

    /// <inheritdoc cref="UpdateForSession"/>
    private bool UpdateIfForSession(Session session, Func<InstanceRuntime, InstanceRuntime?> change) =>
        UpdateIf(session.InstanceId, runtime => runtime.Pid == session.Pid && runtime.ProcessStartTime == session.StartTime ? change(runtime) : null);

    /// <summary>The session-scoped twin of <see cref="SetStateAsync"/>: false, with no database write, when the session's process is gone.</summary>
    private async Task<bool> SetSessionStateAsync(Session session, InstanceState state, string? detail, CancellationToken cancellationToken)
    {
        if (!UpdateForSession(session, runtime => runtime with { State = state, Detail = detail }))
        {
            return false;
        }

        await MirrorForSessionAsync(session, state, cancellationToken);
        return true;
    }

    /// <summary>
    /// Mirrors a session-scoped state into the database under the session's write mutex (B4); skipped once the exit has
    /// begun, so every such write lands before the exit's final Stopped and identity clear, never after.
    /// </summary>
    private async Task MirrorForSessionAsync(Session session, InstanceState state, CancellationToken cancellationToken)
    {
        if (TestHooks.BeforeSessionMirror is { } hook)
        {
            await hook(session.InstanceId, state);
        }

        await session.WriteMutex.WaitAsync(cancellationToken);
        try
        {
            if (session.Exiting)
            {
                return;
            }

            await MirrorStateAsync(session.InstanceId, state, cancellationToken);
        }
        finally
        {
            session.WriteMutex.Release();
        }
    }

    /// <summary>A stop job whose process exited on its own after the stop was accepted: the exit owns the cleanup; wait for its signal.</summary>
    private async Task<OperationOutcome> ExitedOnItsOwnAsync(Session session, CancellationToken token) =>
        await WaitForExitAsync(session, ExitVerificationBound, token)
            ? OperationOutcome.Success
            : OperationOutcome.Rejected($"Pid {session.Pid} left the runtime but its exit was not signalled within {ExitVerificationBound.TotalSeconds:0} s.");

    private void RaiseRuntimeChanged(InstanceRuntime runtime)
    {
        try
        {
            RuntimeChanged?.Invoke(runtime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A RuntimeChanged subscriber threw.");
        }
    }

    /// <summary>Sets the in-memory state (plus <paramref name="extra"/> edits) and mirrors the state into the database.</summary>
    private async Task SetStateAsync(int instanceId, InstanceState state, string? detail, CancellationToken cancellationToken, Func<InstanceRuntime, InstanceRuntime>? extra = null)
    {
        Update(instanceId, runtime =>
        {
            var next = runtime with { State = state, Detail = detail };
            return extra is null ? next : extra(next);
        });
        await MirrorStateAsync(instanceId, state, cancellationToken);
    }

    private async Task MirrorStateAsync(int instanceId, InstanceState state, CancellationToken cancellationToken, bool clearIdentity = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = db.Instances.Where(i => i.Id == instanceId);
            if (clearIdentity)
            {
                await rows.ExecuteUpdateAsync(
                    set => set
                        .SetProperty(i => i.State, state)
                        .SetProperty(i => i.LastPid, (int?)null)
                        .SetProperty(i => i.LastProcessStartTime, (DateTimeOffset?)null),
                    cancellationToken);
            }
            else
            {
                await rows.ExecuteUpdateAsync(set => set.SetProperty(i => i.State, state), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not mirror state {State} for instance {InstanceId} into the database.", state, instanceId);
        }
    }

    private void Append(string channel, string text, ConsoleLineKind kind) =>
        _console.Append(channel, new ConsoleLine(_time.GetUtcNow(), text, kind));

    private static DateTimeOffset? ReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static int? ReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string Describe(InstanceState state) => state switch
    {
        InstanceState.Starting => "starting",
        InstanceState.StartingUnconfirmed => "starting (unconfirmed)",
        InstanceState.Running => "running",
        InstanceState.Unreachable => "running (RCON unreachable)",
        InstanceState.Stopping => "stopping",
        InstanceState.IdentityUnpersisted => "running (identity unpersisted)",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>What an automatic restart's launch callback did besides its outcome (B4); written on the queue worker, read after it completes.</summary>
    private sealed class LaunchReport
    {
        /// <summary>A process was registered, so the relaunch happened even if the launch was then rejected.</summary>
        public bool Registered { get; set; }

        /// <summary>A restore's cluster reservation or unresolved journal refused the launch.</summary>
        public bool HeldByRestore { get; set; }
    }

    /// <summary>Everything the loops and the stop job share about one live process.</summary>
    private sealed class Session(int instanceId, string slug, Process process, DateTimeOffset startTime, bool isAttached, RconEndpoint? rcon, DateTimeOffset registeredAt, CancellationToken lifetime)
    {
        public int InstanceId { get; } = instanceId;

        public string Slug { get; } = slug;

        public Process Process { get; } = process;

        public int Pid { get; } = process.Id;

        public DateTimeOffset StartTime { get; } = startTime;

        public bool IsAttached { get; } = isAttached;

        public RconEndpoint? Rcon { get; } = rcon;

        public DateTimeOffset RegisteredAt { get; } = registeredAt;

        /// <summary>Set for launches only; attach leaves <c>LastLaunchedAt</c> untouched.</summary>
        public DateTimeOffset? LaunchedAt { get; set; }

        /// <summary>Cancels the output source and the probe/liveness loops; linked to the host lifetime.</summary>
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        /// <summary>Completed by the liveness loop after the exit bookkeeping is done.</summary>
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True from just before <c>doexit</c> (or kill) so the liveness loop does not report a crash.</summary>
        public volatile bool StopRequested;

        /// <summary>
        /// Set synchronously when a stop is accepted, under the caller's lease and before the job is dispatched (B0), so an
        /// exit observed between acceptance and the job's first line is a requested exit, never a crash. Stays set for
        /// the life of the session, including after a stop that ended without a verified exit.
        /// </summary>
        public volatile bool StopIntent;

        public volatile bool ProbeSucceeded;

        /// <summary>The one-time warning for a HasExited failure the process table could not settle.</summary>
        public volatile bool ProbeUnknownLogged;

        /// <summary>The previous tick's processor-time reading and the moment it was taken; the liveness loop owns both (B7).</summary>
        public TimeSpan? ProcessorTime { get; set; }

        /// <inheritdoc cref="ProcessorTime" />
        public DateTimeOffset? ProcessorTimeAt { get; set; }

        /// <summary>When the last telemetry sample was published, or null until the first one (B7).</summary>
        public DateTimeOffset? TelemetryPublishedAt { get; set; }

        /// <summary>Drops Sentry SDK chatter before it reaches the console; one per log tail.</summary>
        public ConsoleNoiseFilter Noise { get; } = new();

        public CancellationTokenSource? SkipCountdown { get; set; }

        /// <summary>Guards <see cref="Exiting"/> against stop acceptance and the exit's capture of intent and recovery epoch (B4).</summary>
        public object Sync { get; } = new();

        /// <summary>Set under <see cref="Sync"/> as the exit path begins (B4); from then on no stop is accepted and no session-scoped database write is made.</summary>
        public volatile bool Exiting;

        /// <summary>Orders this session's database writes against each other and against the exit cleanup (B4).</summary>
        public SemaphoreSlim WriteMutex { get; } = new(1, 1);

        public Task? OutputTask { get; set; }

        public Task? ProbeTask { get; set; }

        public Task? LivenessTask { get; set; }
    }
}

/// <summary>
/// Pause points inside <see cref="ProcessManager"/> for the B4 race tests, where no substituted fake sits at the point
/// that must be held. Both are null in production.
/// </summary>
internal sealed class ProcessManagerTestHooks
{
    /// <summary>Called inside the session lock of the exit path, right after the intent and the recovery epoch were captured.</summary>
    public Action<int>? InsideExitCapture { get; set; }

    /// <summary>Awaited by a session-scoped database mirror after its runtime update applied and before it takes the session's write mutex.</summary>
    public Func<int, InstanceState, Task>? BeforeSessionMirror { get; set; }
}
