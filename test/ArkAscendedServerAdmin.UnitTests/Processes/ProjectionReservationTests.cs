using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>The non-draining reservation launches hold shared and projections hold exclusively (B0).</summary>
public class ProjectionReservationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SharedHolders_Overlap_AndExclusiveWaitsForThemAll()
    {
        var reservation = new ProjectionReservation();
        var first = await reservation.AcquireSharedAsync(Ct);
        var second = await reservation.AcquireSharedAsync(Ct);

        var exclusive = reservation.AcquireExclusiveAsync(Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(2, reservation.SharedCount);
        Assert.False(exclusive.IsCompleted);
        Assert.True(reservation.IsHeldExclusively, "pending exclusive already blocks new shared holders");

        first.Dispose();
        await Task.Delay(50, Ct);
        Assert.False(exclusive.IsCompleted);

        second.Dispose();
        using var held = await exclusive.WaitAsync(_timeout, Ct);
        Assert.Equal(0, reservation.SharedCount);
    }

    [Fact]
    public async Task SharedAcquire_WaitsWhileExclusiveIsHeld_AndProceedsAfterRelease_NeverRejected()
    {
        var reservation = new ProjectionReservation();
        var exclusive = await reservation.AcquireExclusiveAsync(Ct);

        var shared = reservation.AcquireSharedAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(shared.IsCompleted);

        exclusive.Dispose();
        using var lease = await shared.WaitAsync(_timeout, Ct);
        Assert.Equal(1, reservation.SharedCount);
        Assert.False(reservation.IsHeldExclusively);
    }

    [Fact]
    public async Task ExclusiveAcquires_AreSerialized_AndACanceledWaitReleasesTheTurn()
    {
        var reservation = new ProjectionReservation();
        var shared = await reservation.AcquireSharedAsync(Ct);
        using var cancel = new CancellationTokenSource();

        var canceled = reservation.AcquireExclusiveAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(reservation.IsHeldExclusively);

        var next = await reservation.AcquireSharedAsync(Ct);
        shared.Dispose();
        next.Dispose();
        using var exclusive = await reservation.AcquireExclusiveAsync(Ct).WaitAsync(_timeout, Ct);
        var another = reservation.AcquireExclusiveAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(another.IsCompleted);
        exclusive.Dispose();
        (await another.WaitAsync(_timeout, Ct)).Dispose();
    }

    [Fact]
    public async Task DisposingALeaseTwice_ReleasesOnce()
    {
        var reservation = new ProjectionReservation();
        var lease = await reservation.AcquireSharedAsync(Ct);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, reservation.SharedCount);
    }
}
