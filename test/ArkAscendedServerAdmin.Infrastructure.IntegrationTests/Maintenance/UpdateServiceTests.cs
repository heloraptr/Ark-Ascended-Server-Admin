using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Install;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Maintenance;

/// <summary>The update/recovery driver (plan steps 11, 29) against fakes and a real SQLite maintenance row.</summary>
public class UpdateServiceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HappyPath_PersistsStoppingUpdatingRestartingNone()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);
        f.Processes.Set(f.Beta.Id, InstanceState.Unreachable);

        var outcome = await f.Service.StartUpdateAsync(confirmStopRunningInstances: true, validate: false, ct);
        Assert.True(outcome.Succeeded, outcome.Error);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([MaintenancePhase.Stopping, MaintenancePhase.Updating, MaintenancePhase.Restarting, MaintenancePhase.None], f.DistinctPhases());
        var row = await TestSeed.MaintenanceRowAsync(root, ct);
        Assert.Equal(MaintenancePhase.None, row.Phase);
        Assert.Empty(row.Entries);
        Assert.Null(row.StartedAt);
        Assert.True(f.Service.Current.IsResolved);
        Assert.False(f.Service.IsOperationInProgress);

        Assert.Equal([f.Alpha.Id, f.Beta.Id], f.Processes.Stops.Select(s => s.InstanceId).Order());
        Assert.All(f.Processes.Stops, s => Assert.True(s.Options.RequireVerifiedExit));
        Assert.Equal([f.Alpha.Id, f.Beta.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.All(f.Processes.Starts, s => Assert.Equal(LaunchKind.Recovery, s.Kind));
        Assert.All(f.Processes.Starts, s => Assert.False(s.GateHeldExclusively));
        Assert.False(f.Gate.IsHeldExclusively);
        Assert.Equal(1, f.Gate.ExclusiveAcquisitions);
        await f.SteamCmd.Received(1).InstallOrUpdateAsync(false, Arg.Any<CancellationToken>());
        Assert.Equal(InstanceState.Running, f.Processes.GetRuntime(f.Alpha.Id).State);
    }

    [Fact]
    public async Task RequestedValidate_IsForwardedOnce_AndTheBuildComparisonIsRecorded()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Checker.Check().Returns(
            new GameInstallStatus(true, true, "Install verified.", "100"),
            new GameInstallStatus(true, true, "Install verified.", "101"));

        Assert.True((await f.Service.StartUpdateAsync(confirmStopRunningInstances: false, validate: true, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        await f.SteamCmd.Received(1).InstallOrUpdateAsync(true, Arg.Any<CancellationToken>());
        var result = Assert.IsType<UpdateResult>(f.Service.Current.LastResult);
        Assert.Equal("100", result.PreviousBuild);
        Assert.Equal("101", result.InstalledBuild);
        Assert.True(result.BuildChanged);
        Assert.True(result.Validated);

        // The request applies to one run only; the next one falls back to the setting (off).
        f.Checker.Check().Returns(new GameInstallStatus(true, true, "Install verified.", "101"));
        Assert.True((await f.Service.StartUpdateAsync(confirmStopRunningInstances: false, validate: false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        await f.SteamCmd.Received(1).InstallOrUpdateAsync(false, Arg.Any<CancellationToken>());
        var second = Assert.IsType<UpdateResult>(f.Service.Current.LastResult);
        Assert.False(second.BuildChanged);
        Assert.Equal("Already on the latest build (101).", second.Summary);
    }

    [Fact]
    public async Task HappyPath_WithNothingRunning_UpdatesWithoutStopsOrLaunches()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        var outcome = await f.Service.StartUpdateAsync(confirmStopRunningInstances: false, validate: false, ct);
        Assert.True(outcome.Succeeded, outcome.Error);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([MaintenancePhase.Stopping, MaintenancePhase.Updating, MaintenancePhase.None], f.DistinctPhases());
        Assert.Empty(f.Processes.Stops);
        Assert.Empty(f.Processes.Starts);
        await f.SteamCmd.Received(1).InstallOrUpdateAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUpdate_RefusesWithoutConfirmationWhileInstancesRun()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);

        var outcome = await f.Service.StartUpdateAsync(confirmStopRunningInstances: false, validate: false, ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("confirm", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MaintenancePhase.None, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
        Assert.False(f.Gate.IsHeldExclusively);
        Assert.False(f.Service.IsOperationInProgress);
    }

    [Fact]
    public async Task StartUpdate_RefusesWhileAnInstanceIsUnknown()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Unknown);

        var outcome = await f.Service.StartUpdateAsync(confirmStopRunningInstances: true, validate: false, ct);

        Assert.False(outcome.Succeeded);
        Assert.Equal(UpdateStateMachine.AmbiguousInstancesReason, outcome.Error);
        Assert.Equal(0, f.Gate.ExclusiveAcquisitions);
    }

    [Fact]
    public async Task StartUpdate_RefusesWhilePersistedStateIsUnresolved()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Restarting, [new MaintenanceEntry(f.Alpha.Id, Error: "port conflict")], ct);

        var outcome = await f.Service.StartUpdateAsync(confirmStopRunningInstances: true, validate: false, ct);

        Assert.False(outcome.Succeeded);
        Assert.Contains("unresolved", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MaintenancePhase.Restarting, f.Service.Current.Phase);
    }

    [Fact]
    public async Task StartUpdate_RefusesASecondUpdateWhileOneRuns()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);
        f.Processes.StartBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.WaitForPhaseAsync(MaintenancePhase.Restarting, ct);

        var second = await f.Service.StartUpdateAsync(true, false, ct);
        Assert.False(second.Succeeded);
        Assert.Equal(UpdateStateMachine.AlreadyRunningReason, second.Error);

        f.Processes.StartBarrier.SetResult();
        await f.Service.Completion.WaitAsync(_timeout, ct);
        Assert.True(f.Service.Current.IsResolved);
    }

    [Fact]
    public async Task SafetyCheck_RefusesWhenAProcessRemainsUnderDataRoot()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);
        f.Enumerator.Enumerate().Returns([new GameProcessInfo(4242, root.Layout.InstanceExecutable("ghost"), null, DateTimeOffset.UnixEpoch)]);

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([MaintenancePhase.Stopping, MaintenancePhase.None], f.DistinctPhases());
        Assert.Contains("PID 4242", f.Service.Current.Detail, StringComparison.Ordinal);
        Assert.Equal(MaintenancePhase.None, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
        await f.SteamCmd.DidNotReceive().InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Single(f.Processes.Stops);
        Assert.Empty(f.Processes.Starts);
        Assert.False(f.Gate.IsHeldExclusively);
        Assert.False(f.Service.IsOperationInProgress);
    }

    [Fact]
    public async Task SafetyCheck_IgnoresProcessesOutsideDataRoot()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Enumerator.Enumerate().Returns([new GameProcessInfo(1, @"C:\Elsewhere\ArkAscendedServer.exe", "elsewhere", DateTimeOffset.UnixEpoch)]);

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.True(f.Service.Current.IsResolved);
        await f.SteamCmd.Received(1).InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SteamCmdFailure_StaysUpdating_AndResumeRerunsIt()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);
        f.SteamCmd.InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(SteamCmdResult.Failure(8, "download failed"));

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal(MaintenancePhase.Updating, f.Service.Current.Phase);
        Assert.Contains("download failed", f.Service.Current.Detail, StringComparison.Ordinal);
        Assert.Equal(MaintenancePhase.Updating, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
        Assert.False(f.Service.IsOperationInProgress);
        Assert.False(f.Gate.IsHeldExclusively);
        Assert.False((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);

        f.SteamCmd.InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(SteamCmdResult.Success);
        await f.Service.ResumeAsync(ct);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.True(f.Service.Current.IsResolved);
        Assert.Equal([f.Alpha.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.Single(f.Processes.Stops);
    }

    [Fact]
    public async Task UnverifiedInstallAfterSteamCmd_StaysUpdating()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Checker.Check().Returns(new GameInstallStatus(true, false, "StateFlags 1026"));

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal(MaintenancePhase.Updating, f.Service.Current.Phase);
        Assert.Contains("StateFlags 1026", f.Service.Current.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_FromNone_DoesNothing()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);

        await f.Service.ResumeAsync(ct);

        Assert.True(f.Service.Current.IsResolved);
        Assert.Equal(0, f.Gate.ExclusiveAcquisitions);
        await f.SteamCmd.DidNotReceive().InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_FromInstalling_IsLeftToTheInstaller()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Installing, [], ct);

        await f.Service.ResumeAsync(ct);

        Assert.Equal(MaintenancePhase.Installing, f.Service.Current.Phase);
        Assert.Equal(0, f.Gate.ExclusiveAcquisitions);
        await f.SteamCmd.DidNotReceive().InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_FromStopping_ReStopsOnlyTheAliveEntries_ThenContinues()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Stopping, [new MaintenanceEntry(f.Alpha.Id), new MaintenanceEntry(f.Beta.Id)], ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);
        f.Processes.Set(f.Beta.Id, InstanceState.Stopped);
        f.Processes.StartBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var resume = f.Service.ResumeAsync(ct);
        await resume.WaitAsync(_timeout, ct);

        // Stops and SteamCMD ran to completion; the launches were handed off and are still pending.
        Assert.Equal([f.Alpha.Id], f.Processes.Stops.Select(s => s.InstanceId));
        await f.SteamCmd.Received(1).InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Equal(MaintenancePhase.Restarting, f.Service.Current.Phase);
        Assert.False(f.Gate.IsHeldExclusively);
        Assert.True(f.Service.IsOperationInProgress);
        Assert.False(f.Service.Completion.IsCompleted);

        f.Processes.StartBarrier.SetResult();
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([f.Alpha.Id, f.Beta.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.True(f.Service.Current.IsResolved);
        Assert.Equal(MaintenancePhase.None, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
    }

    [Fact]
    public async Task Resume_FromUpdating_RerunsSteamCmdWithoutStops()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Updating, [new MaintenanceEntry(f.Alpha.Id, Done: true)], ct);

        await f.Service.ResumeAsync(ct);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Empty(f.Processes.Stops);
        await f.SteamCmd.Received(1).InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Equal([f.Alpha.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.Equal(1, f.Gate.ExclusiveAcquisitions);
        Assert.True(f.Service.Current.IsResolved);
    }

    [Fact]
    public async Task Resume_FromRestarting_MarksAliveEntriesDoneAndLaunchesTheRest_ErrorsNeverBlock()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Restarting, [new MaintenanceEntry(f.Alpha.Id), new MaintenanceEntry(f.Beta.Id)], ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Starting); // reconciliation already found it alive
        f.Processes.StartOutcomes[f.Beta.Id] = OperationOutcome.Rejected("port conflict");

        await f.Service.ResumeAsync(ct);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([f.Beta.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.Equal(0, f.Gate.ExclusiveAcquisitions);
        await f.SteamCmd.DidNotReceive().InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var current = f.Service.Current;
        Assert.Equal(MaintenancePhase.Restarting, current.Phase);
        Assert.True(current.HasFailedEntries);
        Assert.Equal([new MaintenanceEntry(f.Alpha.Id, Done: true), new MaintenanceEntry(f.Beta.Id, Error: "port conflict")], current.Entries);
        Assert.Contains("port conflict", current.Detail, StringComparison.Ordinal);
        Assert.False(f.Service.IsOperationInProgress);
        Assert.Equal(current.Entries, (await TestSeed.MaintenanceRowAsync(root, ct)).Entries);
    }

    [Fact]
    public async Task Retry_RelaunchesTheErroredEntry_AndResolvesThePhase()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Restarting, [new MaintenanceEntry(f.Alpha.Id, Done: true), new MaintenanceEntry(f.Beta.Id, Error: "port conflict")], ct);

        Assert.False((await f.Service.RetryEntryAsync(f.Alpha.Id, ct)).Succeeded);
        var outcome = await f.Service.RetryEntryAsync(f.Beta.Id, ct);
        Assert.True(outcome.Succeeded, outcome.Error);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([f.Beta.Id], f.Processes.Starts.Select(s => s.InstanceId));
        Assert.True(f.Service.Current.IsResolved);
        Assert.Equal(MaintenancePhase.None, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
    }

    [Fact]
    public async Task Retry_ThatFailsAgain_RecordsTheNewError()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Restarting, [new MaintenanceEntry(f.Beta.Id, Error: "port conflict")], ct);
        f.Processes.StartOutcomes[f.Beta.Id] = OperationOutcome.Rejected("still conflicting");

        Assert.True((await f.Service.RetryEntryAsync(f.Beta.Id, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Equal([new MaintenanceEntry(f.Beta.Id, Error: "still conflicting")], f.Service.Current.Entries);
        Assert.Equal(MaintenancePhase.Restarting, f.Service.Current.Phase);
    }

    [Fact]
    public async Task Skip_MarksTheEntryDoneWithoutLaunching_AndResolvesWhenLast()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        await TestSeed.MaintenanceAsync(root, MaintenancePhase.Restarting, [new MaintenanceEntry(f.Alpha.Id, Error: "a"), new MaintenanceEntry(f.Beta.Id, Error: "b")], ct);

        Assert.True((await f.Service.SkipEntryAsync(f.Alpha.Id, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);
        Assert.Equal(MaintenancePhase.Restarting, f.Service.Current.Phase);
        Assert.Equal([new MaintenanceEntry(f.Alpha.Id, Done: true), new MaintenanceEntry(f.Beta.Id, Error: "b")], f.Service.Current.Entries);

        Assert.True((await f.Service.SkipEntryAsync(f.Beta.Id, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Empty(f.Processes.Starts);
        Assert.True(f.Service.Current.IsResolved);
        Assert.Equal(MaintenancePhase.None, (await TestSeed.MaintenanceRowAsync(root, ct)).Phase);
    }

    [Fact]
    public async Task Changed_IsRaisedForEveryPersistedTransition()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var f = await Fixture.CreateAsync(root, ct);
        f.Processes.Set(f.Alpha.Id, InstanceState.Running);

        Assert.True((await f.Service.StartUpdateAsync(true, false, ct)).Succeeded);
        await f.Service.Completion.WaitAsync(_timeout, ct);

        Assert.Contains(f.Snapshots, s => s.Phase == MaintenancePhase.Stopping && s.Entries.Single().Done);
        Assert.Contains(f.Snapshots, s => s.Phase == MaintenancePhase.Restarting && !s.Entries.Single().Done);
        Assert.Contains(f.Snapshots, s => s.Phase == MaintenancePhase.Restarting && s.Entries.Single().Done);
    }

    private sealed class Fixture
    {
        public required UpdateService Service { get; init; }

        public required FakeProcessManager Processes { get; init; }

        public required FakeMaintenanceGate Gate { get; init; }

        public required IGameProcessEnumerator Enumerator { get; init; }

        public required ISteamCmdRunner SteamCmd { get; init; }

        public required IGameInstallChecker Checker { get; init; }

        public required Instance Alpha { get; init; }

        public required Instance Beta { get; init; }

        public List<MaintenanceSnapshot> Snapshots { get; } = [];

        public List<MaintenancePhase> DistinctPhases()
        {
            var phases = new List<MaintenancePhase>();
            lock (Snapshots)
            {
                foreach (var snapshot in Snapshots)
                {
                    if (phases.Count == 0 || phases[^1] != snapshot.Phase)
                    {
                        phases.Add(snapshot.Phase);
                    }
                }
            }

            return phases;
        }

        public async Task WaitForPhaseAsync(MaintenancePhase phase, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + _timeout;
            while (Service.Current.Phase != phase)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {phase}; current is {Service.Current.Phase}.");
                await Task.Delay(20, ct);
            }
        }

        public static async Task<Fixture> CreateAsync(TempDataRoot root, CancellationToken ct)
        {
            await root.InitializeAsync(ct);
            var alpha = await TestSeed.InstanceAsync(root, "alpha", clustered: false, ct);
            var beta = await TestSeed.InstanceAsync(root, "beta", clustered: false, ct, i => { i.GamePort = 7779; i.RconPort = 27021; });

            var gate = new FakeMaintenanceGate();
            var processes = new FakeProcessManager(gate);
            processes.Set(alpha.Id, InstanceState.Stopped);
            processes.Set(beta.Id, InstanceState.Stopped);

            var enumerator = Substitute.For<IGameProcessEnumerator>();
            enumerator.Enumerate().Returns([]);
            var steamCmd = Substitute.For<ISteamCmdRunner>();
            steamCmd.InstallOrUpdateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(SteamCmdResult.Success);
            var checker = Substitute.For<IGameInstallChecker>();
            checker.Check().Returns(new GameInstallStatus(true, true, "Install verified."));
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(CancellationToken.None);

            var service = new UpdateService(
                root,
                root.Layout,
                new AppSettingsStore(root),
                processes,
                gate,
                enumerator,
                steamCmd,
                checker,
                new FakeConsoleService(),
                lifetime,
                TimeProvider.System,
                NullLogger<UpdateService>.Instance);

            var fixture = new Fixture
            {
                Service = service,
                Processes = processes,
                Gate = gate,
                Enumerator = enumerator,
                SteamCmd = steamCmd,
                Checker = checker,
                Alpha = alpha,
                Beta = beta,
            };
            service.Changed += snapshot =>
            {
                lock (fixture.Snapshots)
                {
                    fixture.Snapshots.Add(snapshot);
                }
            };
            return fixture;
        }
    }
}
