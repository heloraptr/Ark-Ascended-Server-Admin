using System.ComponentModel;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>
/// Crash recovery in the real process manager (B4). Launches go through the launch seam, which starts a stand-in
/// <c>cmd.exe</c>, so a relaunch succeeds and killing the stand-in drives the real exit path. The races are held with
/// barriers on the fakes (starter, RCON client, console), an EF command interceptor for database writes, and the
/// manager's internal pause points where no fake sits.
/// </summary>
public class ProcessManagerCrashRecoveryTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    /// <summary>For a call that can include the identity persist retries (about 10 s) or a stop.</summary>
    private static readonly TimeSpan _slow = TimeSpan.FromSeconds(45);

    /// <summary>No countdown, the shortest graceful timeout, and no stagger between consecutive launches.</summary>
    private static readonly AppSettings _settings = new() { PreStopBroadcastMinutes = 0, GracefulStopTimeoutSeconds = 5, StaggerDelaySeconds = 0 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the marker ------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnexpectedExit_InstallsTheMarker_ButARequestedStopOrAMaintenanceExitDoesNot()
    {
        using var rig = await Rig.CreateAsync();
        var first = await rig.LaunchAsync();

        var crash = await rig.KillAsync(first);

        Assert.Same(crash, rig.Runtime.PendingCrash);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.False(crash.StopIntent);
        Assert.False(crash.DuringMaintenance);

        await rig.LaunchAsync();
        var stopped = await rig.Manager.StopAsync(rig.Alpha, new StopOptions(SkipCountdown: true), Ct).WaitAsync(_slow, Ct);
        var requested = await rig.NextRequestAsync();

        Assert.True(stopped.Succeeded, stopped.Error);
        Assert.True(requested.StopIntent);
        Assert.Null(rig.Runtime.PendingCrash);

        var third = await rig.LaunchAsync();
        RecoveryRequest maintenance;
        using (await rig.Harness.Gate.AcquireExclusiveAsync(Ct))
        {
            maintenance = await rig.KillAsync(third);
        }

        Assert.True(maintenance.DuringMaintenance);
        Assert.False(maintenance.StopIntent);
        Assert.Null(rig.Runtime.PendingCrash);
    }

    // ---- RecoverAsync ----------------------------------------------------------------------------

    [Fact]
    public async Task Recover_RelaunchesThreeTimes_ThenGivesUpAsCrashed()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());

        for (var attempt = 1; attempt <= CrashLoopRule.MaxRestarts; attempt++)
        {
            var launched = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);

            Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, attempt), launched);
            Assert.Equal(attempt, rig.Runtime.AutoRestarts);
            Assert.True(rig.Runtime.HasLiveProcess);
            Assert.Null(rig.Runtime.PendingCrash);
            Assert.Contains(rig.Lines, line => line.Text == $"Restarting after unexpected exit ({attempt}/3).");
            crash = await rig.KillAsync(rig.Harness.Starter.Last);
        }

        var gaveUp = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);

        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.GaveUp, CrashLoopRule.MaxRestarts), gaveUp);
        Assert.Equal(InstanceState.Crashed, rig.Runtime.State);
        Assert.Equal(CrashLoopRule.MaxRestarts, rig.Runtime.AutoRestarts);
        Assert.Equal(CrashLoopRule.GaveUpMessage, rig.Runtime.Detail);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(1 + CrashLoopRule.MaxRestarts, rig.Harness.Starter.Started.Count);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task Recover_IsBusyWhileTheLeaseIsHeld_AndLeavesTheCountAlone()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        Assert.Equal(CrashRecoveryStatus.Launched, (await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct)).Status);
        crash = await rig.KillAsync(rig.Harness.Starter.Last);

        using (rig.Harness.Locks.TryAcquire(rig.Alpha))
        {
            Assert.Equal(CrashRecovery.Busy, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
            Assert.Equal(1, rig.Runtime.AutoRestarts);
            Assert.Same(crash, rig.Runtime.PendingCrash);
        }

        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, 2), await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task AnOldRequest_IsStaleAfterAManualStart_AndLeavesTheNewCountAlone()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        Assert.Equal(CrashRecoveryStatus.Launched, (await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct)).Status);
        var old = await rig.KillAsync(rig.Harness.Starter.Last);

        await rig.LaunchAsync();

        Assert.Equal(0, rig.Runtime.AutoRestarts);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(old, Ct).WaitAsync(_slow, Ct));
        Assert.Equal(0, rig.Runtime.AutoRestarts);
        Assert.Equal(3, rig.Harness.Starter.Started.Count);
    }

    [Fact]
    public async Task Recover_IsDisabled_WhenAutoRestartWasTurnedOffAfterTheExit()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        await rig.SetAutoRestartAsync(false);

        Assert.Equal(CrashRecovery.Disabled, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Single(rig.Harness.Starter.Started);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task Recover_IsDisabled_WhenAutoRestartIsTurnedOffWhileTheLaunchWaitsForItsRowRead()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        var held = rig.Database.Hold(command => command.CommandText.Contains("\"Maps\"", StringComparison.Ordinal));

        var recovering = rig.Manager.RecoverAsync(crash, Ct);
        await held.WaitAsync(_wait, Ct);
        await rig.SetAutoRestartAsync(false);
        rig.Database.Release();
        var result = await recovering.WaitAsync(_wait, Ct);

        Assert.Equal(CrashRecovery.Disabled, result);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Single(rig.Harness.Starter.Started);
    }

    [Fact]
    public async Task ARelaunchPastItsRowRead_IsCommitted_EvenWhenAutoRestartIsTurnedOffBehindIt()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Harness.Starter.ResetWaiting();
        rig.Harness.Starter.Barrier = release;

        var recovering = rig.Manager.RecoverAsync(crash, Ct);
        await rig.Harness.Starter.Waiting.Task.WaitAsync(_wait, Ct);
        await rig.SetAutoRestartAsync(false);
        release.SetResult();
        var result = await recovering.WaitAsync(_wait, Ct);

        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, 1), result);
        Assert.True(rig.Runtime.HasLiveProcess);
    }

    [Fact]
    public async Task Recover_IsSkipped_WhenTheGateIsTakenBeforeTheLaunch()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        Assert.Same(crash, rig.Runtime.PendingCrash);

        CrashRecovery result;
        using (await rig.Harness.Gate.AcquireExclusiveAsync(Ct))
        {
            result = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);
        }

        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Skipped, 1, MaintenanceGate.UpdateInProgress), result);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(0, rig.Runtime.AutoRestarts);
    }

    [Fact]
    public async Task ARefusedRelaunch_EndsCrashed_WithTheReason()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        rig.Harness.Starter.Failure = new Win32Exception(2, "The system cannot find the file specified.");

        var result = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);

        Assert.Equal(CrashRecoveryStatus.Refused, result.Status);
        Assert.Equal("Process.Start failed: The system cannot find the file specified.", result.Reason);
        Assert.Equal(InstanceState.Crashed, rig.Runtime.State);
        Assert.Equal($"Automatic restart was refused: {result.Reason}", rig.Runtime.Detail);
        Assert.Equal(0, rig.Runtime.AutoRestarts);
        Assert.Null(rig.Runtime.PendingCrash);
    }

    [Fact]
    public async Task ARejectedManualStart_StillClearsCrashed_AndTheCount()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        Assert.Equal(CrashRecoveryStatus.Launched, (await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct)).Status);
        crash = await rig.KillAsync(rig.Harness.Starter.Last);
        rig.Harness.Starter.Failure = new Win32Exception(2, "gone");
        Assert.Equal(CrashRecoveryStatus.Refused, (await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct)).Status);
        Assert.Equal((InstanceState.Crashed, 1), (rig.Runtime.State, rig.Runtime.AutoRestarts));

        OperationOutcome start;
        using (await rig.Harness.Gate.AcquireExclusiveAsync(Ct))
        {
            start = await rig.Manager.StartAsync(rig.Alpha, LaunchKind.User, Ct).WaitAsync(_slow, Ct);
        }

        Assert.Equal(MaintenanceGate.UpdateInProgress, start.Error);
        Assert.Equal((InstanceState.Stopped, 0), (rig.Runtime.State, rig.Runtime.AutoRestarts));
        Assert.Null(rig.Runtime.Detail);
    }

    // ---- dismissal -------------------------------------------------------------------------------

    [Fact]
    public async Task DismissCrash_MakesALaterRecoverStale()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());

        rig.Manager.DismissCrash(rig.Alpha);

        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
        Assert.Single(rig.Harness.Starter.Started);
    }

    [Fact]
    public async Task AFailedDeleteOfAnInstanceThatJustCrashed_LeavesNoMarker()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        var delete = rig.DeleteService(new ThrowingLayoutService());

        var outcome = await delete.DeleteAsync(rig.Alpha, new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false), Ct).WaitAsync(_slow, Ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Delete failed", outcome.Error, StringComparison.Ordinal);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct));
        Assert.Single(rig.Harness.Starter.Started);
    }

    // ---- exit ordering ---------------------------------------------------------------------------

    [Fact]
    public async Task ExitCleanup_KeepsStartRefused_UntilItsDatabaseWriteIsDone_AndTheNewIdentitySurvives()
    {
        using var rig = await Rig.CreateAsync();
        var first = await rig.LaunchAsync();
        var held = rig.Database.Hold(CommandGate.ClearsIdentity);

        first.Process.Kill(entireProcessTree: true);
        await held.WaitAsync(_wait, Ct);
        var refused = await rig.Manager.StartAsync(rig.Alpha, LaunchKind.User, Ct).WaitAsync(_slow, Ct);

        Assert.False(refused.Succeeded);
        Assert.Contains("already", refused.Error, StringComparison.Ordinal);
        Assert.False(rig.Harness.Recovery.Reader.TryPeek(out _));

        rig.Database.Release();
        await rig.NextRequestAsync();
        var second = await rig.LaunchAsync();
        await Task.Delay(300, Ct);

        var row = await rig.RowAsync();
        Assert.Equal(second.Process.Id, row.LastPid);
        Assert.NotNull(row.LastProcessStartTime);
    }

    /// <summary>
    /// The owner pressed Stop on a server whose exit was already being cleaned up: the stop is accepted, waits for the
    /// exit, and dismisses the crash, so nothing restarts the server behind the owner's back.
    /// </summary>
    [Fact]
    public async Task AStopDuringExitCleanup_Succeeds_DismissesTheCrash_AndNeverWritesStopping()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var states = new List<InstanceState>();
        rig.Manager.RuntimeChanged += runtime =>
        {
            lock (states)
            {
                states.Add(runtime.State);
            }
        };
        var held = rig.Database.Hold(CommandGate.ClearsIdentity);

        game.Process.Kill(entireProcessTree: true);
        await held.WaitAsync(_wait, Ct);
        var stopping = rig.Manager.StopAsync(rig.Alpha, new StopOptions(SkipCountdown: true), Ct);
        await Task.Delay(300, Ct);

        Assert.False(stopping.IsCompleted, "the stop waits for the exit it found in progress");

        rig.Database.Release();
        var stop = await stopping.WaitAsync(_slow, Ct);
        var request = await rig.NextRequestAsync();

        Assert.True(stop.Succeeded, stop.Error);
        Assert.False(request.StopIntent);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
        Assert.DoesNotContain(RconCommands.DoExit, rig.Rcon.Commands);
        lock (states)
        {
            Assert.DoesNotContain(InstanceState.Stopping, states);
        }
    }

    /// <summary>A restart pressed during the exit cleanup stops as above, then relaunches as an ordinary start.</summary>
    [Fact]
    public async Task ARestartDuringExitCleanup_RelaunchesAsAManualStart_AndLeavesNoMarker()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var held = rig.Database.Hold(CommandGate.ClearsIdentity);

        game.Process.Kill(entireProcessTree: true);
        await held.WaitAsync(_wait, Ct);
        var restarting = rig.Manager.RestartWithCountdownAsync(rig.Alpha, DateTimeOffset.UtcNow, Ct);
        rig.Database.Release();
        var restart = await restarting.WaitAsync(_slow, Ct);
        var request = await rig.NextRequestAsync();

        Assert.True(restart.Succeeded, restart.Error);
        Assert.Equal(2, rig.Harness.Starter.Started.Count);
        Assert.True(rig.Runtime.HasLiveProcess);
        Assert.Equal(0, rig.Runtime.AutoRestarts);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task AStopAcceptedJustBeforeTheKill_CarriesTheIntent_AndInstallsNoMarker()
    {
        using var rig = await Rig.CreateAsync(rcon: new FakeRconClient(failure: null));
        var game = await rig.LaunchAsync();

        var stopping = rig.Manager.StopAsync(rig.Alpha, new StopOptions(Deadline: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)), Ct);
        await WaitUntilAsync(() => rig.Runtime.State == InstanceState.Stopping);
        var request = await rig.KillAsync(game);
        var stop = await stopping.WaitAsync(_wait, Ct);

        Assert.True(request.StopIntent);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.True(stop.Succeeded, stop.Error);
        Assert.DoesNotContain(RconCommands.DoExit, rig.Rcon.Commands);
        Assert.DoesNotContain(rig.Lines, line => line.Text.StartsWith("Kill failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeleteWhileTheExitWritesTheDatabase_Completes_AndVoidsTheExit()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var held = rig.Database.Hold(CommandGate.ClearsIdentity);

        game.Process.Kill(entireProcessTree: true);
        await held.WaitAsync(_wait, Ct);
        var deleting = rig.DeleteService(new FakeLayoutService()).DeleteAsync(rig.Alpha, new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false), Ct);
        rig.Database.Release();
        var outcome = await deleting.WaitAsync(_slow, Ct);
        var request = await rig.NextRequestAsync();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.False(rig.Harness.Locks.IsHeld(rig.Alpha));
        Assert.False(request.StopIntent);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
        Assert.Single(rig.Harness.Starter.Started);
        await using var db = rig.Root.CreateDbContext();
        Assert.False(await db.Instances.AnyAsync(i => i.Id == rig.Alpha, Ct));
    }

    [Fact]
    public async Task ADeleteJustBeforeTheExitPublishes_Completes_AndVoidsTheExit()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Console.LineAppended += (_, line) =>
        {
            if (line.Text.StartsWith("Server exited unexpectedly", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                release.Task.Wait(Ct);
            }
        };

        game.Process.Kill(entireProcessTree: true);
        await reached.Task.WaitAsync(_wait, Ct);
        Assert.True(rig.Runtime.HasLiveProcess, "the exit has not published yet");
        var deleting = rig.DeleteService(new FakeLayoutService()).DeleteAsync(rig.Alpha, new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false), Ct);
        await Task.Delay(300, Ct);

        Assert.False(deleting.IsCompleted, "the delete's stop waits for the exit signal");

        release.SetResult();
        var outcome = await deleting.WaitAsync(_slow, Ct);
        var request = await rig.NextRequestAsync();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task AnExitPausedInsideItsCapture_HoldsADeleteStopUntilTheCaptureIsDone_ThenTheDeleteVoidsIt()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager.TestHooks.InsideExitCapture = _ =>
        {
            reached.TrySetResult();
            release.Task.Wait(Ct);
        };

        game.Process.Kill(entireProcessTree: true);
        await reached.Task.WaitAsync(_wait, Ct);
        var deleting = rig.DeleteService(new FakeLayoutService()).DeleteAsync(rig.Alpha, new InstanceDeleteOptions(KeepWorldData: true, DeleteBackups: false), Ct);
        await Task.Delay(500, Ct);

        Assert.False(deleting.IsCompleted, "the delete's stop must wait for the session lock");

        release.SetResult();
        var outcome = await deleting.WaitAsync(_slow, Ct);
        var request = await rig.NextRequestAsync();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.False(request.StopIntent, "the capture ran before the delete's stop was accepted");
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Equal(CrashRecovery.Stale, await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
    }

    /// <summary>The exit's database write fails: the row is stale, but the exit is still published and signaled.</summary>
    [Fact]
    public async Task AFailedExitDatabaseWrite_StillPublishesTheExit_AndSignalsIt()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        rig.Database.FailWhen = CommandGate.ClearsIdentity;
        var pid = game.Process.Id;

        var request = await rig.KillAsync(game);

        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.Pid);
        Assert.Same(request, rig.Runtime.PendingCrash);
        Assert.Equal(pid, (await rig.RowAsync()).LastPid);
        rig.Database.FailWhen = null;
        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, 1), await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task Recover_IsSkipped_WhileARestoreReservesTheCluster()
    {
        using var rig = await Rig.CreateAsync(clustered: true);
        var crash = await rig.KillAsync(await rig.LaunchAsync());

        CrashRecovery result;
        using (rig.Harness.Locks.TryReserveCluster(rig.ClusterId!.Value))
        {
            result = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);
        }

        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Skipped, 1, InstanceLocks.ClusterReservedByRestore), result);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Single(rig.Harness.Starter.Started);
    }

    [Fact]
    public async Task Recover_IsSkipped_WhileARestoreJournalIsUnresolved()
    {
        using var rig = await Rig.CreateAsync();
        var crash = await rig.KillAsync(await rig.LaunchAsync());
        var journal = new RestoreJournal("alpha-1", DateTimeOffset.UtcNow, rig.Alpha, "alpha", "TheIsland_WP", null, null, [rig.Alpha], rig.Root.Layout.Root, "x.zip", RestorePhase.Replacing);
        new RestoreJournalStore(rig.Root.Layout).Write(journal);

        var result = await rig.Manager.RecoverAsync(crash, Ct).WaitAsync(_slow, Ct);

        Assert.Equal(CrashRecoveryStatus.Skipped, result.Status);
        Assert.Equal(journal.RefusalReason("this instance"), result.Reason);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.PendingCrash);
        Assert.Single(rig.Harness.Starter.Started);
    }

    // ---- the epoch -------------------------------------------------------------------------------

    [Fact]
    public void ThePublishedRecord_CarriesTheMarker_OnlyForAnEligibleExitWhoseEpochDidNotMove()
    {
        var request = new RecoveryRequest(1, 100, DateTimeOffset.UnixEpoch, 1, false, DateTimeOffset.UnixEpoch.AddMinutes(1), false);
        var live = new InstanceRuntime(1, InstanceState.Running, 100, DateTimeOffset.UnixEpoch, null, null, null, AutoRestarts: 2, RecoveryEpoch: 4);

        var published = ProcessManager.ExitedRuntime(live, request, capturedEpoch: 4, eligible: true, "Exited unexpectedly.");

        Assert.Same(request, published.PendingCrash);
        Assert.Equal((InstanceState.Stopped, (int?)null, (DateTimeOffset?)null, 2, 4), (published.State, published.Pid, published.ProcessStartTime, published.AutoRestarts, published.RecoveryEpoch));
        Assert.Null(ProcessManager.ExitedRuntime(live, request, capturedEpoch: 3, eligible: true, null).PendingCrash);
        Assert.Null(ProcessManager.ExitedRuntime(live, request, capturedEpoch: 4, eligible: false, null).PendingCrash);
    }

    [Fact]
    public async Task ADismissalAfterTheCapture_AlwaysWinsTheRaceWithPublication()
    {
        using var rig = await Rig.CreateAsync();
        var seed = new RecoveryRequest(rig.Alpha, 1, DateTimeOffset.UnixEpoch, null, false, DateTimeOffset.UnixEpoch, false);
        rig.Manager.PublishExit(rig.Alpha, seed, 0, eligible: false, null);

        for (var round = 0; round < 3000; round++)
        {
            var epoch = rig.Runtime.RecoveryEpoch;
            var request = seed with { Pid = round + 2 };
            using var start = new Barrier(2);
            var publish = Task.Run(() =>
            {
                start.SignalAndWait(Ct);
                rig.Manager.PublishExit(rig.Alpha, request, epoch, eligible: true, null);
            }, Ct);
            var dismiss = Task.Run(() =>
            {
                start.SignalAndWait(Ct);
                rig.Manager.DismissCrash(rig.Alpha);
            }, Ct);
            await Task.WhenAll(publish, dismiss);

            Assert.Null(rig.Runtime.PendingCrash);
        }

        var last = seed with { Pid = 99_999 };
        rig.Manager.PublishExit(rig.Alpha, last, rig.Runtime.RecoveryEpoch, eligible: true, null);
        Assert.Same(last, rig.Runtime.PendingCrash);
    }

    // ---- database ordering -----------------------------------------------------------------------

    [Fact]
    public async Task AStopJobWriteHeldInsideTheSessionMutex_MakesTheExitCleanupWait()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var held = rig.Database.Hold(command => CommandGate.SetsState(command, InstanceState.Stopping));

        var stopping = rig.Manager.StopAsync(rig.Alpha, new StopOptions(SkipCountdown: true), Ct);
        await held.WaitAsync(_wait, Ct);
        game.Process.Kill(entireProcessTree: true);
        await Task.Delay(700, Ct);

        Assert.False(rig.Harness.Recovery.Reader.TryPeek(out _), "the exit cleanup must wait for the stop job's write");
        Assert.Equal(InstanceState.Stopping, rig.Runtime.State);

        rig.Database.Release();
        var request = await rig.NextRequestAsync();
        var stop = await stopping.WaitAsync(_wait, Ct);

        Assert.True(request.StopIntent);
        Assert.True(stop.Succeeded, stop.Error);
        var row = await rig.RowAsync();
        Assert.Equal((InstanceState.Stopped, (int?)null, (DateTimeOffset?)null), (row.State, row.LastPid, row.LastProcessStartTime));
    }

    [Fact]
    public async Task AnIdentityRetryHeldInsideTheSessionMutex_MakesTheExitCleanupWait()
    {
        using var rig = await Rig.CreateAsync();
        rig.Database.FailWhen = CommandGate.PersistsIdentity;
        var unpersisted = await rig.Manager.StartAsync(rig.Alpha, LaunchKind.User, Ct).WaitAsync(_slow, Ct);
        Assert.Contains("identity could not be saved", unpersisted.Error, StringComparison.Ordinal);
        Assert.Equal(InstanceState.IdentityUnpersisted, rig.Runtime.State);
        rig.Database.FailWhen = null;
        var held = rig.Database.Hold(CommandGate.PersistsIdentity);

        var retrying = rig.Manager.RetryPersistIdentityAsync(rig.Alpha, Ct);
        await held.WaitAsync(_wait, Ct);
        rig.Harness.Starter.Last.Process.Kill(entireProcessTree: true);
        await Task.Delay(700, Ct);

        Assert.False(rig.Harness.Recovery.Reader.TryPeek(out _), "the exit cleanup must wait for the identity write");

        rig.Database.Release();
        await rig.NextRequestAsync();
        await retrying.WaitAsync(_wait, Ct);

        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        var row = await rig.RowAsync();
        Assert.Equal((InstanceState.Stopped, (int?)null, (DateTimeOffset?)null), (row.State, row.LastPid, row.LastProcessStartTime));
    }

    [Fact]
    public async Task AWriterHeldBeforeTheSessionMutex_ResumesAfterTheCleanup_AndWritesNothing()
    {
        using var rig = await Rig.CreateAsync();
        var game = await rig.LaunchAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager.TestHooks.BeforeSessionMirror = async (_, state) =>
        {
            if (state == InstanceState.Stopping)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };

        var stopping = rig.Manager.StopAsync(rig.Alpha, new StopOptions(SkipCountdown: true), Ct);
        await reached.Task.WaitAsync(_wait, Ct);
        var request = await rig.KillAsync(game);
        release.SetResult();
        var stop = await stopping.WaitAsync(_wait, Ct);
        await Task.Delay(300, Ct);

        Assert.True(request.StopIntent);
        Assert.True(stop.Succeeded, stop.Error);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        var row = await rig.RowAsync();
        Assert.Equal((InstanceState.Stopped, (int?)null), (row.State, row.LastPid));
    }

    [Fact]
    public async Task AProbeSuccessHeldUntilAfterTheExitCleanup_ChangesNothing_AndTheRequestIsStillAnswered()
    {
        var rcon = new FakeRconClient(failure: null);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rcon.BeforeReply = async command =>
        {
            if (command == RconCommands.ListPlayers && reached.TrySetResult())
            {
                await release.Task;
            }
        };
        using var rig = await Rig.CreateAsync(rcon: rcon);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manager.ProbeObserved += _ => observed.TrySetResult();
        var game = await rig.LaunchAsync();
        await reached.Task.WaitAsync(_wait, Ct);

        var request = await rig.KillAsync(game);
        release.SetResult();
        await observed.Task.WaitAsync(_wait, Ct);

        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Null(rig.Runtime.Pid);
        Assert.Same(request, rig.Runtime.PendingCrash);
        Assert.Equal(InstanceState.Stopped, (await rig.RowAsync()).State);
        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, 1), await rig.Manager.RecoverAsync(request, Ct).WaitAsync(_slow, Ct));
    }

    [Fact]
    public async Task AnAutomaticLaunchWhoseIdentityCannotBeSaved_AndThatDiesAtOnce_LeavesItsOwnRequestToAnswer()
    {
        using var rig = await Rig.CreateAsync();
        var first = await rig.KillAsync(await rig.LaunchAsync());
        rig.Database.FailWhen = CommandGate.PersistsIdentity;

        var recovering = rig.Manager.RecoverAsync(first, Ct);
        await WaitUntilAsync(() => rig.Harness.Starter.Started.Count == 2);
        rig.Harness.Starter.Last.Process.Kill(entireProcessTree: true);
        var launched = await recovering.WaitAsync(_slow, Ct);
        rig.Database.FailWhen = null;
        var second = await rig.NextRequestAsync();

        // The process was registered, so the relaunch happened; the failure that followed is only a note.
        Assert.Equal((CrashRecoveryStatus.Launched, 1), (launched.Status, launched.Attempt));
        Assert.Contains("identity could not be saved", launched.Reason, StringComparison.Ordinal);
        Assert.Same(second, rig.Runtime.PendingCrash);
        Assert.Equal(InstanceState.Stopped, rig.Runtime.State);
        Assert.Equal(1, rig.Runtime.AutoRestarts);
        Assert.Equal(new CrashRecovery(CrashRecoveryStatus.Launched, 2), await rig.Manager.RecoverAsync(second, Ct).WaitAsync(_slow, Ct));
    }

    // ---- end to end ------------------------------------------------------------------------------

    [Fact]
    public async Task WithThePolicy_KillingTheServer_RelaunchesItAsAutomaticRestartOne()
    {
        using var rig = await Rig.CreateAsync();
        var readiness = Substitute.For<IReadinessMonitor>();
        readiness.Current.Returns(new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow));
        using var policy = new CrashPolicy(rig.Harness.Recovery, rig.Manager, rig.Root, readiness, rig.Console, TimeProvider.System, NullLogger<CrashPolicy>.Instance);
        await policy.StartAsync(Ct);
        try
        {
            var game = await rig.LaunchAsync();

            game.Process.Kill(entireProcessTree: true);
            await WaitUntilAsync(() => rig.Harness.Starter.Started.Count == 2 && rig.Runtime.HasLiveProcess);

            Assert.Equal(1, rig.Runtime.AutoRestarts);
            Assert.Equal(InstanceState.Starting, rig.Runtime.State);
            Assert.Equal(rig.Harness.Starter.Last.Process.Id, rig.Runtime.Pid);
        }
        finally
        {
            await policy.StopAsync(Ct);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>One instance (AutoRestart on, ports nothing else on the box uses) and a manager whose launches start stand-ins.</summary>
    private sealed class Rig : IDisposable
    {
        private Rig(TempDataRoot root, ProcessManagerHarness harness, RecordingConsole console, FakeRconClient rcon, CommandGate database, int alpha)
        {
            Root = root;
            Harness = harness;
            Console = console;
            Rcon = rcon;
            Database = database;
            Alpha = alpha;
        }

        public TempDataRoot Root { get; }

        public ProcessManagerHarness Harness { get; }

        public ProcessManager Manager => Harness.Manager;

        public RecordingConsole Console { get; }

        public FakeRconClient Rcon { get; }

        public CommandGate Database { get; }

        public int Alpha { get; }

        public InstanceRuntime Runtime => Manager.GetRuntime(Alpha);

        public IReadOnlyList<ConsoleLine> Lines => Console.Snapshot(ConsoleChannels.Instance(Alpha));

        /// <summary>The cluster Alpha belongs to, when created with <c>clustered: true</c>.</summary>
        public int? ClusterId { get; private init; }

        public static async Task<Rig> CreateAsync(FakeRconClient? rcon = null, bool clustered = false)
        {
            var root = new TempDataRoot();
            await root.InitializeAsync(Ct);
            int alpha;
            int? clusterId;
            await using (var db = root.CreateDbContext())
            {
                var map = await db.Maps.OrderBy(m => m.Id).FirstAsync(Ct);
                var cluster = clustered ? new Cluster { Name = "Cluster", Slug = "cluster", ClusterKey = "cluster", CreatedAt = DateTimeOffset.UnixEpoch } : null;
                var instance = new Instance { Name = "Alpha", Slug = "alpha", SessionName = "Alpha", MapId = map.Id, Cluster = cluster, GamePort = 47777, RconPort = 47020, AutoRestart = true, CreatedAt = DateTimeOffset.UtcNow };
                db.Instances.Add(instance);
                await db.SaveChangesAsync(Ct);
                alpha = instance.Id;
                clusterId = cluster?.Id;
            }

            rcon ??= new FakeRconClient(RconFailure.Connect);
            var console = new RecordingConsole();
            var gate = new CommandGate();
            var harness = new ProcessManagerHarness(root, [], rcon, console, new FakeOutputSourceFactory(), settings: _settings, database: new InterceptedDatabase(root, gate));
            harness.Starter.UseStandIn = true;
            return new Rig(root, harness, console, rcon, gate, alpha) { ClusterId = clusterId };
        }

        /// <summary>A manual start that must succeed; returns the stand-in it launched.</summary>
        public async Task<StandInProcess> LaunchAsync()
        {
            var outcome = await Manager.StartAsync(Alpha, LaunchKind.User, Ct).WaitAsync(_slow, Ct);
            Assert.True(outcome.Succeeded, outcome.Error);
            return Harness.Starter.Last;
        }

        public async Task<RecoveryRequest> KillAsync(StandInProcess game)
        {
            game.Process.Kill(entireProcessTree: true);
            return await NextRequestAsync();
        }

        public async Task<RecoveryRequest> NextRequestAsync() =>
            await Harness.Recovery.Reader.ReadAsync(Ct).AsTask().WaitAsync(_wait, Ct);

        public async Task SetAutoRestartAsync(bool value)
        {
            await using var db = Root.CreateDbContext();
            await db.Instances.Where(i => i.Id == Alpha).ExecuteUpdateAsync(set => set.SetProperty(i => i.AutoRestart, value), Ct);
        }

        public async Task<Instance> RowAsync()
        {
            await using var db = Root.CreateDbContext();
            return await db.Instances.AsNoTracking().SingleAsync(i => i.Id == Alpha, Ct);
        }

        public InstanceDeleteService DeleteService(IInstanceLayoutService layout)
        {
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(CancellationToken.None);
            return new InstanceDeleteService(Root, Root.Layout, Harness.Locks, new RestoreJournalStore(Root.Layout), new DetachedJobs(TimeProvider.System), Manager, new FakeFirewall(), layout, Console, lifetime, TimeProvider.System, NullLogger<InstanceDeleteService>.Instance);
        }

        public void Dispose()
        {
            Harness.Dispose();
            Root.Dispose();
        }
    }

    /// <summary>A layout service whose junction removal fails, so a delete aborts after it took ownership.</summary>
    private sealed class ThrowingLayoutService : IInstanceLayoutService
    {
        public Task EnsureAsync(string slug, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool IsComplete(string slug) => true;

        public Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken) => throw new IOException("The junction is in use.");

        public Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
