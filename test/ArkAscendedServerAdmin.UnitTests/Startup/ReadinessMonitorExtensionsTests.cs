using ArkAscendedServerAdmin.Startup;

namespace ArkAscendedServerAdmin.UnitTests.Startup;

/// <summary>
/// The shared readiness wait the background services run before touching the database: it returns once the
/// pipeline is Ready, ends on cancellation, and never leaves its handler subscribed.
/// </summary>
public class ReadinessMonitorExtensionsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AlreadyReady_ReturnsAtOnce()
    {
        var monitor = new StubReadinessMonitor(ReadinessPhase.Ready);

        var wait = monitor.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
        Assert.Equal(0, monitor.Subscribers);
    }

    [Fact]
    public async Task ChangedReportsReady_Completes()
    {
        var monitor = new StubReadinessMonitor(ReadinessPhase.Initializing);

        var wait = monitor.WaitUntilReadyAsync(TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, monitor.Subscribers);

        monitor.Report(ReadinessPhase.Recovering);
        Assert.False(wait.IsCompleted);

        monitor.Report(ReadinessPhase.Ready);
        await wait.WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(0, monitor.Subscribers);
    }

    [Fact]
    public async Task ReadyBetweenCheckAndSubscription_ReturnsWithoutWaiting()
    {
        var monitor = new StubReadinessMonitor(ReadinessPhase.Initializing) { ReadyOnSubscribe = true };

        await monitor.WaitUntilReadyAsync(TestContext.Current.CancellationToken).WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(0, monitor.Subscribers);
    }

    [Fact]
    public async Task Canceled_ThrowsOperationCanceled()
    {
        var monitor = new StubReadinessMonitor(ReadinessPhase.Failed);
        using var cancellation = new CancellationTokenSource();

        var wait = monitor.WaitUntilReadyAsync(cancellation.Token);
        Assert.Equal(1, monitor.Subscribers);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, monitor.Subscribers);
    }

    /// <summary>A readiness monitor whose state the test sets, counting the handlers subscribed to <see cref="Changed"/>.</summary>
    private sealed class StubReadinessMonitor(ReadinessPhase phase) : IReadinessMonitor
    {
        private Action<ReadinessState>? _changed;

        public ReadinessState Current { get; private set; } = State(phase);

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        /// <summary>Reports Ready as the handler is added, the race the second readiness check covers.</summary>
        public bool ReadyOnSubscribe { get; init; }

        public event Action<ReadinessState>? Changed
        {
            add
            {
                _changed += value;
                if (ReadyOnSubscribe)
                {
                    Current = State(ReadinessPhase.Ready);
                }
            }
            remove => _changed -= value;
        }

        public void Report(ReadinessPhase next)
        {
            Current = State(next);
            _changed?.Invoke(Current);
        }

        private static ReadinessState State(ReadinessPhase phase) => new(phase, phase.ToString(), null, DateTimeOffset.UnixEpoch);
    }
}
