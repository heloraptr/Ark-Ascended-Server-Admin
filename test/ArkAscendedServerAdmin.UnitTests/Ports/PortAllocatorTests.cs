using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Ports;

namespace ArkAscendedServerAdmin.UnitTests.Ports;

public class PortAllocatorTests
{
    private const int HostPort = 5000;

    private static PortAllocator Defaults() => new(new AppSettings());

    [Fact]
    public void NoInstances_GiveDefaultStarts()
    {
        var allocator = Defaults();

        Assert.Equal(7777, allocator.NextGamePort([], HostPort));
        Assert.Equal(27020, allocator.NextRconPort([], HostPort));
    }

    [Fact]
    public void NextGamePort_StepsPastOwnedRange()
    {
        var owners = new[] { new PortOwner("Island", 7777, 27020) };

        Assert.Equal(7779, Defaults().NextGamePort(owners, HostPort));
    }

    [Fact]
    public void NextGamePort_OwnerAt7778_BlocksBothNeighbors()
    {
        // 7778 occupies 7778 and 7779: 7777 collides through port + 1, 7779 collides directly.
        var owners = new[] { new PortOwner("Odd", 7778, 27020) };

        Assert.Equal(7781, Defaults().NextGamePort(owners, HostPort));
    }

    [Fact]
    public void NextGamePort_SkipsHostPort()
    {
        var owners = new[] { new PortOwner("Island", 7777, 27020) };

        Assert.Equal(7781, Defaults().NextGamePort(owners, hostPort: 7779));
    }

    [Fact]
    public void NextGamePort_HostPortOnPlusOne_SkipsCandidate()
    {
        Assert.Equal(7779, Defaults().NextGamePort([], hostPort: 7778));
    }

    [Fact]
    public void NextGamePort_SkipsAnotherOwnersRconPort()
    {
        var owners = new[] { new PortOwner("Weird", 9000, 7778) };

        Assert.Equal(7779, Defaults().NextGamePort(owners, HostPort));
    }

    [Fact]
    public void NextRconPort_StepsPastOwnedPort()
    {
        var owners = new[] { new PortOwner("Island", 7777, 27020), new PortOwner("Center", 7779, 27021) };

        Assert.Equal(27022, Defaults().NextRconPort(owners, HostPort));
    }

    [Fact]
    public void NextRconPort_SkipsGameRangeAndHostPort()
    {
        // 27019 occupies 27019 and 27020; the host sits on 27021.
        var owners = new[] { new PortOwner("Clash", 27019, 9000) };

        Assert.Equal(27022, Defaults().NextRconPort(owners, hostPort: 27021));
    }

    [Fact]
    public void CustomStartAndStep_AreHonored()
    {
        var allocator = new PortAllocator(new AppSettings
        {
            GamePortStart = 8000,
            GamePortStep = 10,
            RconPortStart = 30000,
            RconPortStep = 5,
        });
        var owners = new[] { new PortOwner("A", 8000, 30000) };

        Assert.Equal(8010, allocator.NextGamePort(owners, HostPort));
        Assert.Equal(30005, allocator.NextRconPort(owners, HostPort));
    }

    [Fact]
    public void NextGamePort_Exhausted_Throws()
    {
        var allocator = new PortAllocator(new AppSettings { GamePortStart = 65533, GamePortStep = 2 });
        var owners = new[] { new PortOwner("Last", 65533, 27020) };

        Assert.Throws<InvalidOperationException>(() => allocator.NextGamePort(owners, HostPort));
    }

    [Fact]
    public void NextGamePort_StartAtCeiling_ReturnsCeiling()
    {
        var allocator = new PortAllocator(new AppSettings { GamePortStart = 65534 });

        Assert.Equal(65534, allocator.NextGamePort([], HostPort));
    }

    [Fact]
    public void NextRconPort_Exhausted_Throws()
    {
        var allocator = new PortAllocator(new AppSettings { RconPortStart = 65535 });
        var owners = new[] { new PortOwner("Last", 7777, 65535) };

        Assert.Throws<InvalidOperationException>(() => allocator.NextRconPort(owners, HostPort));
    }

    [Fact]
    public void FindConflicts_CleanCandidate_IsEmpty()
    {
        var candidate = new PortOwner("New", 7779, 27021);
        var others = new[] { new PortOwner("Island", 7777, 27020) };

        var conflicts = Defaults().FindConflicts(candidate, others, HostPort, new HashSet<int> { 7777 }, new HashSet<int> { 27020 });

        Assert.Empty(conflicts);
    }

    [Theory]
    [InlineData(0, 27020, 0)]
    [InlineData(65535, 27020, 65535)]
    [InlineData(7777, 0, 0)]
    [InlineData(7777, 65536, 65536)]
    public void FindConflicts_ReportsOutOfRangePorts(int gamePort, int rconPort, int reported)
    {
        var conflict = Assert.Single(Defaults().FindConflicts(new PortOwner("X", gamePort, rconPort), [], HostPort));

        Assert.Equal(reported, conflict.Port);
    }

