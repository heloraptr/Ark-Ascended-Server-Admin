using System.Diagnostics;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>Reconciliation (plan step 21) against a stub process list, with the current test process standing in for the game.</summary>
public class ProcessManagerReconcileTests
{
    private const string GeneratedIni = "[ServerSettings]\r\nServerAdminPassword=secret\r\nRCONPort=27020\r\nRCONEnabled=True\r\n";
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MatchingCandidate_IsAttached_AndTheRowRecordsThePid()
    {
        using var root = new TempDataRoot();
        var (alpha, beta) = await SeedAsync(root);
        var console = new RecordingConsole();
        var outputs = new FakeOutputSourceFactory();
        using var harness = new Harness(root, [CurrentProcessAs(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), console, outputs);

        await harness.Manager.ReconcileAsync(Ct);

        var runtime = harness.Manager.GetRuntime(alpha);
        Assert.Equal(InstanceState.Starting, runtime.State);
        Assert.Equal(Environment.ProcessId, runtime.Pid);
        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(beta).State);

        await using var db = root.CreateDbContext();
        var row = await db.Instances.SingleAsync(i => i.Id == alpha, Ct);
        Assert.Equal(InstanceState.Starting, row.State);
        Assert.Equal(Environment.ProcessId, row.LastPid);
        Assert.NotNull(row.LastProcessStartTime);
        Assert.Equal(InstanceState.Stopped, (await db.Instances.SingleAsync(i => i.Id == beta, Ct)).State);

        Assert.Contains(console.Snapshot(ConsoleChannels.Instance(alpha)), line => line.Kind == ConsoleLineKind.Info && line.Text.StartsWith("re-attached", StringComparison.Ordinal));
        await outputs.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(new OutputSourceOptions(true, new AppSettings().ConsoleBackfillLines), outputs.LastOptions);
        Assert.Equal([root.Layout.InstanceLogPath("alpha")], outputs.Paths);
    }

    [Fact]
    public async Task NoCandidate_MarksStopped()
    {
        using var root = new TempDataRoot();
        var (alpha, beta) = await SeedAsync(root, alphaLastPid: 123456);
        using var harness = new Harness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());

