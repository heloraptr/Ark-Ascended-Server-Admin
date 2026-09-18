using ArkAscendedServerAdmin.Maintenance;

namespace ArkAscendedServerAdmin.UnitTests.Maintenance;

/// <summary>The detached-job registry and its shutdown wait (B0).</summary>
public class DetachedJobsTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shutdown_RefusesNewJobs_AndReturnsOnceTheActiveOnesEnd()
    {
        var clock = new TestTimeProvider(_start);
        var jobs = new DetachedJobs(clock);
        var restore = jobs.TryBegin("restore alpha");

        var shutdown = jobs.ShutdownAsync(Ct);
        await Task.Delay(50, Ct);

        Assert.NotNull(restore);
        Assert.True(jobs.IsStopping);
        Assert.Null(jobs.TryBegin("delete beta"));
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(["restore alpha"], jobs.ActiveNames);

        restore.Dispose();
        Assert.Empty(await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(jobs.ActiveNames);
    }

    [Fact]
    public async Task Shutdown_GivesUpAfterTheWait_AndNamesWhatIsStillRunning()
    {
        var clock = new TestTimeProvider(_start);
        var jobs = new DetachedJobs(clock);
        using var stuck = jobs.TryBegin("restore alpha");

        var shutdown = jobs.ShutdownAsync(Ct);
        await WaitUntilAsync(() => clock.ActiveTimerCount == 1);
        clock.Advance(DetachedJobs.ShutdownWait);

        Assert.Equal(["restore alpha"], await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Shutdown_WithNothingActive_ReturnsAtOnce()
    {
        var jobs = new DetachedJobs(new TestTimeProvider(_start));
        using (jobs.TryBegin("delete alpha"))
        {
        }

        Assert.Empty(await jobs.ShutdownAsync(Ct));
        Assert.True(jobs.IsStopping);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, Ct);
        }
    }
}