    [Theory]
    [InlineData(7777)]
    [InlineData(7778)]
    public void FindConflicts_ReportsRconInsideOwnGameRange(int rconPort)
    {
        var conflict = Assert.Single(Defaults().FindConflicts(new PortOwner("X", 7777, rconPort), [], HostPort));

        Assert.Equal(rconPort, conflict.Port);
        Assert.Contains("own game port range", conflict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflicts_ReportsOverlapWithOtherOwnersGameRange()
    {
        // Candidate 7776 occupies 7776 and 7777; the other occupies 7777 and 7778.
        var candidate = new PortOwner("New", 7776, 27021);
        var others = new[] { new PortOwner("Island", 7777, 27020) };

        var conflict = Assert.Single(Defaults().FindConflicts(candidate, others, HostPort));

        Assert.Equal(7777, conflict.Port);
        Assert.Contains("'Island'", conflict.Reason, StringComparison.Ordinal);
        Assert.Contains("game port range 7777-7778", conflict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflicts_ReportsGamePortOnOtherOwnersRconPort()
    {
        var candidate = new PortOwner("New", 27019, 30000);
        var others = new[] { new PortOwner("Island", 7777, 27020) };

        var conflict = Assert.Single(Defaults().FindConflicts(candidate, others, HostPort));

        Assert.Equal(27020, conflict.Port);
        Assert.Contains("'Island'", conflict.Reason, StringComparison.Ordinal);
        Assert.Contains("RCON port", conflict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflicts_ReportsRconPortOnOtherOwnersGameRange()
    {
        var candidate = new PortOwner("New", 7779, 7778);
        var others = new[] { new PortOwner("Island", 7777, 27020) };

        var conflict = Assert.Single(Defaults().FindConflicts(candidate, others, HostPort));

        Assert.Equal(7778, conflict.Port);
    }

    [Fact]
    public void FindConflicts_ReportsHostPort()
    {
        var candidate = new PortOwner("New", 7777, 27020);

        var conflict = Assert.Single(Defaults().FindConflicts(candidate, [], hostPort: 7778));

        Assert.Equal(7778, conflict.Port);
        Assert.Contains("web UI", conflict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflicts_ReportsOsListeners()
    {
        var candidate = new PortOwner("New", 7777, 27020);
        var udp = new HashSet<int> { 7777, 7778 };
        var tcp = new HashSet<int> { 27020 };

        var conflicts = Defaults().FindConflicts(candidate, [], HostPort, udp, tcp);

        Assert.Collection(
            conflicts,
            c => Assert.Equal((7777, "UDP"), (c.Port, Protocol(c))),
            c => Assert.Equal((7778, "UDP"), (c.Port, Protocol(c))),
            c => Assert.Equal((27020, "TCP"), (c.Port, Protocol(c))));
    }

    [Fact]
    public void FindConflicts_OsListeners_UseTheRightProtocolTable()
    {
        // The game port is UDP and RCON is TCP: a TCP listener on the game port or a UDP listener on the
        // RCON port is not a collision.
        var candidate = new PortOwner("New", 7777, 27020);

        var conflicts = Defaults().FindConflicts(candidate, [], HostPort, new HashSet<int> { 27020 }, new HashSet<int> { 7777, 7778 });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_NullOsSets_SkipOsChecks()
    {
        var candidate = new PortOwner("New", 7777, 27020);

        Assert.Empty(Defaults().FindConflicts(candidate, [], HostPort));
    }

    [Fact]
    public void FindConflicts_CountsAnOwnerWithTheSameName_BecauseCallersExcludeSelfById()
    {
        var candidate = new PortOwner("Island", 7777, 27020);
        var namesake = new[] { new PortOwner("island", 7777, 27020), new PortOwner("Center", 7779, 27021) };
        var selfLeftOut = new[] { new PortOwner("Center", 7779, 27021) };

        Assert.Equal([7777, 7778, 27020], Defaults().FindConflicts(candidate, namesake, HostPort).Select(c => c.Port));
        Assert.Empty(Defaults().FindConflicts(candidate, selfLeftOut, HostPort));
    }

    [Fact]
    public void FindConflicts_ReportsEveryConflictInStableOrder()
    {
        // Out of range (RCON 0), own-range overlap is impossible with RCON 0, other owner on the game
        // range, host on port + 1, OS listener on the game port.
        var candidate = new PortOwner("New", 7777, 0);
        var others = new[] { new PortOwner("Island", 7778, 27020) };

        var conflicts = Defaults().FindConflicts(candidate, others, hostPort: 7778, activeUdpPorts: new HashSet<int> { 7777 });

        Assert.Collection(
            conflicts,
            c => Assert.Equal(0, c.Port),
            c => Assert.Equal((7778, "'Island'"), (c.Port, Owner(c))),
            c => Assert.Equal((7778, "web UI"), (c.Port, Owner(c))),
            c => Assert.Equal((7777, "UDP"), (c.Port, Protocol(c))));
    }

    private static string Protocol(PortConflict conflict) =>
        conflict.Reason.Contains("(UDP)", StringComparison.Ordinal) ? "UDP"
        : conflict.Reason.Contains("(TCP)", StringComparison.Ordinal) ? "TCP"
        : "none";

    private static string Owner(PortConflict conflict) =>
        conflict.Reason.Contains("'Island'", StringComparison.Ordinal) ? "'Island'"
        : conflict.Reason.Contains("web UI", StringComparison.Ordinal) ? "web UI"
        : "none";
}
