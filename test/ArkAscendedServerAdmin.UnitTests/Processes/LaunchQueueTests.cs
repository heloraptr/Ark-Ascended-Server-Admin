using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>Launch queue + maintenance gate + instance lock ordering with a fake clock (plan step 33).</summary>
public class LaunchQueueTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _realTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Launches_AreStaggeredByTheSetting()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings { StaggerDelaySeconds = 30 }), clock);
        var launchedAt = new List<DateTimeOffset>();
        Task<OperationOutcome> Launch(int id) => queue.EnqueueAsync(id, LaunchKind.User, _ =>
        {
            launchedAt.Add(clock.GetUtcNow());
            return Task.FromResult(OperationOutcome.Success);
        }, Ct);

        var first = Launch(1);
        var second = Launch(2);

        Assert.True((await first.WaitAsync(_realTimeout, Ct)).Succeeded);
        await WaitUntilAsync(() => clock.ActiveTimerCount == 1);
        Assert.False(second.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(second.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await second.WaitAsync(_realTimeout, Ct)).Succeeded);
        Assert.Equal([_start, _start + TimeSpan.FromSeconds(30)], launchedAt);
    }

    [Fact]
    public async Task RejectedLaunch_DoesNotStartTheStaggerClock()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings { StaggerDelaySeconds = 30 }), clock);

        var first = await queue.EnqueueAsync(1, LaunchKind.User, _ => Task.FromResult(OperationOutcome.Rejected("port conflict")), Ct).WaitAsync(_realTimeout, Ct);
        var second = await queue.EnqueueAsync(2, LaunchKind.User, _ => Task.FromResult(OperationOutcome.Success), Ct).WaitAsync(_realTimeout, Ct);

        Assert.False(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(_start, clock.GetUtcNow());
    }

    [Fact]
    public async Task LaunchEnqueuedAfterExclusiveAcquire_IsRejected()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings()), clock);
        var launched = false;

        using var exclusive = await gate.AcquireExclusiveAsync(Ct);
        var outcome = await queue.EnqueueAsync(1, LaunchKind.User, _ =>
        {
            launched = true;
            return Task.FromResult(OperationOutcome.Success);
        }, Ct).WaitAsync(_realTimeout, Ct);

        Assert.False(outcome.Succeeded);
        Assert.Equal(MaintenanceGate.UpdateInProgress, outcome.Error);
        Assert.False(launched);
    }

    [Fact]
    public async Task QueuedLaunches_AreDrainedWithTheReason_WhileTheInFlightOneContinues()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings()), clock);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.EnqueueAsync(1, LaunchKind.User, async _ =>
        {
            firstStarted.SetResult();
            await release.Task;
            return OperationOutcome.Success;
        }, Ct);
        await firstStarted.Task.WaitAsync(_realTimeout, Ct);
        var second = queue.EnqueueAsync(2, LaunchKind.User, _ => Task.FromResult(OperationOutcome.Success), Ct);
        var third = queue.EnqueueAsync(3, LaunchKind.Recovery, _ => Task.FromResult(OperationOutcome.Success), Ct);

        queue.Drain("update in progress");

        Assert.Equal("update in progress", (await second.WaitAsync(_realTimeout, Ct)).Error);
        Assert.Equal("update in progress", (await third.WaitAsync(_realTimeout, Ct)).Error);
        Assert.False(first.IsCompleted);
        Assert.Equal(0, queue.PendingCount);

        release.SetResult();
        Assert.True((await first.WaitAsync(_realTimeout, Ct)).Succeeded);
    }

    [Fact]
    public async Task ExclusiveAcquire_DrainsTheQueue_AndWaitsForTheInFlightSharedLease()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings()), clock);
        var registered = new List<int>();
        var leaseTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The launch callback mirrors the process manager: lease before "Process.Start", release after persistence.
        var inFlight = queue.EnqueueAsync(1, LaunchKind.User, async _ =>
        {
            using var lease = gate.TryAcquireShared();
            Assert.NotNull(lease);
            leaseTaken.SetResult();
            await persisted.Task;
            registered.Add(4242);
            return OperationOutcome.Success;
        }, Ct);
        await leaseTaken.Task.WaitAsync(_realTimeout, Ct);
        var queued = queue.EnqueueAsync(2, LaunchKind.User, _ => Task.FromResult(OperationOutcome.Success), Ct);

        var exclusive = gate.AcquireExclusiveAsync(Ct);

        Assert.Equal(MaintenanceGate.UpdateInProgress, (await queued.WaitAsync(_realTimeout, Ct)).Error);
        Assert.True(gate.IsHeldExclusively);
        await Task.Delay(50, Ct);
        Assert.False(exclusive.IsCompleted);
        Assert.Empty(registered);

        persisted.SetResult();
        using (await exclusive.WaitAsync(_realTimeout, Ct))
        {
            // An update collecting the running set after the gate sees the newly registered process.
            Assert.Equal([4242], registered);
            Assert.True((await inFlight.WaitAsync(_realTimeout, Ct)).Succeeded);
            Assert.Null(gate.TryAcquireShared());
        }

        Assert.False(gate.IsHeldExclusively);
        using var afterwards = gate.TryAcquireShared();
        Assert.NotNull(afterwards);
    }

    [Fact]
    public async Task PersistenceThatNeverSucceeds_EndsRejected_AndReleasesGateAndLock()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        var locks = new InstanceLocks();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings()), clock);
        var attempts = 0;

        var instanceLock = locks.TryAcquire(7);
        Assert.NotNull(instanceLock);
        Task<OperationOutcome> launch;
        try
        {
            launch = queue.EnqueueAsync(7, LaunchKind.User, async token =>
            {
                var lease = gate.TryAcquireShared();
                Assert.NotNull(lease);
                try
                {
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        if (attempt > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(3), clock, token);
                        }

                        attempts++;
                    }

                    return OperationOutcome.Rejected("database is locked");
                }
                finally
                {
                    lease.Dispose();
                }
            }, Ct);

            for (var i = 0; i < 3; i++)
            {
                await WaitUntilAsync(() => clock.ActiveTimerCount == 1);
                Assert.Equal(1, gate.SharedLeaseCount);
                clock.Advance(TimeSpan.FromSeconds(3));
            }

            var outcome = await launch.WaitAsync(_realTimeout, Ct);
            Assert.False(outcome.Succeeded);
            Assert.Equal("database is locked", outcome.Error);
            Assert.Equal(4, attempts);
        }
        finally
        {
            instanceLock.Dispose();
        }

        Assert.Equal(0, gate.SharedLeaseCount);
        using var stopLock = locks.TryAcquire(7);
        Assert.NotNull(stopLock);
        using var exclusive = await gate.AcquireExclusiveAsync(Ct).WaitAsync(_realTimeout, Ct);
    }

    [Fact]
    public async Task CallerCancellationWhileQueued_RejectsWithoutLaunching()
    {
        var clock = new TestTimeProvider(_start);
        var gate = new MaintenanceGate();
        using var queue = new LaunchQueue(gate, new StubSettings(new AppSettings()), clock);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLaunched = false;

        var first = queue.EnqueueAsync(1, LaunchKind.User, async _ =>
        {
            firstStarted.SetResult();
            await release.Task;
            return OperationOutcome.Success;
        }, Ct);
        await firstStarted.Task.WaitAsync(_realTimeout, Ct);
        using var cancellation = new CancellationTokenSource();
        var second = queue.EnqueueAsync(2, LaunchKind.User, _ =>
        {
            secondLaunched = true;
            return Task.FromResult(OperationOutcome.Success);
        }, cancellation.Token);

        cancellation.Cancel();
        var outcome = await second.WaitAsync(_realTimeout, Ct);
        release.SetResult();
        await first.WaitAsync(_realTimeout, Ct);

        Assert.False(outcome.Succeeded);
        await Task.Delay(50, Ct);
        Assert.False(secondLaunched);
    }

    [Fact]
    public void InstanceLock_ReturnsNullWhileHeld_AndWorksAgainAfterDispose()
    {
        var locks = new InstanceLocks();

        var held = locks.TryAcquire(1);
        Assert.NotNull(held);
        Assert.Null(locks.TryAcquire(1));
        Assert.True(locks.IsHeld(1));
        using (var other = locks.TryAcquire(2))
        {
            Assert.NotNull(other);
        }

        held.Dispose();
        held.Dispose();
        Assert.False(locks.IsHeld(1));
        using var again = locks.TryAcquire(1);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task InstanceLock_AcquireAsync_WaitsForTheHolder()
    {
        var locks = new InstanceLocks();
        var held = locks.TryAcquire(1);
        Assert.NotNull(held);

        var waiting = locks.AcquireAsync(1, Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var acquired = await waiting.WaitAsync(_realTimeout, Ct);
        Assert.Null(locks.TryAcquire(1));
    }

    [Fact]
    public async Task SharedLease_IsRefusedWhileExclusive_AndWorksAgainAfterRelease()
    {
        var gate = new MaintenanceGate();

        Assert.False(gate.IsHeldExclusively);
        var exclusive = await gate.AcquireExclusiveAsync(Ct);
        Assert.True(gate.IsHeldExclusively);
        Assert.Null(gate.TryAcquireShared());

        var secondExclusive = gate.AcquireExclusiveAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(secondExclusive.IsCompleted);

        exclusive.Dispose();
        exclusive.Dispose();
        using (await secondExclusive.WaitAsync(_realTimeout, Ct))
        {
            Assert.True(gate.IsHeldExclusively);
        }

        Assert.False(gate.IsHeldExclusively);
        using var shared = gate.TryAcquireShared();
        Assert.NotNull(shared);
        Assert.Equal(1, gate.SharedLeaseCount);
    }

    [Fact]
    public async Task CanceledExclusiveWait_ReopensTheGate()
    {
        var gate = new MaintenanceGate();
        var shared = gate.TryAcquireShared();
        Assert.NotNull(shared);
        using var cancellation = new CancellationTokenSource();

        var exclusive = gate.AcquireExclusiveAsync(cancellation.Token);
        Assert.True(gate.IsHeldExclusively);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exclusive.WaitAsync(_realTimeout, Ct));

        Assert.False(gate.IsHeldExclusively);
        using var another = gate.TryAcquireShared();
        Assert.NotNull(another);
        shared.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _realTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class StubSettings(AppSettings settings) : IAppSettingsStore
    {
        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default) => Task.FromResult(mutate(settings));
    }
}
