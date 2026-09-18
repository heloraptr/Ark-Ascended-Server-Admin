using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Maintenance;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>
/// The owned lifecycle operations (B0): a stop under a lease the caller holds, a restart under one lease with a
/// countdown to an absolute deadline, and launches refused while a session is registered. A stand-in process
/// (<see cref="StandInProcess"/>) plays the game server, so the stop's kill path is the real one.
/// </summary>
public class ProcessManagerLifecycleTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);

    /// <summary>No countdown and the shortest graceful timeout the settings allow: the fake doexit never exits the stand-in, so every stop ends in a kill.</summary>
    private static readonly AppSettings _fastStop = new() { PreStopBroadcastMinutes = 0, GracefulStopTimeoutSeconds = 5 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeleteOfARunningInstance_StopsItUnderTheDeleteLease_AndRemovesTheRows()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        var console = new RecordingConsole();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), console, new FakeOutputSourceFactory(), settings: _fastStop);
        await harness.Manager.ReconcileAsync(Ct);
        Assert.True(harness.Manager.GetRuntime(alpha).HasLiveProcess);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var delete = new InstanceDeleteService(root, root.Layout, harness.Locks, new DetachedJobs(TimeProvider.System), harness.Manager, new FakeFirewall(), new FakeLayoutService(), console, lifetime, TimeProvider.System, NullLogger<InstanceDeleteService>.Instance);

        var outcome = await delete.DeleteAsync(alpha, new InstanceDeleteOptions(KeepWorldData: false, DeleteBackups: true), Ct);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(game.Process.HasExited);
        Assert.False(harness.Locks.IsHeld(alpha));
        Assert.Equal(InstanceState.Stopped, harness.Manager.GetRuntime(alpha).State);
        await using var db = root.CreateDbContext();
        Assert.False(await db.Instances.AnyAsync(i => i.Id == alpha, Ct));
    }

    [Fact]
    public async Task RestartWithCountdown_BroadcastsToTheDeadline_SendsDoExitAtIt_AndKeepsTheLeaseThroughTheStart()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        var rcon = new FakeRconClient(failure: null);
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], rcon, new RecordingConsole(), new FakeOutputSourceFactory(), settings: _fastStop);
        await harness.Manager.ReconcileAsync(Ct);
        await WaitUntilAsync(() => harness.Manager.GetRuntime(alpha).State == InstanceState.Running);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);

        var outcome = await harness.Manager.RestartWithCountdownAsync(alpha, deadline, Ct);

        // The stop is verified by the kill (the fake doexit exits nothing); the queued start then fails for want of a game
        // install. What matters is that it got as far as the launch under the same lease instead of "operation in progress".
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(ProcessManager.OperationInProgress, outcome.Error, StringComparison.Ordinal);
        Assert.True(game.Process.HasExited);
        Assert.False(harness.Locks.IsHeld(alpha));
        var sent = rcon.Log.Where(entry => entry.Command != RconCommands.ListPlayers).ToList();
        Assert.Equal(
            [RconCommands.Broadcast("Server shutting down in 1 minute."), RconCommands.Broadcast("Server shutting down now."), RconCommands.DoExit],
            sent.Select(entry => entry.Command));
        Assert.True(sent[^1].At >= deadline - TimeSpan.FromMilliseconds(50), $"doexit went out at {sent[^1].At:O}, before the deadline {deadline:O}.");
    }

    [Fact]
    public async Task Start_IsRefusedWhileASessionIsRegistered()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var game = StandInProcess.Start();
        using var harness = new ProcessManagerHarness(root, [game.As(root.Layout, "alpha")], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        await harness.Manager.ReconcileAsync(Ct);

        var outcome = await harness.Manager.StartAsync(alpha, LaunchKind.User, Ct);

        Assert.Contains("already", outcome.Error, StringComparison.Ordinal);
        Assert.False(harness.Locks.IsHeld(alpha));
    }

    [Fact]
    public async Task StopUnderLease_RefusesAReleasedLease()
    {
        using var root = new TempDataRoot();
        var alpha = await SeedAlphaAsync(root);
        using var harness = new ProcessManagerHarness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        var lease = harness.Locks.TryAcquire(alpha)!;
        lease.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Manager.StopUnderLeaseAsync(lease, new StopOptions(), Ct));
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(25, Ct);
        }
    }
}
