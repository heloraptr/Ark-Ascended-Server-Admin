using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Maintenance;

/// <summary>What the update driver does next (plan step 29). Terminal kinds end the driver's loop.</summary>
public enum UpdateActionKind
{
    /// <summary>Nothing to execute; the flow is parked (errored entries wait for Retry/Skip) or there is no flow.</summary>
    None,
    /// <summary>The flow cannot proceed; <see cref="UpdateAction.Reason"/> is shown to the owner.</summary>
    Refuse,
    /// <summary>Stop the listed instances with verified exit.</summary>
    StopInstances,
    /// <summary>The listed entries were resolved without an action (dead before the stop, alive before the launch); persist and continue.</summary>
    MarkDone,
    RunSteamCmd,
    /// <summary>Launch the listed instances through the recovery path.</summary>
    Launch,
    /// <summary>Every entry is done; the phase is <see cref="MaintenancePhase.None"/> and the operation lock can be released.</summary>
    Complete,
}

/// <summary>One decision of <see cref="UpdateStateMachine"/>.</summary>
/// <param name="Kind">What to execute.</param>
/// <param name="InstanceIds">The instances the action applies to (empty for the kinds that take none).</param>
/// <param name="Reason">The refusal reason for <see cref="UpdateActionKind.Refuse"/>.</param>
public sealed record UpdateAction(UpdateActionKind Kind, IReadOnlyList<int> InstanceIds, string? Reason = null)
{
    public static readonly UpdateAction None = new(UpdateActionKind.None, []);

    public static readonly UpdateAction RunSteamCmd = new(UpdateActionKind.RunSteamCmd, []);

    public static readonly UpdateAction Complete = new(UpdateActionKind.Complete, []);

    public static UpdateAction Refuse(string reason) => new(UpdateActionKind.Refuse, [], reason);

    public static UpdateAction StopInstances(IEnumerable<int> instanceIds) => new(UpdateActionKind.StopInstances, [.. instanceIds]);

    public static UpdateAction MarkDone(IEnumerable<int> instanceIds) => new(UpdateActionKind.MarkDone, [.. instanceIds]);

    public static UpdateAction Launch(IEnumerable<int> instanceIds) => new(UpdateActionKind.Launch, [.. instanceIds]);

    /// <summary>True for the kinds after which the driver stops looping.</summary>
    public bool IsTerminal => Kind is UpdateActionKind.None or UpdateActionKind.Refuse or UpdateActionKind.Complete;

    public bool Equals(UpdateAction? other) =>
        other is not null && Kind == other.Kind && Reason == other.Reason && InstanceIds.SequenceEqual(other.InstanceIds);

    public override int GetHashCode() => HashCode.Combine(Kind, Reason, InstanceIds.Count);
}

/// <summary>The next persisted state and the action the driver executes before asking again.</summary>
/// <param name="State">The state to persist (identical to the input when nothing changed).</param>
/// <param name="Action">What to do next.</param>
public sealed record UpdateTransition(MaintenanceSnapshot State, UpdateAction Action);

/// <summary>
/// What the driver observed since the last decision: the live process picture plus the results of the
/// action it just executed. Results are folded into the state by <see cref="UpdateStateMachine.Next"/>.
/// </summary>
public sealed record UpdateInput
{
    /// <summary>Instances whose runtime <see cref="InstanceRuntime.HasLiveProcess"/>.</summary>
    public IReadOnlySet<int> LiveInstances { get; init; } = new HashSet<int>();

    /// <summary>Instances in <see cref="InstanceState.Unknown"/> (ambiguous reconciliation).</summary>
    public IReadOnlySet<int> UnknownInstances { get; init; } = new HashSet<int>();

    /// <summary>Executables of <c>ArkAscendedServer.exe</c> processes found under <c>DataRoot</c> by the safety check.</summary>
    public IReadOnlyList<string> ForeignProcesses { get; init; } = [];

    /// <summary>Per-instance outcome of the last <see cref="UpdateActionKind.StopInstances"/>.</summary>
    public IReadOnlyDictionary<int, OperationOutcome> StopResults { get; init; } = new Dictionary<int, OperationOutcome>();

