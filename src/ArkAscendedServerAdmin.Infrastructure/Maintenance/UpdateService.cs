using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Maintenance;

/// <summary>
/// Drives <see cref="UpdateStateMachine"/> (plan steps 11, 19, 29): every decision comes from the pure
/// machine, every returned state is persisted to the singleton <see cref="MaintenanceState"/> row before its
/// action runs, and the whole flow — including a resumed one — holds the single maintenance operation lock
/// until the phase is <see cref="MaintenancePhase.None"/> or the flow parks on errored entries. The gate is
/// held exclusively from before the running set is collected until the verified install is in place.
/// <see cref="ResumeAsync"/> runs the stop sweep and SteamCMD to completion but hands pending relaunches to a
/// background task, so readiness never waits for them. <see cref="MaintenanceSnapshot.Detail"/> is in-memory only.
/// </summary>
public sealed class UpdateService(
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    IAppSettingsStore settingsStore,
    IProcessManager processManager,
    IMaintenanceGate gate,
    IGameProcessEnumerator processEnumerator,
    ISteamCmdRunner steamCmd,
    IGameInstallChecker installChecker,
    IConsoleService console,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<UpdateService> logger) : IUpdateService, IMaintenanceRecovery
{
    private static readonly MaintenanceSnapshot _idle = new(MaintenancePhase.None, [], null, null);

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _sync = new();
    private MaintenanceSnapshot _current = _idle;
    private bool _loaded;
    private bool _validateRequested;
    private IDisposable? _gateLease;
    private Task _flow = Task.CompletedTask;

    public MaintenanceSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event Action<MaintenanceSnapshot>? Changed;

    /// <summary>The background flow currently running (a completed task when idle); tests and shutdown await it.</summary>
    public Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _flow;
            }
        }
    }

    /// <summary>True while an update or recovery holds the maintenance operation lock.</summary>
    public bool IsOperationInProgress => _operationLock.CurrentCount == 0;

    public async Task<OperationOutcome> StartUpdateAsync(bool confirmStopRunningInstances, bool validate, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        var precheck = UpdateStateMachine.Begin(current, IsOperationInProgress, processManager.GetAllRuntimes(), confirmStopRunningInstances, timeProvider.GetUtcNow());
        if (precheck.Action.Kind == UpdateActionKind.Refuse)
        {
            return OperationOutcome.Rejected(precheck.Action.Reason!);
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return OperationOutcome.Rejected(UpdateStateMachine.AlreadyRunningReason);
        }

        try
        {
            // The gate goes first (plan step 19): it drains the launch queue and waits for in-flight launches, so
            // the running set collected afterward is complete.
            _gateLease = await gate.AcquireExclusiveAsync(cancellationToken);
            var transition = UpdateStateMachine.Begin(Current, false, processManager.GetAllRuntimes(), confirmStopRunningInstances, timeProvider.GetUtcNow());
            if (transition.Action.Kind == UpdateActionKind.Refuse)
            {
                ReleaseGate();
                _operationLock.Release();
                return OperationOutcome.Rejected(transition.Action.Reason!);
            }

            // In-memory only: a flow resumed after a service restart falls back to the SteamCmdValidate setting.
            _validateRequested = validate;
            await PersistAsync(transition.State, cancellationToken);
            // Each owner-initiated run starts with an empty SteamCMD console so its output is not mixed with the last one's.
            console.Clear(ConsoleChannels.SteamCmd);
            Announce($"Update started; stopping {transition.State.Entries.Count} instance(s).");
            StartBackgroundFlow();
            return OperationOutcome.Success;
        }
        catch
        {
            ReleaseGate();
            _operationLock.Release();
            throw;
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        if (current.Phase is MaintenancePhase.None or MaintenancePhase.Installing)
        {
            return; // nothing to resume; a first-run install is the startup pipeline's own job
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            Announce($"Resuming interrupted update from the {current.Phase} phase.");
            if (current.Phase is MaintenancePhase.Stopping or MaintenancePhase.Updating)
            {
                _gateLease = await gate.AcquireExclusiveAsync(cancellationToken);
            }

            var parked = await DriveAsync(returnWhenRestarting: true, cancellationToken);
            if (parked.Phase == MaintenancePhase.Restarting)
            {
                StartBackgroundFlow(); // hands the relaunches off; readiness does not wait for them
                return;
            }

            ReleaseGate();
            _operationLock.Release();
        }
        catch
        {
            ReleaseGate();
            _operationLock.Release();
            throw;
        }
    }

    public Task<OperationOutcome> RetryEntryAsync(int instanceId, CancellationToken cancellationToken) =>
        ResolveEntryAsync(current => UpdateStateMachine.Retry(current, instanceId), cancellationToken);

    public Task<OperationOutcome> SkipEntryAsync(int instanceId, CancellationToken cancellationToken) =>
        ResolveEntryAsync(current => UpdateStateMachine.Skip(current, instanceId), cancellationToken);

    private async Task<OperationOutcome> ResolveEntryAsync(Func<MaintenanceSnapshot, UpdateTransition> decide, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        var preview = decide(current);
        if (preview.Action.Kind == UpdateActionKind.Refuse)
        {
            return OperationOutcome.Rejected(preview.Action.Reason!);
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return OperationOutcome.Rejected("An update operation is in progress; try again when it finishes.");
        }

        try
        {
            var transition = decide(Current);
            if (transition.Action.Kind == UpdateActionKind.Refuse)
            {
                _operationLock.Release();
                return OperationOutcome.Rejected(transition.Action.Reason!);
            }

            await PersistAsync(transition.State, cancellationToken);
            StartBackgroundFlow();
            return OperationOutcome.Success;
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    /// <summary>Runs <see cref="DriveAsync"/> on the thread pool; releases the gate and the operation lock when it ends.</summary>
    private void StartBackgroundFlow()
    {
        var token = lifetime.ApplicationStopping;
        var flow = Task.Run(async () =>
        {
            try
            {
                await DriveAsync(returnWhenRestarting: false, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                logger.LogInformation("Update flow interrupted by shutdown; it resumes from the persisted phase at the next start.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Update flow failed.");
                SetDetail($"Update flow failed: {ex.Message}. It resumes from the persisted phase at the next service start.");
                Announce($"Update flow failed: {ex.Message}", ConsoleLineKind.Error);
            }
            finally
            {
                ReleaseGate();
                _operationLock.Release();
            }
        }, CancellationToken.None);

        lock (_sync)
        {
            _flow = flow;
        }
    }

    /// <summary>
    /// The driver loop: observe → decide → persist → execute, until the machine returns a terminal action.
    /// With <paramref name="returnWhenRestarting"/> it returns as soon as a <see cref="MaintenancePhase.Restarting"/>
    /// state is persisted so the caller can hand the launches off. The gate is released the moment the persisted
    /// phase leaves <see cref="MaintenancePhase.Updating"/>.
    /// </summary>
    private async Task<MaintenanceSnapshot> DriveAsync(bool returnWhenRestarting, CancellationToken cancellationToken)
    {
        var results = new UpdateInput();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Observe(results);
            var transition = UpdateStateMachine.Next(Current, input);
            await PersistAsync(transition.State, cancellationToken);
            results = new UpdateInput();

            if (transition.State.Phase is MaintenancePhase.Restarting or MaintenancePhase.None)
            {
                ReleaseGate();
            }

            var action = transition.Action;
            if (action.Kind == UpdateActionKind.Refuse)
            {
                Announce($"Update stopped: {action.Reason}", ConsoleLineKind.Error);
                return transition.State;
            }

            if (action.Kind == UpdateActionKind.Complete)
            {
                Announce("Update complete; every instance has been relaunched or resolved.");
                return transition.State;
            }

            if (action.Kind == UpdateActionKind.None)
            {
                if (transition.State.Detail is { } detail)
                {
                    Announce(detail, ConsoleLineKind.Warning);
                }

                return transition.State;
            }

            if (returnWhenRestarting && transition.State.Phase == MaintenancePhase.Restarting)
            {
                return transition.State;
            }

            switch (action.Kind)
            {
                case UpdateActionKind.MarkDone:
                    Announce($"Resolved without action: {Describe(action.InstanceIds)}.");
                    break;
                case UpdateActionKind.StopInstances:
                    results = new UpdateInput { StopResults = await StopAllAsync(action.InstanceIds, cancellationToken) };
                    break;
                case UpdateActionKind.RunSteamCmd:
                    results = new UpdateInput { Install = await RunSteamCmdAsync(cancellationToken) };
                    break;
                case UpdateActionKind.Launch:
                    results = new UpdateInput { LaunchResults = await LaunchAllAsync(action.InstanceIds, cancellationToken) };
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled update action {action.Kind}.");
            }
        }
    }

    private UpdateInput Observe(UpdateInput results)
    {
        var observed = UpdateInput.Observe(processManager.GetAllRuntimes());
        var input = observed with
        {
            StopResults = results.StopResults,
            Install = results.Install,
            LaunchResults = results.LaunchResults,
        };

        return Current.Phase == MaintenancePhase.Stopping ? input with { ForeignProcesses = FindProcessesUnderDataRoot() } : input;
    }

    /// <summary>The safety invariant before SteamCMD (plan step 29): no <c>ArkAscendedServer.exe</c> under <c>DataRoot</c>.</summary>
    private List<string> FindProcessesUnderDataRoot()
    {
        var root = layout.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return processEnumerator.Enumerate()
            .Where(p => (p.ExecutablePath is { } path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                || (p.ExecutablePath is null && p.CommandLine is { } commandLine && commandLine.Contains(root, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.ExecutablePath ?? "<path withheld>"} (PID {p.Pid})")
            .ToList();
    }

    private async Task<Dictionary<int, OperationOutcome>> StopAllAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken)
    {
        if (instanceIds.Count > 0)
        {
            Announce($"Stopping {Describe(instanceIds)} with verified exit.");
        }

        var stops = instanceIds.Select(async id =>
        {
            try
            {
                return (id, await processManager.StopAsync(id, new StopOptions(RequireVerifiedExit: true), cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stopping instance {InstanceId} for the update threw.", id);
                return (id, OperationOutcome.Rejected(ex.Message));
            }
        });

        var outcomes = await Task.WhenAll(stops);
        return outcomes.ToDictionary(o => o.Item1, o => o.Item2);
    }

    private async Task<InstallResult> RunSteamCmdAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var validate = settings.SteamCmdValidate || _validateRequested;
        _validateRequested = false;
        var before = installChecker.Check().BuildId;
        Announce($"Running SteamCMD app_update {DataRootLayout.ServerAppId}{(validate ? " validate" : string.Empty)}{(before is null ? string.Empty : $"; installed build {before}")}.");
        var result = await steamCmd.InstallOrUpdateAsync(validate, cancellationToken);
        if (!result.Succeeded)
        {
            return InstallResult.Failure(result.Error ?? $"SteamCMD exited with code {result.ExitCode}.");
        }

        var status = installChecker.Check();
        if (!status.IsComplete)
        {
            return InstallResult.Failure(status.Detail);
        }

        var outcome = new UpdateResult(timeProvider.GetUtcNow(), before, status.BuildId, validate);
        Announce(outcome.Summary);
        MaintenanceSnapshot snapshot;
        lock (_sync)
        {
            _current = _current with { LastResult = outcome };
            snapshot = _current;
        }

        Changed?.Invoke(snapshot);
        return InstallResult.Success;
    }

    private async Task<Dictionary<int, OperationOutcome>> LaunchAllAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken)
    {
        // Sequential on purpose: each entry is persisted done (or errored) as soon as its own launch has started and
        // its identity is persisted, so a restart in the middle relaunches only what has not been launched.
        var outcomes = new Dictionary<int, OperationOutcome>();
        foreach (var id in instanceIds)
        {
            Announce($"Relaunching instance #{id}.");
            OperationOutcome outcome;
            try
            {
                outcome = await processManager.StartAsync(id, LaunchKind.Recovery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Relaunching instance {InstanceId} after the update threw.", id);
                outcome = OperationOutcome.Rejected(ex.Message);
            }

            outcomes[id] = outcome;
            if (!outcome.Succeeded)
            {
                Announce($"Instance #{id} did not relaunch: {outcome.Error}", ConsoleLineKind.Error);
            }

            // Persist this entry's result right away (the launch action for the remaining ids stays in flight here);
            // the completing transition is left to the driver loop so it is announced and observed once.
            var folded = UpdateStateMachine.Next(Current, new UpdateInput { LaunchResults = new Dictionary<int, OperationOutcome> { [id] = outcome } });
            if (folded.State.Phase == MaintenancePhase.Restarting)
            {
                await PersistAsync(folded.State with { Detail = null }, cancellationToken);
            }
        }

        return outcomes;
    }

    private async Task<MaintenanceSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return _current;
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.MaintenanceStates.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MaintenanceState.SingletonId, cancellationToken);
        var snapshot = row is null ? _idle : new MaintenanceSnapshot(row.Phase, [.. row.Entries], row.StartedAt, null);

        lock (_sync)
        {
            if (!_loaded)
            {
                _current = snapshot;
                _loaded = true;
            }

            return _current;
        }
    }

    private async Task PersistAsync(MaintenanceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var row = await db.MaintenanceStates.SingleOrDefaultAsync(s => s.Id == MaintenanceState.SingletonId, cancellationToken);
            if (row is null)
            {
                row = new MaintenanceState();
                db.MaintenanceStates.Add(row);
            }

            row.Phase = snapshot.Phase;
            row.Entries = [.. snapshot.Entries];
            row.StartedAt = snapshot.Phase == MaintenancePhase.None ? null : snapshot.StartedAt;
            row.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        lock (_sync)
        {
            _current = snapshot;
            _loaded = true;
        }

        Changed?.Invoke(snapshot);
    }

    private void SetDetail(string detail)
    {
        MaintenanceSnapshot snapshot;
        lock (_sync)
        {
            _current = _current with { Detail = detail };
            snapshot = _current;
        }

        Changed?.Invoke(snapshot);
    }

    private void ReleaseGate()
    {
        var lease = Interlocked.Exchange(ref _gateLease, null);
        lease?.Dispose();
    }

    private void Announce(string text, ConsoleLineKind kind = ConsoleLineKind.Info)
    {
        logger.Log(kind == ConsoleLineKind.Error ? LogLevel.Error : LogLevel.Information, "{Message}", text);
        console.Append(ConsoleChannels.SteamCmd, new ConsoleLine(timeProvider.GetUtcNow(), text, kind));
    }

    private static string Describe(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"#{id}"));
}
