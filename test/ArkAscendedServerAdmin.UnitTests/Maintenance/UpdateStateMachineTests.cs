using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Maintenance;

/// <summary>Plan step 33: every persisted phase × every recovery input, plus the start/refuse rules and Retry/Skip.</summary>
public class UpdateStateMachineTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly MaintenanceSnapshot _idle = new(MaintenancePhase.None, [], null, null);

    // ---------------------------------------------------------------- Begin

    [Fact]
    public void Begin_WithNothingRunning_EntersStoppingWithNoEntries()
    {
        var t = UpdateStateMachine.Begin(_idle, false, [Runtime(1, InstanceState.Stopped)], false, _now);

        Assert.Equal(MaintenancePhase.Stopping, t.State.Phase);
        Assert.Empty(t.State.Entries);
        Assert.Equal(_now, t.State.StartedAt);
        Assert.Equal(UpdateAction.StopInstances([]), t.Action);
    }

    [Theory]
    [InlineData(InstanceState.Running)]
    [InlineData(InstanceState.Starting)]
    [InlineData(InstanceState.StartingUnconfirmed)]
    [InlineData(InstanceState.Unreachable)]
    [InlineData(InstanceState.Stopping)]
    public void Begin_WithLiveInstanceAndNoConfirmation_Refuses(InstanceState state)
    {
        var t = UpdateStateMachine.Begin(_idle, false, [Runtime(1, state)], false, _now);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("confirm", t.Action.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_idle, t.State);
    }

    [Fact]
    public void Begin_WithConfirmation_ListsEveryLiveInstance()
    {
        var runtimes = new[] { Runtime(1, InstanceState.Running), Runtime(2, InstanceState.Stopped), Runtime(3, InstanceState.Unreachable), Runtime(4, InstanceState.StartingUnconfirmed) };

        var t = UpdateStateMachine.Begin(_idle, false, runtimes, true, _now);

        Assert.Equal(MaintenancePhase.Stopping, t.State.Phase);
        Assert.Equal([new MaintenanceEntry(1), new MaintenanceEntry(3), new MaintenanceEntry(4)], t.State.Entries);
        Assert.Equal(UpdateAction.StopInstances([1, 3, 4]), t.Action);
    }

    [Fact]
    public void Begin_WithUnknownInstance_RefusesEvenWhenConfirmed()
    {
        var t = UpdateStateMachine.Begin(_idle, false, [Runtime(1, InstanceState.Unknown)], true, _now);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Equal(UpdateStateMachine.AmbiguousInstancesReason, t.Action.Reason);
    }

    [Fact]
    public void Begin_WithIdentityUnpersistedInstance_Refuses()
    {
        var t = UpdateStateMachine.Begin(_idle, false, [Runtime(7, InstanceState.IdentityUnpersisted)], true, _now);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("identity unresolved", t.Action.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Begin_WhileOperationLockHeld_Refuses()
    {
        var t = UpdateStateMachine.Begin(_idle, true, [], true, _now);

        Assert.Equal(UpdateAction.Refuse(UpdateStateMachine.AlreadyRunningReason), t.Action);
    }

    [Theory]
    [InlineData(MaintenancePhase.Installing)]
    [InlineData(MaintenancePhase.Stopping)]
    [InlineData(MaintenancePhase.Updating)]
    [InlineData(MaintenancePhase.Restarting)]
    public void Begin_WhilePersistedPhaseIsUnresolved_Refuses(MaintenancePhase phase)
    {
        var current = new MaintenanceSnapshot(phase, [new MaintenanceEntry(1, Error: "boom")], _now, null);

        var t = UpdateStateMachine.Begin(current, false, [], true, _now);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("unresolved", t.Action.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Same(current, t.State);
    }

    [Fact]
    public void Begin_SecondUpdateWhileRestartingHasUnresolvedEntries_IsRejected()
    {
        var restarting = new MaintenanceSnapshot(MaintenancePhase.Restarting, [new MaintenanceEntry(1, Done: true), new MaintenanceEntry(2, Error: "port conflict")], _now, null);

        var t = UpdateStateMachine.Begin(restarting, false, [Runtime(1, InstanceState.Running)], true, _now);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
    }

    // ---------------------------------------------------------------- None / Installing

    [Fact]
    public void Next_InNone_DoesNothingAndClearsStrayEntries()
    {
        var stray = new MaintenanceSnapshot(MaintenancePhase.None, [new MaintenanceEntry(1)], null, "old");

        var t = UpdateStateMachine.Next(stray, new UpdateInput());

        Assert.Equal(UpdateAction.None, t.Action);
        Assert.Empty(t.State.Entries);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
    }

    [Fact]
    public void Next_InInstalling_IsLeftToTheInstaller()
    {
        var installing = new MaintenanceSnapshot(MaintenancePhase.Installing, [], _now, null);

        var t = UpdateStateMachine.Next(installing, new UpdateInput { LiveInstances = Set(1) });

        Assert.Equal(UpdateAction.None, t.Action);
        Assert.Same(installing, t.State);
    }

    // ---------------------------------------------------------------- Stopping

    public static TheoryData<string, MaintenanceEntry[], int[], UpdateAction, MaintenanceEntry[]> StoppingCases => new()
    {
        {
            "interrupted before any stop, both alive: stop both",
            [E(1), E(2)], [1, 2],
            UpdateAction.StopInstances([1, 2]),
            [E(1), E(2)]
        },
        {
            "interrupted before any stop, one already dead: mark it done first (never stop a dead instance)",
            [E(1), E(2)], [2],
            UpdateAction.MarkDone([1]),
            [E(1, done: true), E(2)]
        },
        {
            "interrupted after one stop persisted done, it is alive again: never double-stop, stop only the pending one",
            [E(1, done: true), E(2)], [1, 2],
            UpdateAction.StopInstances([2]),
            [E(1, done: true), E(2)]
        },
        {
            "interrupted after a stop completed but before done was persisted: the dead entry resolves without a stop",
            [E(1), E(2, done: true)], [],
            UpdateAction.MarkDone([1]),
            [E(1, done: true), E(2, done: true)]
        },
        {
            "all done and the safety check is clear: run SteamCMD",
            [E(1, done: true), E(2, done: true)], [],
            UpdateAction.RunSteamCmd,
            [E(1, done: true), E(2, done: true)]
        },
        {
            "empty list (nothing was running): straight to SteamCMD",
            [], [],
            UpdateAction.RunSteamCmd,
            []
        },
    };

    [Theory]
    [MemberData(nameof(StoppingCases))]
    public void Next_InStopping_RecoversWithoutDoubleStops(string scenario, MaintenanceEntry[] entries, int[] alive, UpdateAction expectedAction, MaintenanceEntry[] expectedEntries)
    {
        Assert.NotEmpty(scenario);
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, entries, _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { LiveInstances = Set(alive) });

        Assert.Equal(expectedAction, t.Action);
        Assert.Equal(expectedEntries, t.State.Entries);
        Assert.Equal(expectedAction.Kind == UpdateActionKind.RunSteamCmd ? MaintenancePhase.Updating : MaintenancePhase.Stopping, t.State.Phase);
    }

    [Fact]
    public void Next_InStopping_FoldsVerifiedExitsIntoDone()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, [E(1), E(2)], _now, null);
        var input = new UpdateInput
        {
            LiveInstances = Set(2),
            StopResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Success },
        };

        var t = UpdateStateMachine.Next(current, input);

        Assert.Equal(UpdateAction.MarkDone([1]), t.Action);
        Assert.Equal([E(1, done: true), E(2)], t.State.Entries);
        Assert.Equal(MaintenancePhase.Stopping, t.State.Phase);

        var next = UpdateStateMachine.Next(t.State, new UpdateInput { LiveInstances = Set(2) });

        Assert.Equal(UpdateAction.StopInstances([2]), next.Action);
    }

    [Fact]
    public void Next_InStopping_StopFailureRefusesAndReturnsToNone()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, [E(1), E(2)], _now, null);
        var input = new UpdateInput
        {
            LiveInstances = Set(1),
            StopResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Rejected("exit not verified"), [2] = OperationOutcome.Success },
        };

        var t = UpdateStateMachine.Next(current, input);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("exit not verified", t.Action.Reason, StringComparison.Ordinal);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
        Assert.Empty(t.State.Entries);
        Assert.Equal(t.Action.Reason, t.State.Detail);
    }

    [Fact]
    public void Next_InStopping_LiveProcessUnderDataRootBlocksUpdating()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, [E(1, done: true)], _now, null);
        var input = new UpdateInput { ForeignProcesses = [@"D:\Ark\Instances\alpha\ShooterGame\Binaries\Win64\ArkAscendedServer.exe (PID 4242)"] };

        var t = UpdateStateMachine.Next(current, input);

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("PID 4242", t.Action.Reason, StringComparison.Ordinal);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
    }

    [Fact]
    public void Next_InStopping_UnknownInstanceBlocksUpdating()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, [E(1, done: true)], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { UnknownInstances = Set(9) });

        Assert.Equal(UpdateAction.Refuse(UpdateStateMachine.AmbiguousInstancesReason), t.Action);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
    }

    [Fact]
    public void Next_InStopping_ForeignProcessesAreIgnoredUntilEveryEntryIsDone()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Stopping, [E(1)], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { LiveInstances = Set(1), ForeignProcesses = ["x.exe (PID 1)"] });

        Assert.Equal(UpdateAction.StopInstances([1]), t.Action);
    }

    // ---------------------------------------------------------------- Updating

    [Fact]
    public void Next_InUpdating_WithoutAnInstallResult_RunsSteamCmd()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Updating, [E(1, done: true)], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput());

        Assert.Equal(UpdateAction.RunSteamCmd, t.Action);
        Assert.Same(current, t.State);
    }

    [Fact]
    public void Next_InUpdating_FailedInstall_StaysUpdatingSoRecoveryRerunsSteamCmd()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Updating, [E(1, done: true)], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { Install = InstallResult.Failure("StateFlags 1026") });

        Assert.Equal(UpdateActionKind.Refuse, t.Action.Kind);
        Assert.Contains("StateFlags 1026", t.Action.Reason, StringComparison.Ordinal);
        Assert.Equal(MaintenancePhase.Updating, t.State.Phase);
        Assert.Equal([E(1, done: true)], t.State.Entries);
        Assert.Equal(t.Action.Reason, t.State.Detail);
    }

    [Fact]
    public void Next_InUpdating_VerifiedInstall_EntersRestartingWithDoneResetAndLaunchesEverything()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Updating, [E(1, done: true), E(2, done: true)], _now, "old detail");

        var t = UpdateStateMachine.Next(current, new UpdateInput { Install = InstallResult.Success });

        Assert.Equal(MaintenancePhase.Restarting, t.State.Phase);
        Assert.Equal([E(1), E(2)], t.State.Entries);
        Assert.Null(t.State.Detail);
        Assert.Equal(UpdateAction.Launch([1, 2]), t.Action);
    }

    [Fact]
    public void Next_InUpdating_VerifiedInstallWithNoEntries_Completes()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Updating, [], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { Install = InstallResult.Success });

        Assert.Equal(UpdateAction.Complete, t.Action);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
    }

    // ---------------------------------------------------------------- Restarting

    public static TheoryData<string, MaintenanceEntry[], int[], UpdateAction, MaintenanceEntry[], MaintenancePhase> RestartingCases => new()
    {
        {
            "interrupted before enqueue, nothing alive: launch every pending entry",
            [E(1), E(2)], [],
            UpdateAction.Launch([1, 2]),
            [E(1), E(2)], MaintenancePhase.Restarting
        },
        {
            "interrupted after Process.Start (alive) but before done was persisted: mark done, never double-launch",
            [E(1), E(2)], [1],
            UpdateAction.MarkDone([1]),
            [E(1, done: true), E(2)], MaintenancePhase.Restarting
        },
        {
            "interrupted after identity persistence and done: only the pending entry is launched",
            [E(1, done: true), E(2)], [1],
            UpdateAction.Launch([2]),
            [E(1, done: true), E(2)], MaintenancePhase.Restarting
        },
        {
            "interrupted after done and the instance died since: done entries are never relaunched",
            [E(1, done: true), E(2)], [],
            UpdateAction.Launch([2]),
            [E(1, done: true), E(2)], MaintenancePhase.Restarting
        },
        {
            "every pending entry is already alive: complete without launching",
            [E(1, done: true), E(2)], [2],
            UpdateAction.Complete,
            [], MaintenancePhase.None
        },
        {
            "all done: complete",
            [E(1, done: true), E(2, done: true)], [],
            UpdateAction.Complete,
            [], MaintenancePhase.None
        },
        {
            "errored entry not alive: park until Retry/Skip; the healthy pending entry still launches",
            [E(1, error: "port conflict"), E(2)], [],
            UpdateAction.Launch([2]),
            [E(1, error: "port conflict"), E(2)], MaintenancePhase.Restarting
        },
        {
            "only errored entries remain: park with a detail",
            [E(1, error: "port conflict"), E(2, done: true)], [],
            UpdateAction.None,
            [E(1, error: "port conflict"), E(2, done: true)], MaintenancePhase.Restarting
        },
        {
            "errored entry was started by hand meanwhile: resolved without a launch",
            [E(1, error: "port conflict")], [1],
            UpdateAction.Complete,
            [], MaintenancePhase.None
        },
        {
            "empty list: complete",
            [], [],
            UpdateAction.Complete,
            [], MaintenancePhase.None
        },
    };

    [Theory]
    [MemberData(nameof(RestartingCases))]
    public void Next_InRestarting_RecoversWithoutDoubleLaunches(string scenario, MaintenanceEntry[] entries, int[] alive, UpdateAction expectedAction, MaintenanceEntry[] expectedEntries, MaintenancePhase expectedPhase)
    {
        Assert.NotEmpty(scenario);
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, entries, _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput { LiveInstances = Set(alive) });

        Assert.Equal(expectedAction, t.Action);
        Assert.Equal(expectedEntries, t.State.Entries);
        Assert.Equal(expectedPhase, t.State.Phase);
    }

    [Fact]
    public void Next_InRestarting_ParkedState_CarriesTheFailuresInTheDetail()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, error: "port conflict")], _now, null);

        var t = UpdateStateMachine.Next(current, new UpdateInput());

        Assert.Equal(UpdateAction.None, t.Action);
        Assert.Contains("port conflict", t.State.Detail, StringComparison.Ordinal);
        Assert.True(t.State.HasFailedEntries);
    }

    [Fact]
    public void Next_InRestarting_FailedLaunchRecordsAnErrorAndDoesNotBlock()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1), E(2)], _now, null);
        var input = new UpdateInput
        {
            LaunchResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Rejected("missing ServerAdminPassword"), [2] = OperationOutcome.Success },
        };

        var t = UpdateStateMachine.Next(current, input);

        Assert.Equal(UpdateAction.MarkDone([2]), t.Action);
        Assert.Equal(MaintenancePhase.Restarting, t.State.Phase);
        Assert.Equal([E(1, error: "missing ServerAdminPassword"), E(2, done: true)], t.State.Entries);

        var parked = UpdateStateMachine.Next(t.State, new UpdateInput());

        Assert.Equal(UpdateAction.None, parked.Action);
        Assert.Equal(MaintenancePhase.Restarting, parked.State.Phase);
        Assert.Equal(t.State.Entries, parked.State.Entries);
    }

    [Fact]
    public void Next_InRestarting_AllLaunchesSucceeded_Completes()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1), E(2)], _now, null);
        var input = new UpdateInput
        {
            LiveInstances = Set(1, 2),
            LaunchResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Success, [2] = OperationOutcome.Success },
        };

        var marked = UpdateStateMachine.Next(current, input);
        Assert.Equal(UpdateAction.MarkDone([1, 2]), marked.Action);
        Assert.Equal([E(1, done: true), E(2, done: true)], marked.State.Entries);

        var t = UpdateStateMachine.Next(marked.State, new UpdateInput { LiveInstances = Set(1, 2) });

        Assert.Equal(UpdateAction.Complete, t.Action);
        Assert.Equal(MaintenancePhase.None, t.State.Phase);
        Assert.Empty(t.State.Entries);
        Assert.True(t.State.IsResolved);
    }

    [Fact]
    public void Next_InRestarting_LaunchResultForADoneEntryIsIgnored()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, done: true), E(2)], _now, null);
        var input = new UpdateInput { LaunchResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Rejected("late") } };

        var t = UpdateStateMachine.Next(current, input);

        Assert.Equal(UpdateAction.Launch([2]), t.Action);
        Assert.Equal([E(1, done: true), E(2)], t.State.Entries);
    }

    // ---------------------------------------------------------------- Retry / Skip

    [Fact]
    public void Retry_ClearsTheErrorAndLaunchesOnlyThatEntry()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, error: "boom"), E(2, error: "bang")], _now, "detail");

        var t = UpdateStateMachine.Retry(current, 1);

        Assert.Equal(UpdateAction.Launch([1]), t.Action);
        Assert.Equal([E(1), E(2, error: "bang")], t.State.Entries);
        Assert.Null(t.State.Detail);
    }

    [Fact]
    public void Retry_ThenNext_LaunchesOnlyTheRetriedEntry()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, error: "boom"), E(2, error: "bang")], _now, null);
        var retried = UpdateStateMachine.Retry(current, 1).State;

        var t = UpdateStateMachine.Next(retried, new UpdateInput());

        Assert.Equal(UpdateAction.Launch([1]), t.Action);
    }

    [Fact]
    public void Skip_MarksTheEntryDone()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, error: "boom"), E(2, error: "bang")], _now, null);

        var t = UpdateStateMachine.Skip(current, 1);

        Assert.Equal(UpdateAction.MarkDone([1]), t.Action);
        Assert.Equal([E(1, done: true), E(2, error: "bang")], t.State.Entries);
        Assert.Equal(MaintenancePhase.Restarting, t.State.Phase);
    }

    [Fact]
    public void Skip_OfTheLastEntry_ResolvesThePhase()
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, done: true), E(2, error: "bang")], _now, null);

        var t = UpdateStateMachine.Skip(current, 2);

        Assert.Equal(UpdateAction.Complete, t.Action);
        Assert.True(t.State.IsResolved);
    }

    [Theory]
    [InlineData(MaintenancePhase.None)]
    [InlineData(MaintenancePhase.Stopping)]
    [InlineData(MaintenancePhase.Updating)]
    public void RetryAndSkip_OutsideRestarting_AreRefused(MaintenancePhase phase)
    {
        var current = new MaintenanceSnapshot(phase, [E(1, error: "boom")], _now, null);

        Assert.Equal(UpdateActionKind.Refuse, UpdateStateMachine.Retry(current, 1).Action.Kind);
        Assert.Equal(UpdateActionKind.Refuse, UpdateStateMachine.Skip(current, 1).Action.Kind);
    }

    [Theory]
    [InlineData(1)] // done
    [InlineData(2)] // pending without error
    [InlineData(3)] // not listed
    public void RetryAndSkip_OfAnEntryWithoutAnError_AreRefused(int instanceId)
    {
        var current = new MaintenanceSnapshot(MaintenancePhase.Restarting, [E(1, done: true), E(2)], _now, null);

        Assert.Equal(UpdateActionKind.Refuse, UpdateStateMachine.Retry(current, instanceId).Action.Kind);
        Assert.Equal(UpdateActionKind.Refuse, UpdateStateMachine.Skip(current, instanceId).Action.Kind);
    }

    // ---------------------------------------------------------------- whole-flow replay

    [Fact]
    public void FullFlow_ReplayedThroughTheMachine_PersistsStoppingUpdatingRestartingNone()
    {
        var phases = new List<MaintenancePhase>();
        var runtimes = new[] { Runtime(1, InstanceState.Running), Runtime(2, InstanceState.Running) };
        var t = UpdateStateMachine.Begin(_idle, false, runtimes, true, _now);
        phases.Add(t.State.Phase);
        Assert.Equal(UpdateAction.StopInstances([1, 2]), t.Action);

        t = UpdateStateMachine.Next(t.State, new UpdateInput
        {
            StopResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Success, [2] = OperationOutcome.Success },
        });
        phases.Add(t.State.Phase);
        Assert.Equal(UpdateAction.MarkDone([1, 2]), t.Action);

        t = UpdateStateMachine.Next(t.State, new UpdateInput());
        phases.Add(t.State.Phase);
        Assert.Equal(UpdateAction.RunSteamCmd, t.Action);

        t = UpdateStateMachine.Next(t.State, new UpdateInput { Install = InstallResult.Success });
        phases.Add(t.State.Phase);
        Assert.Equal(UpdateAction.Launch([1, 2]), t.Action);

        t = UpdateStateMachine.Next(t.State, new UpdateInput
        {
            LiveInstances = Set(1, 2),
            LaunchResults = new Dictionary<int, OperationOutcome> { [1] = OperationOutcome.Success, [2] = OperationOutcome.Success },
        });
        phases.Add(t.State.Phase);
        Assert.Equal(UpdateAction.MarkDone([1, 2]), t.Action);

        t = UpdateStateMachine.Next(t.State, new UpdateInput { LiveInstances = Set(1, 2) });
        phases.Add(t.State.Phase);

        Assert.Equal(UpdateAction.Complete, t.Action);
        Assert.Equal(
            [MaintenancePhase.Stopping, MaintenancePhase.Stopping, MaintenancePhase.Updating, MaintenancePhase.Restarting, MaintenancePhase.Restarting, MaintenancePhase.None],
            phases);
    }

    [Fact]
    public void UpdateInput_Observe_SplitsLiveAndUnknown()
    {
        var input = UpdateInput.Observe([Runtime(1, InstanceState.Running), Runtime(2, InstanceState.Unknown), Runtime(3, InstanceState.Stopped), Runtime(4, InstanceState.IdentityUnpersisted)]);

        Assert.Equal(Set(1, 4), input.LiveInstances);
        Assert.Equal(Set(2), input.UnknownInstances);
    }

    private static InstanceRuntime Runtime(int id, InstanceState state) => new(id, state, null, null, null, null, null);

    private static MaintenanceEntry E(int id, bool done = false, string? error = null) => new(id, done, error);

    private static HashSet<int> Set(params int[] ids) => [.. ids];
}