        await harness.Manager.ReconcileAsync(Ct);

        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(alpha).State);
        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(beta).State);
        await using var db = root.CreateDbContext();
        var row = await db.Instances.SingleAsync(i => i.Id == alpha, Ct);
        Assert.Equal(InstanceState.Stopped, row.State);
        Assert.Equal(123456, row.LastPid); // reconciliation clears nothing else
    }

    [Fact]
    public async Task TwoCandidates_MarkUnknown_WithThePidsInTheDetail()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        var first = CurrentProcessAs(root.Layout, "alpha");
        var second = first with { Pid = first.Pid + 1 };
        using var harness = new Harness(root, [first, second], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());

        await harness.Manager.ReconcileAsync(Ct);

        var runtime = harness.Manager.GetRuntime(alpha);
        Assert.Equal(InstanceState.Unknown, runtime.State);
        Assert.Contains(first.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), runtime.Detail, StringComparison.Ordinal);
        Assert.Contains(second.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), runtime.Detail, StringComparison.Ordinal);
        await using var db = root.CreateDbContext();
        Assert.Equal(InstanceState.Unknown, (await db.Instances.SingleAsync(i => i.Id == alpha, Ct)).State);
    }

    [Fact]
    public async Task SuccessfulProbe_PromotesToRunning_AndUpdatesTheRow()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        var rcon = new FakeRconClient(failure: null);
        using var harness = new Harness(root, [CurrentProcessAs(root.Layout, "alpha")], rcon, new RecordingConsole(), new FakeOutputSourceFactory());
        var changes = new List<InstanceRuntime>();
        harness.Manager.RuntimeChanged += changes.Add;

        await harness.Manager.ReconcileAsync(Ct);
        await WaitUntilAsync(() => harness.Manager.GetRuntime(alpha).State == InstanceState.Running);

        var runtime = harness.Manager.GetRuntime(alpha);
        Assert.NotNull(runtime.LastRconSuccessAt);
        Assert.Equal(Environment.ProcessId, runtime.Pid);
        Assert.Contains(RconCommands.ListPlayers, rcon.Commands);
        Assert.Contains(changes, change => change.State == InstanceState.Running);
        await WaitUntilAsync(() =>
        {
            using var db = root.CreateDbContext();
            return db.Instances.Single(i => i.Id == alpha).State == InstanceState.Running;
        });
    }

    [Fact]
    public async Task MissingPassword_IsUnreachableImmediately_ButStillWatched()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        var rcon = new FakeRconClient(failure: null);
        using var harness = new Harness(root, [CurrentProcessAs(root.Layout, "alpha")], rcon, new RecordingConsole(), new FakeOutputSourceFactory(), generatedIni: "[ServerSettings]\r\nRCONPort=27020\r\n");

        await harness.Manager.ReconcileAsync(Ct);

        var runtime = harness.Manager.GetRuntime(alpha);
        Assert.Equal(InstanceState.Unreachable, runtime.State);
        Assert.Contains("ServerAdminPassword", runtime.Detail, StringComparison.Ordinal);
        Assert.Equal(Environment.ProcessId, runtime.Pid);
        await Task.Delay(200, Ct);
        Assert.Empty(rcon.Commands);
        Assert.Equal(InstanceState.Unreachable, harness.Manager.GetRuntime(alpha).State);
        await using var db = root.CreateDbContext();
        Assert.Equal(InstanceState.Unreachable, (await db.Instances.SingleAsync(i => i.Id == alpha, Ct)).State);
    }

    [Fact]
    public async Task StopOnAnAttachedProcess_IsRefusedWhileAnotherOperationHoldsTheLock()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        using var harness = new Harness(root, [CurrentProcessAs(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        await harness.Manager.ReconcileAsync(Ct);

        using var held = harness.Locks.TryAcquire(alpha);
        var outcome = await harness.Manager.StopAsync(alpha, new StopOptions(), Ct);

        Assert.Equal(ProcessManager.OperationInProgress, outcome.Error);
        Assert.False(harness.Manager.TrySkipCountdown(alpha));
    }

    [Fact]
    public async Task UserStart_IsRefusedUntilReady_AndWhileTheGateIsExclusive()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        using var harness = new Harness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory(), ready: false);

        var notReady = await harness.Manager.StartAsync(alpha, LaunchKind.User, Ct);
        Assert.Contains("not ready", notReady.Error, StringComparison.Ordinal);

        harness.SetReady();
        using (await harness.Gate.AcquireExclusiveAsync(Ct))
        {
            var updating = await harness.Manager.StartAsync(alpha, LaunchKind.User, Ct);
            Assert.Equal(MaintenanceGate.UpdateInProgress, updating.Error);
        }
    }

    private static GameProcessInfo CurrentProcessAs(DataRootLayout layout, string slug)
    {
        using var current = Process.GetCurrentProcess();
        var executable = layout.InstanceExecutable(slug);
        return new GameProcessInfo(
            current.Id,
            executable,
            $"\"{executable}\" TheIsland_WP?listen?AltSaveDirectoryName={slug} -port=7777 -log",
            new DateTimeOffset(current.StartTime));
    }

    private static async Task<(int Alpha, int Beta)> SeedAsync(TempDataRoot root, int? alphaLastPid = null)
    {
        await root.InitializeAsync(Ct);
        await using var db = root.CreateDbContext();
        var map = await db.Maps.OrderBy(m => m.Id).FirstAsync(Ct);
        var alpha = new Instance { Name = "Alpha", Slug = "alpha", SessionName = "Alpha", MapId = map.Id, GamePort = 7777, RconPort = 27020, LastPid = alphaLastPid, CreatedAt = DateTimeOffset.UtcNow };
        var beta = new Instance { Name = "Beta", Slug = "beta", SessionName = "Beta", MapId = map.Id, GamePort = 7779, RconPort = 27021, CreatedAt = DateTimeOffset.UtcNow };
        db.Instances.AddRange(alpha, beta);
        await db.SaveChangesAsync(Ct);
        return (alpha.Id, beta.Id);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>A process manager wired to fakes; disposing fires the host lifetime so every loop ends.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly IReadinessMonitor _readiness = Substitute.For<IReadinessMonitor>();

        public Harness(
            TempDataRoot root,
            IReadOnlyList<GameProcessInfo> processes,
            FakeRconClient rcon,
            RecordingConsole console,
            FakeOutputSourceFactory outputs,
            string? generatedIni = GeneratedIni,
            bool ready = true)
        {
            var settings = Substitute.For<IAppSettingsStore>();
            settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings());
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(_stopping.Token);
            if (ready)
            {
                SetReady();
            }
            else
            {
                _readiness.Current.Returns(new ReadinessState(ReadinessPhase.Recovering, "Recovering", null, DateTimeOffset.UtcNow));
            }

            Gate = new MaintenanceGate();
            Locks = new InstanceLocks();
            Queue = new LaunchQueue(Gate, settings, TimeProvider.System);
            Manager = new ProcessManager(
                root,
                root.Layout,
                settings,
                new HostConfiguration(root.Layout.Root, ["https://localhost:5001"], [], false, true, false, "0.0.0-test"),
                _readiness,
                Locks,
                Gate,
                Queue,
                rcon,
                new StubEnumerator(processes),
                new FakeFirewall(),
                new FakeLayoutService(),
                new FakeConfigWriter(generatedIni),
                outputs,
                console,
                TimeProvider.System,
                lifetime,
                NullLogger<ProcessManager>.Instance);
        }

        public ProcessManager Manager { get; }

        public MaintenanceGate Gate { get; }

        public InstanceLocks Locks { get; }

        public LaunchQueue Queue { get; }

        public void SetReady() => _readiness.Current.Returns(new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow));

        public void Dispose()
        {
            _stopping.Cancel();
            Queue.Dispose();
            _stopping.Dispose();
        }
    }
}
