using System.Collections.Concurrent;
using System.Diagnostics;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>
/// The crash policy (B4) against a scripted process manager: what it filters, how it retries a busy lock, the console
/// lines per result, and how its per-instance chains behave and shut down.
/// </summary>
public class CrashPolicyTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset _started = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequestedMaintenanceDisabledAndUnknownExits_AreIgnored_WithoutAskingTheManager()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.StartAsync();

        rig.Post(Request(rig.Alpha) with { StopIntent = true });
        rig.Post(Request(rig.Alpha) with { DuringMaintenance = true });
        rig.Post(Request(rig.Gamma));
        rig.Post(Request(999));
        var answered = Request(rig.Alpha);
        rig.Post(answered);
        await WaitUntilAsync(() => rig.Calls.Count >= 1);
        await Task.Delay(300, Ct);

        var call = Assert.Single(rig.Calls);
        Assert.Same(answered, call);
    }

    [Fact]
    public async Task ABusyLock_IsRetried_UntilTheManagerAnswers()
    {
        await using var rig = await Rig.CreateAsync();
        var answers = new ConcurrentQueue<CrashRecovery>([CrashRecovery.Busy, CrashRecovery.Busy, new CrashRecovery(CrashRecoveryStatus.Launched, 1)]);
        rig.Processes.RecoverHandler = (_, _) => Task.FromResult(answers.TryDequeue(out var next) ? next : CrashRecovery.Stale);
        await rig.StartAsync();

        rig.Post(Request(rig.Alpha));
        await WaitUntilAsync(() => rig.Calls.Count == 3);
        await Task.Delay(200, Ct);

        Assert.Equal(3, rig.Calls.Count);
        Assert.Empty(rig.Lines(rig.Alpha));
    }

    [Fact]
    public async Task ALockBusyForTheWholeWindow_WarnsOnce_AndStopsAsking()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Processes.RecoverHandler = (_, _) => Task.FromResult(CrashRecovery.Busy);
        await rig.StartAsync();

        rig.Post(Request(rig.Alpha));
        await WaitUntilAsync(() => rig.Lines(rig.Alpha).Count > 0);
        var calls = rig.Calls.Count;
        await Task.Delay(300, Ct);

        var line = Assert.Single(rig.Lines(rig.Alpha));
        Assert.Equal((CrashPolicy.BusyMessage, ConsoleLineKind.Warning), (line.Text, line.Kind));
        Assert.Equal(calls, rig.Calls.Count);
        Assert.InRange(calls, 100, 130);
    }

    [Fact]
    public async Task EachAnswer_WritesItsConsoleLine_OrOnlyLogs()
    {
        await using var rig = await Rig.CreateAsync();
        var answers = new ConcurrentQueue<CrashRecovery>(
        [
            new CrashRecovery(CrashRecoveryStatus.GaveUp, 3),
            new CrashRecovery(CrashRecoveryStatus.Refused, 1, "Port conflict: Port 7777 is in use."),
            new CrashRecovery(CrashRecoveryStatus.Skipped, 1, "update in progress"),
            new CrashRecovery(CrashRecoveryStatus.Skipped, 1, "The service is shutting down."),
            CrashRecovery.Disabled,
            CrashRecovery.Stale,
            new CrashRecovery(CrashRecoveryStatus.Launched, 2),
            new CrashRecovery(CrashRecoveryStatus.Launched, 3, "The server started (pid 1) but its identity could not be saved."),
        ]);
        rig.Processes.RecoverHandler = (_, _) => Task.FromResult(answers.TryDequeue(out var next) ? next : CrashRecovery.Stale);
        await rig.StartAsync();

        for (var i = 0; i < 8; i++)
        {
            rig.Post(Request(rig.Alpha));
        }

        await WaitUntilAsync(() => rig.Calls.Count == 8);
        await Task.Delay(200, Ct);

        Assert.Equal(
            [
                (CrashLoopRule.GaveUpMessage, ConsoleLineKind.Warning),
                ("Automatic restart was refused: Port conflict: Port 7777 is in use.", ConsoleLineKind.Warning),
                ("Automatic restart skipped: update in progress.", ConsoleLineKind.Info),
                ("Automatic restart skipped: The service is shutting down.", ConsoleLineKind.Info),
            ],
            rig.Lines(rig.Alpha).Select(line => (line.Text, line.Kind)));
    }

    [Fact]
    public async Task AThrowingManager_DoesNotEndTheReader()
    {
        await using var rig = await Rig.CreateAsync();
        var first = true;
        rig.Processes.RecoverHandler = (_, _) =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new CrashRecovery(CrashRecoveryStatus.Launched, 1));
        };
        await rig.StartAsync();

        rig.Post(Request(rig.Alpha));
        rig.Post(Request(rig.Beta));
        rig.Post(Request(rig.Alpha));

        await WaitUntilAsync(() => rig.Calls.Count == 3);
    }

    [Fact]
    public async Task TwoInstances_DoNotBlockEachOther()
    {
        await using var rig = await Rig.CreateAsync();
        var release = new TaskCompletionSource<CrashRecovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Processes.RecoverHandler = (request, _) => request.InstanceId == rig.Alpha
            ? release.Task
            : Task.FromResult(new CrashRecovery(CrashRecoveryStatus.Launched, 1));
        await rig.StartAsync();

        rig.Post(Request(rig.Alpha));
        await WaitUntilAsync(() => rig.Calls.Count == 1);
        rig.Post(Request(rig.Beta));

        await WaitUntilAsync(() => rig.Calls.Any(call => call.InstanceId == rig.Beta));
        Assert.False(release.Task.IsCompleted);
        release.SetResult(new CrashRecovery(CrashRecoveryStatus.Launched, 1));
    }

    [Fact]
    public async Task Stop_WaitsForAChainInFlight()
    {
        await using var rig = await Rig.CreateAsync();
        var release = new TaskCompletionSource<CrashRecovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Processes.RecoverHandler = (_, _) => release.Task;
        await rig.StartAsync();
        rig.Post(Request(rig.Alpha));
        await WaitUntilAsync(() => rig.Calls.Count == 1);

        var stopping = rig.Policy.StopAsync(CancellationToken.None);
        await Task.Delay(300, Ct);

        Assert.False(stopping.IsCompleted, "the policy must wait for the relaunch in flight");
        release.SetResult(new CrashRecovery(CrashRecoveryStatus.Launched, 1));
        await stopping.WaitAsync(_wait, Ct);
    }

    [Fact]
    public async Task AChainInItsPause_EndsOnTheStoppingToken()
    {
        await using var rig = await Rig.CreateAsync(TimeProvider.System);
        await rig.StartAsync();
        rig.Post(Request(rig.Alpha));
        await Task.Delay(300, Ct);
        var watch = Stopwatch.StartNew();

        await rig.Policy.StopAsync(CancellationToken.None).WaitAsync(_wait, Ct);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the stop took {watch.Elapsed}");
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task NothingIsRead_UntilTheServiceIsReady()
    {
        await using var rig = await Rig.CreateAsync(ready: false);
        await rig.StartAsync();
        rig.Post(Request(rig.Alpha));
        await Task.Delay(300, Ct);

        Assert.Empty(rig.Calls);

        var ready = new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow);
        rig.Readiness.Current.Returns(ready);
        rig.Readiness.Changed += Raise.Event<Action<ReadinessState>>(ready);

        await WaitUntilAsync(() => rig.Calls.Count == 1);
    }

    private static RecoveryRequest Request(int instanceId) =>
        new(instanceId, 4242, _started, 1, false, _started + TimeSpan.FromMinutes(1), false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>Alpha and Beta have AutoRestart on, Gamma off; the policy runs on a fast clock unless told otherwise.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDataRoot _root;
        private readonly RecoveryRequests _requests = new();
        private readonly FakeConsoleService _console = new();
        private bool _running;

        private Rig(TempDataRoot root, TimeProvider clock, bool ready)
        {
            _root = root;
            if (ready)
            {
                Readiness.Current.Returns(new ReadinessState(ReadinessPhase.Ready, "Ready", null, DateTimeOffset.UtcNow));
            }
            else
            {
                Readiness.Current.Returns(new ReadinessState(ReadinessPhase.Recovering, "Recovering", null, DateTimeOffset.UtcNow));
            }

            Policy = new CrashPolicy(_requests, Processes, root, Readiness, _console, clock, NullLogger<CrashPolicy>.Instance);
        }

        public FakeProcessManager Processes { get; } = new();

        public IReadinessMonitor Readiness { get; } = Substitute.For<IReadinessMonitor>();

        public CrashPolicy Policy { get; }

        public int Alpha { get; private set; }

        public int Beta { get; private set; }

        public int Gamma { get; private set; }

        public IReadOnlyList<RecoveryRequest> Calls
        {
            get
            {
                lock (Processes.Recoveries)
                {
                    return Processes.Recoveries.Select(call => call.Request).ToList();
                }
            }
        }

        public static async Task<Rig> CreateAsync(TimeProvider? clock = null, bool ready = true)
        {
            var root = new TempDataRoot();
            await root.InitializeAsync(Ct);
            var rig = new Rig(root, clock ?? new FastTimeProvider(_started), ready)
            {
                Alpha = (await TestSeed.InstanceAsync(root, "alpha", clustered: false, Ct, i => i.AutoRestart = true)).Id,
                Beta = (await TestSeed.InstanceAsync(root, "beta", clustered: false, Ct, i => i.AutoRestart = true)).Id,
                Gamma = (await TestSeed.InstanceAsync(root, "gamma", clustered: false, Ct)).Id,
            };
            return rig;
        }

        public async Task StartAsync()
        {
            await Policy.StartAsync(Ct);
            _running = true;
        }

        public void Post(RecoveryRequest request) => Assert.True(_requests.TryPost(request));

        public IReadOnlyList<ConsoleLine> Lines(int instanceId) => _console.Snapshot(ConsoleChannels.Instance(instanceId));

        public async ValueTask DisposeAsync()
        {
            if (_running)
            {
                await Policy.StopAsync(CancellationToken.None);
            }

            Policy.Dispose();
            _root.Dispose();
        }
    }
}
