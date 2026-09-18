using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>The session probe, the probe observations, and the recovery requests posted after an exit (B0).</summary>
public class ProcessManagerProbeTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);
    private static readonly AppSettings _fastStop = new() { PreStopBroadcastMinutes = 0, GracefulStopTimeoutSeconds = 5 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProbeSession_ReportsAlive_Dead_AndUnknown_FromOneTargetedRead()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        var row = game.As(root.Layout, "alpha");
        using var harness = new ProcessManagerHarness(root, [row], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        await harness.Manager.ReconcileAsync(Ct);

        Assert.Equal(SessionLiveness.Unknown, await harness.Manager.ProbeSessionAsync(999, Ct));
        Assert.Equal(SessionLiveness.Alive, await harness.Manager.ProbeSessionAsync(alpha, Ct));

        harness.Enumerator.ReadRowOverride = _ => ProcessRowRead.Missing;
        Assert.Equal(SessionLiveness.Dead, await harness.Manager.ProbeSessionAsync(alpha, Ct));

        harness.Enumerator.ReadRowOverride = _ => new ProcessRowRead(ProcessRowStatus.Complete, row with { CreationTime = row.CreationTime + TimeSpan.FromMinutes(1) });
        Assert.Equal(SessionLiveness.Dead, await harness.Manager.ProbeSessionAsync(alpha, Ct));

        harness.Enumerator.ReadRowOverride = _ => new ProcessRowRead(ProcessRowStatus.Incomplete, row with { ExecutablePath = null });
        Assert.Equal(SessionLiveness.Unknown, await harness.Manager.ProbeSessionAsync(alpha, Ct));

        harness.Enumerator.ReadRowOverride = _ => throw new InvalidOperationException("wmi down");
        Assert.Equal(SessionLiveness.Unknown, await harness.Manager.ProbeSessionAsync(alpha, Ct));
    }

    [Fact]
    public async Task SuccessfulProbe_RaisesAnObservation_TaggedWithTheSession()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(failure: null), new RecordingConsole(), new FakeOutputSourceFactory());
        var observed = new TaskCompletionSource<ProbeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Manager.ProbeObserved += observation => observed.TrySetResult(observation);
        var before = DateTimeOffset.UtcNow;

        await harness.Manager.ReconcileAsync(Ct);
        var observation = await observed.Task.WaitAsync(_wait, Ct);

        Assert.Equal((alpha, game.Process.Id, RconCommands.NoPlayersReply), (observation.InstanceId, observation.Pid, observation.Reply));
        Assert.Equal(harness.Manager.GetRuntime(alpha).ProcessStartTime, observation.ProcessStartTime);
        Assert.InRange(observation.SentAt, before - TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AnUnexpectedExit_PostsOneRecoveryRequest_WithoutStopIntent()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        await harness.Manager.ReconcileAsync(Ct);
        var pid = game.Process.Id;

        game.Process.Kill(entireProcessTree: true);
        var request = await harness.Recovery.Reader.ReadAsync(Ct).AsTask().WaitAsync(_wait, Ct);

        Assert.Equal((alpha, pid, false), (request.InstanceId, request.Pid, request.StopIntent));
        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(alpha).State);
        Assert.Contains("unexpectedly", harness.Manager.GetRuntime(alpha).Detail, StringComparison.Ordinal);
        Assert.False(harness.Recovery.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AManagerStop_PostsItsRecoveryRequest_WithStopIntent()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory(), settings: _fastStop);
        await harness.Manager.ReconcileAsync(Ct);

        var stopped = await harness.Manager.StopAsync(alpha, new StopOptions(), Ct);
        var request = await harness.Recovery.Reader.ReadAsync(Ct).AsTask().WaitAsync(_wait, Ct);

        Assert.True(stopped.Succeeded, stopped.Error);
        Assert.True(request.StopIntent);
        Assert.Null(harness.Manager.GetRuntime(alpha).Detail);
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