    /// <summary>Outcome of the last <see cref="UpdateActionKind.RunSteamCmd"/> including manifest verification; null when it has not run.</summary>
    public InstallResult? Install { get; init; }

    /// <summary>Per-instance outcome of the last <see cref="UpdateActionKind.Launch"/>.</summary>
    public IReadOnlyDictionary<int, OperationOutcome> LaunchResults { get; init; } = new Dictionary<int, OperationOutcome>();

    /// <summary>Builds the live picture from the process manager's runtimes.</summary>
    public static UpdateInput Observe(IEnumerable<InstanceRuntime> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        var list = runtimes.ToList();
        return new UpdateInput
        {
            LiveInstances = list.Where(r => r.HasLiveProcess).Select(r => r.InstanceId).ToHashSet(),
            UnknownInstances = list.Where(r => r.State == InstanceState.Unknown).Select(r => r.InstanceId).ToHashSet(),
        };
    }
}

/// <summary>
/// The update flow as pure functions (plan step 29; the step 33 tests are its specification). The driver
/// persists every returned state before executing the action, so a service restart at any point resumes
/// from a state this machine produced: <see cref="Next"/> never stops an entry marked <c>done</c> and never
/// launches an instance that already has a live process, so nothing is double-stopped or double-launched.
/// </summary>
public static class UpdateStateMachine
{
    public const string AlreadyRunningReason = "An update is already in progress.";

    public const string AmbiguousInstancesReason = "Resolve ambiguous instances first: reconciliation found more than one process for at least one instance.";

    /// <summary>Decides whether an update may start and, if so, produces the <see cref="MaintenancePhase.Stopping"/> state.</summary>
    /// <param name="current">The persisted state.</param>
    /// <param name="operationLockHeld">True while another update or recovery holds the maintenance operation lock.</param>
    /// <param name="runtimes">Every instance's runtime as reported by the process manager.</param>
    /// <param name="confirmStopRunningInstances">The owner confirmed that running instances may be stopped.</param>
    /// <param name="now">Becomes <see cref="MaintenanceSnapshot.StartedAt"/>.</param>
    public static UpdateTransition Begin(
        MaintenanceSnapshot current,
        bool operationLockHeld,
        IReadOnlyList<InstanceRuntime> runtimes,
        bool confirmStopRunningInstances,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(runtimes);

        if (operationLockHeld)
        {
            return new UpdateTransition(current, UpdateAction.Refuse(AlreadyRunningReason));
        }

        if (current.Phase != MaintenancePhase.None)
        {
            return new UpdateTransition(current, UpdateAction.Refuse(
                $"The previous {current.Phase.ToString().ToLowerInvariant()} phase is unresolved; resolve the recovery on the Dashboard first."));
        }

        if (runtimes.Any(r => r.State == InstanceState.Unknown))
        {
            return new UpdateTransition(current, UpdateAction.Refuse(AmbiguousInstancesReason));
        }

        var unresolvedIdentity = runtimes.Where(r => r.State == InstanceState.IdentityUnpersisted).Select(r => r.InstanceId).ToList();
        if (unresolvedIdentity.Count > 0)
        {
            return new UpdateTransition(current, UpdateAction.Refuse(
                $"Instance identity unresolved for {Describe(unresolvedIdentity)}; retry persisting the identity or stop the instance first."));
        }

        var live = runtimes.Where(r => r.HasLiveProcess).Select(r => r.InstanceId).OrderBy(id => id).ToList();
        if (live.Count > 0 && !confirmStopRunningInstances)
        {
            return new UpdateTransition(current, UpdateAction.Refuse(
                $"{live.Count} instance(s) are running ({Describe(live)}); confirm stopping them to update."));
        }

        var stopping = new MaintenanceSnapshot(
            MaintenancePhase.Stopping,
            live.Select(id => new MaintenanceEntry(id)).ToList(),
            now,
            null);
        return new UpdateTransition(stopping, UpdateAction.StopInstances(live));
    }

    /// <summary>Folds the observed results into the state and decides the next action for the current phase.</summary>
    public static UpdateTransition Next(MaintenanceSnapshot current, UpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);

