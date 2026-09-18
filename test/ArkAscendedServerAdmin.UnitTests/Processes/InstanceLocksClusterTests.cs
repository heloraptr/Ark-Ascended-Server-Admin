using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>Cluster reservations (B0) live beside the per-instance locks and never touch them.</summary>
public class InstanceLocksClusterTests
{
    [Fact]
    public void ReserveCluster_IsExclusivePerCluster_AndIndependentOfInstanceLocks()
    {
        var locks = new InstanceLocks();

        using var held = locks.TryReserveCluster(1);
        using var instance = locks.TryAcquire(7);

        Assert.NotNull(held);
        Assert.Null(locks.TryReserveCluster(1));
        Assert.True(locks.IsClusterReserved(1));
        Assert.False(locks.IsClusterReserved(2));
        Assert.NotNull(instance);
        Assert.NotNull(locks.TryReserveCluster(2));
    }

    [Fact]
    public void ReleasingTheReservation_FreesTheCluster()
    {
        var locks = new InstanceLocks();
        var held = locks.TryReserveCluster(3)!;

        held.Dispose();
        held.Dispose();

        Assert.False(locks.IsClusterReserved(3));
        using var again = locks.TryReserveCluster(3);
        Assert.NotNull(again);
    }
}
