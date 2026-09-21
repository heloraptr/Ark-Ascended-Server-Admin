using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>
/// The resource telemetry (B7) against a stand-in process: the liveness loop publishes a sample, the getter returns
/// it, the next sample waits for the publish interval without touching <c>RuntimeChanged</c>, and the exit publishes
/// null and clears the getter.
/// </summary>
public class ProcessManagerTelemetryTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALiveProcess_PublishesThrottledSamples_AndTheExitPublishesNull()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        var samples = new List<InstanceTelemetry>();
        var second = new TaskCompletionSource<InstanceTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeChanges = 0;
        harness.Manager.TelemetryChanged += (instanceId, sample) =>
        {
            if (sample is null)
            {
                cleared.TrySetResult(instanceId);
                return;
            }

            lock (samples)
            {
                samples.Add(sample);
                if (samples.Count == 2)
                {
                    second.TrySetResult(sample);
                }
            }
        };
        var before = DateTimeOffset.UtcNow;

        await harness.Manager.ReconcileAsync(Ct);
        Assert.Null(harness.Manager.GetTelemetry(alpha));
        harness.Manager.RuntimeChanged += _ => Interlocked.Increment(ref runtimeChanges);
        var latest = await second.Task.WaitAsync(_wait, Ct);

        InstanceTelemetry first;
        lock (samples)
        {
            first = samples[0];
        }

        Assert.Equal(alpha, first.InstanceId);
        Assert.True(first.WorkingSetBytes > 0, "the stand-in has a working set");
        Assert.InRange(first.CpuPercent, 0, 100);
        Assert.InRange(first.SampledAt, before, DateTimeOffset.UtcNow);
        Assert.True(latest.SampledAt - first.SampledAt >= TelemetrySampler.PublishInterval, $"the second sample came {(latest.SampledAt - first.SampledAt).TotalSeconds:0.0} s after the first.");
        Assert.Equal(latest, harness.Manager.GetTelemetry(alpha));
        Assert.Equal(0, Volatile.Read(ref runtimeChanges));

        game.Process.Kill();

        Assert.Equal(alpha, await cleared.Task.WaitAsync(_wait, Ct));
        Assert.Null(harness.Manager.GetTelemetry(alpha));
        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(alpha).State);
    }

    private static async Task<int> SeedAlphaAsync(TempDataRoot root)
    {
        await root.InitializeAsync(Ct);
        await using var db = root.CreateDbContext();
        var map = await db.Maps.OrderBy(m => m.Id).FirstAsync(Ct);
        var alpha = new Instance { Name = "Alpha", Slug = "alpha", SessionName = "Alpha", MapId = map.Id, GamePort = 7777, RconPort = 27020, CreatedAt = DateTimeOffset.UtcNow };
        db.Instances.Add(alpha);
        await db.SaveChangesAsync(Ct);
        return alpha.Id;
    }
}