        return current.Phase switch
        {
            MaintenancePhase.None => new UpdateTransition(current with { Entries = [] }, UpdateAction.None),
            MaintenancePhase.Installing => new UpdateTransition(current, UpdateAction.None),
            MaintenancePhase.Stopping => NextStopping(current, input),
            MaintenancePhase.Updating => NextUpdating(current, input),
            MaintenancePhase.Restarting => NextRestarting(current, input),
            _ => throw new ArgumentOutOfRangeException(nameof(current), current.Phase, "Unknown maintenance phase."),
        };
    }

    /// <summary>Re-enqueues an errored <see cref="MaintenancePhase.Restarting"/> entry (plan step 11 "Retry").</summary>
    public static UpdateTransition Retry(MaintenanceSnapshot current, int instanceId)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Phase != MaintenancePhase.Restarting)
        {
            return new UpdateTransition(current, UpdateAction.Refuse("There is no restart to retry."));
        }

        var entry = current.Entries.FirstOrDefault(e => e.InstanceId == instanceId);
        if (entry is null || entry.Done || entry.Error is null)
        {
            return new UpdateTransition(current, UpdateAction.Refuse($"Instance {instanceId} has no failed restart to retry."));
        }

        var state = current with { Entries = Replace(current.Entries, entry with { Error = null }), Detail = null };
        return new UpdateTransition(state, UpdateAction.Launch([instanceId]));
    }

    /// <summary>Marks an errored <see cref="MaintenancePhase.Restarting"/> entry done without launching it (plan step 11 "Skip").</summary>
    public static UpdateTransition Skip(MaintenanceSnapshot current, int instanceId)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Phase != MaintenancePhase.Restarting)
        {
            return new UpdateTransition(current, UpdateAction.Refuse("There is no restart to skip."));
        }

        var entry = current.Entries.FirstOrDefault(e => e.InstanceId == instanceId);
        if (entry is null || entry.Done || entry.Error is null)
        {
            return new UpdateTransition(current, UpdateAction.Refuse($"Instance {instanceId} has no failed restart to skip."));
        }

        var entries = Replace(current.Entries, entry with { Done = true, Error = null });
        return entries.All(e => e.Done)
            ? new UpdateTransition(Resolved(current), UpdateAction.Complete)
            : new UpdateTransition(current with { Entries = entries, Detail = null }, UpdateAction.MarkDone([instanceId]));
    }

    private static UpdateTransition NextStopping(MaintenanceSnapshot current, UpdateInput input)
    {
        var entries = current.Entries.Select(e =>
            !e.Done && input.StopResults.TryGetValue(e.InstanceId, out var result)
                ? (result.Succeeded ? e with { Done = true, Error = null } : e with { Error = result.Error ?? "stop failed" })
                : e).ToList();

        var failed = entries.Where(e => e.Error is not null && !e.Done).ToList();
        if (failed.Count > 0)
        {
            var reasons = string.Join("; ", failed.Select(e => $"instance {e.InstanceId}: {e.Error}"));
            return Refused(current, $"Could not stop every instance with a verified exit ({reasons}). The stopped instances stay stopped; fix the cause and start the update again.");
        }

        var newlyDone = NewlyDone(current.Entries, entries);
        if (newlyDone.Count > 0)
        {
            return new UpdateTransition(current with { Entries = entries }, UpdateAction.MarkDone(newlyDone));
        }

        var pending = entries.Where(e => !e.Done).ToList();
        var dead = pending.Where(e => !input.LiveInstances.Contains(e.InstanceId)).Select(e => e.InstanceId).ToList();
        if (dead.Count > 0)
        {
            var marked = entries.Select(e => dead.Contains(e.InstanceId) ? e with { Done = true } : e).ToList();
            return new UpdateTransition(current with { Entries = marked }, UpdateAction.MarkDone(dead));
        }

        if (pending.Count > 0)
        {
            return new UpdateTransition(current with { Entries = entries }, UpdateAction.StopInstances(pending.Select(e => e.InstanceId)));
        }

        if (input.UnknownInstances.Count > 0)
        {
            return Refused(current, AmbiguousInstancesReason);
        }

        if (input.ForeignProcesses.Count > 0)
        {
            return Refused(current, $"ArkAscendedServer.exe is still running under the data root ({string.Join(", ", input.ForeignProcesses)}); stop it before updating.");
        }

        return new UpdateTransition(current with { Phase = MaintenancePhase.Updating, Entries = entries, Detail = null }, UpdateAction.RunSteamCmd);
    }

    private static UpdateTransition NextUpdating(MaintenanceSnapshot current, UpdateInput input)
    {
        if (input.Install is null)
        {
            return new UpdateTransition(current, UpdateAction.RunSteamCmd);
        }

        if (!input.Install.Succeeded)
        {
            // The phase stays Updating so recovery (or a later resume) runs SteamCMD again; nothing is launched
            // against an unverified install.
            var reason = $"SteamCMD did not produce a verified install: {input.Install.Error ?? "unknown error"}";
            return new UpdateTransition(current with { Detail = reason }, UpdateAction.Refuse(reason));
        }

        var restarting = current with
        {
            Phase = MaintenancePhase.Restarting,
            Entries = current.Entries.Select(e => new MaintenanceEntry(e.InstanceId)).ToList(),
            Detail = null,
        };
        return NextRestarting(restarting, new UpdateInput { LiveInstances = input.LiveInstances, UnknownInstances = input.UnknownInstances });
    }

    private static UpdateTransition NextRestarting(MaintenanceSnapshot current, UpdateInput input)
    {
        var entries = current.Entries.Select(e =>
            !e.Done && input.LaunchResults.TryGetValue(e.InstanceId, out var result)
                ? (result.Succeeded ? e with { Done = true, Error = null } : e with { Error = result.Error ?? "launch failed" })
                : e).ToList();

        var newlyDone = NewlyDone(current.Entries, entries);
        if (newlyDone.Count > 0)
        {
            return new UpdateTransition(current with { Entries = entries, Detail = null }, UpdateAction.MarkDone(newlyDone));
        }

        if (entries.All(e => e.Done))
        {
            return new UpdateTransition(Resolved(current), UpdateAction.Complete);
        }

        var pending = entries.Where(e => !e.Done).ToList();
        var alreadyAlive = pending.Where(e => input.LiveInstances.Contains(e.InstanceId)).Select(e => e.InstanceId).ToList();
        if (alreadyAlive.Count > 0)
        {
            var marked = entries.Select(e => alreadyAlive.Contains(e.InstanceId) ? e with { Done = true, Error = null } : e).ToList();
            return marked.All(e => e.Done)
                ? new UpdateTransition(Resolved(current), UpdateAction.Complete)
                : new UpdateTransition(current with { Entries = marked }, UpdateAction.MarkDone(alreadyAlive));
        }

        var launchable = pending.Where(e => e.Error is null).Select(e => e.InstanceId).ToList();
        if (launchable.Count > 0)
        {
            return new UpdateTransition(current with { Entries = entries }, UpdateAction.Launch(launchable));
        }

        var failures = string.Join("; ", pending.Select(e => $"instance {e.InstanceId}: {e.Error}"));
        return new UpdateTransition(
            current with { Entries = entries, Detail = $"Update recovery incomplete: {failures}. Retry or skip each instance." },
            UpdateAction.None);
    }

    private static UpdateTransition Refused(MaintenanceSnapshot current, string reason) =>
        new(current with { Phase = MaintenancePhase.None, Entries = [], Detail = reason }, UpdateAction.Refuse(reason));

    private static MaintenanceSnapshot Resolved(MaintenanceSnapshot current) =>
        current with { Phase = MaintenancePhase.None, Entries = [], Detail = null };

    /// <summary>Entries that a folded result has just marked done; they are persisted as their own transition.</summary>
    private static List<int> NewlyDone(IReadOnlyList<MaintenanceEntry> before, IReadOnlyList<MaintenanceEntry> after)
    {
        var wasDone = before.Where(e => e.Done).Select(e => e.InstanceId).ToHashSet();
        return after.Where(e => e.Done && !wasDone.Contains(e.InstanceId)).Select(e => e.InstanceId).ToList();
    }

    private static List<MaintenanceEntry> Replace(IReadOnlyList<MaintenanceEntry> entries, MaintenanceEntry replacement) =>
        entries.Select(e => e.InstanceId == replacement.InstanceId ? replacement : e).ToList();

    private static string Describe(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"#{id}"));
}
