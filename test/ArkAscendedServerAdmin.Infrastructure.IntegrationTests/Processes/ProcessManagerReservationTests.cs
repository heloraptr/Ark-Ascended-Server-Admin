using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Backups;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Processes;

/// <summary>The projection reservation, the launch handoff order, and cluster reservations (B0).</summary>
public class ProcessManagerReservationTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReserveProjection_IsRefusedWhileASessionIsRegistered_OrTheTableIsBusyOrUnreadable_AndHeldWhenIdle()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        using var game = StandInProcess.Start();
        var row = game.As(root.Layout, "alpha");
        using var harness = new ProcessManagerHarness(root, [row], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        await harness.Manager.ReconcileAsync(Ct);

        var withSession = await harness.Manager.TryReserveProjectionAsync(Ct);
        Assert.False(withSession.Held);
        Assert.Contains($"instance {alpha}", withSession.RefusalReason, StringComparison.Ordinal);
        Assert.False(harness.Queue.Reservation.IsHeldExclusively);

        // Nothing registered from here on: the process table decides.
        using var idle = new ProcessManagerHarness(root, [row], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        var busy = await idle.Manager.TryReserveProjectionAsync(Ct);
        Assert.False(busy.Held);
        Assert.Contains("1 game process(es) running", busy.RefusalReason, StringComparison.Ordinal);

        idle.Enumerator.SnapshotOverride = () => new ProcessTableSnapshot([], false);
        var incomplete = await idle.Manager.TryReserveProjectionAsync(Ct);
        Assert.Contains("could not be read completely", incomplete.RefusalReason, StringComparison.Ordinal);

        idle.Enumerator.SnapshotOverride = () => throw new InvalidOperationException("wmi down");
        var failed = await idle.Manager.TryReserveProjectionAsync(Ct);
        Assert.False(failed.Held);

        idle.Enumerator.SnapshotOverride = () => new ProcessTableSnapshot([], true);
        var held = await idle.Manager.TryReserveProjectionAsync(Ct);
        Assert.True(held.Held, held.RefusalReason);
        Assert.True(idle.Queue.Reservation.IsHeldExclusively);
        held.Lease!.Dispose();
        Assert.False(idle.Queue.Reservation.IsHeldExclusively);
    }

    [Fact]
    public async Task Launch_RunsTheSynchronizerFirst_ThenWaitsForTheReservation_NeverRejected()
    {
        using var root = new TempDataRoot();
        var (alpha, _) = await SeedAsync(root);
        var synchronizer = new RecordingSynchronizer();
        using var harness = new ProcessManagerHarness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory(), synchronizer: synchronizer);
        var held = await harness.Manager.TryReserveProjectionAsync(Ct);
        Assert.True(held.Held, held.RefusalReason);

        var launch = harness.Manager.StartAsync(alpha, LaunchKind.User, Ct);
        await synchronizer.Ran.Task.WaitAsync(_wait, Ct);
        await Task.Delay(300, Ct);

        Assert.False(launch.IsCompleted, "the launch must wait on the reservation, not be rejected");
        Assert.Equal(0, harness.Queue.Reservation.SharedCount);

        held.Lease!.Dispose();
        var outcome = await launch.WaitAsync(_wait, Ct);

        // No game install here, so the launch fails after the handoff; what matters is that it got there.
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("reserved", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Queue.Reservation.SharedCount);
    }

    [Fact]
    public async Task Start_IsRefusedWhileTheInstancesClusterIsReserved()
    {
        using var root = new TempDataRoot();
        var (_, member) = await SeedAsync(root);
        using var harness = new ProcessManagerHarness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        int clusterId;
        await using (var db = root.CreateDbContext())
        {
            clusterId = (await db.Instances.SingleAsync(i => i.Id == member, Ct)).ClusterId!.Value;
        }

        using (harness.Locks.TryReserveCluster(clusterId))
        {
            var refused = await harness.Manager.StartAsync(member, LaunchKind.User, Ct);
            Assert.Contains("reserved by a restore", refused.Error, StringComparison.Ordinal);
        }

        var afterwards = await harness.Manager.StartAsync(member, LaunchKind.User, Ct);
        Assert.DoesNotContain("reserved", afterwards.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Locks.IsHeld(member));
    }

    /// <summary>B2: a restore journal keeps every affected launch refused until it is recovered or discarded.</summary>
    [Fact]
    public async Task Start_IsRefusedWhileARestoreJournalReferencesTheInstance()
    {
        using var root = new TempDataRoot();
        var (_, member) = await SeedAsync(root);
        using var harness = new ProcessManagerHarness(root, [], new FakeRconClient(RconFailure.Connect), new RecordingConsole(), new FakeOutputSourceFactory());
        var journals = new RestoreJournalStore(root.Layout);
        journals.Write(new RestoreJournal("member-op", DateTimeOffset.UnixEpoch, member, "member", "TheIsland_WP", null, null, [member], Path.Combine(root.Layout.Root, "safety"), "x.zip", RestorePhase.RollbackFailed));

        var refused = await harness.Manager.StartAsync(member, LaunchKind.User, Ct);
        Assert.Contains("incomplete restore (member-op)", refused.Error, StringComparison.Ordinal);
        Assert.False(harness.Locks.IsHeld(member));

        journals.Delete("member-op");
        var afterwards = await harness.Manager.StartAsync(member, LaunchKind.User, Ct);
        Assert.DoesNotContain("incomplete restore", afterwards.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seeds a standalone instance and a cluster member; returns their ids.</summary>
    private static async Task<(int Alpha, int Member)> SeedAsync(TempDataRoot root)
    {
        await root.InitializeAsync(Ct);
        await using var db = root.CreateDbContext();
        var map = await db.Maps.OrderBy(m => m.Id).FirstAsync(Ct);
        var cluster = new Cluster { Name = "Main", Slug = "main", ClusterKey = "main", CreatedAt = DateTimeOffset.UtcNow };
        db.Clusters.Add(cluster);
        var alpha = new Instance { Name = "Alpha", Slug = "alpha", SessionName = "Alpha", MapId = map.Id, GamePort = 7777, RconPort = 27020, CreatedAt = DateTimeOffset.UtcNow };
        var member = new Instance { Name = "Member", Slug = "member", SessionName = "Member", MapId = map.Id, GamePort = 7779, RconPort = 27021, Cluster = cluster, CreatedAt = DateTimeOffset.UtcNow };
        db.Instances.AddRange(alpha, member);
        await db.SaveChangesAsync(Ct);
        return (alpha.Id, member.Id);
    }

    private sealed class RecordingSynchronizer : IProjectionSynchronizer
    {
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunCycleAsync(CancellationToken cancellationToken)
        {
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
