using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.UnitTests.Launch;

public class LaunchFlagResolverTests
{
    [Fact]
    public void NullCluster_ReturnsCopyOfInstanceFlags()
    {
        var instance = new LaunchFlags { NoBattlEye = false, ServerPlatform = "PC", AdditionalArgs = "-culture=en" };

        var resolved = LaunchFlagResolver.Resolve(null, instance);

        Assert.NotSame(instance, resolved);
        Assert.False(resolved.NoBattlEye);
        Assert.Equal("PC", resolved.ServerPlatform);
        Assert.Equal("-culture=en", resolved.AdditionalArgs);
        Assert.Null(resolved.ExclusiveJoin);
        Assert.Null(resolved.ActiveEvent);
    }

    [Fact]
    public void InstanceNull_InheritsEveryClusterProperty()
    {
        var cluster = AllSet(true, "ALL", "Summer");

        var resolved = LaunchFlagResolver.Resolve(cluster, new LaunchFlags());

        Assert.True(resolved.NoBattlEye);
        Assert.Equal("ALL", resolved.ServerPlatform);
        Assert.True(resolved.ExclusiveJoin);
        Assert.True(resolved.NoWildBabies);
        Assert.True(resolved.PreventSpawnAnimations);
        Assert.True(resolved.UseStore);
        Assert.True(resolved.ConvertToStore);
        Assert.True(resolved.ServerGameLog);
        Assert.True(resolved.ServerGameLogIncludeTribeLogs);
        Assert.True(resolved.ServerRconOutputTribeLogs);
        Assert.Equal("Summer", resolved.ActiveEvent);
    }

    [Fact]
    public void InstanceNonNull_WinsPerProperty()
    {
        var cluster = AllSet(true, "ALL", "Summer");
        var instance = AllSet(false, "PC+XSX", "Winter");

        var resolved = LaunchFlagResolver.Resolve(cluster, instance);

        Assert.False(resolved.NoBattlEye);
        Assert.Equal("PC+XSX", resolved.ServerPlatform);
        Assert.False(resolved.ExclusiveJoin);
        Assert.False(resolved.NoWildBabies);
        Assert.False(resolved.PreventSpawnAnimations);
        Assert.False(resolved.UseStore);
        Assert.False(resolved.ConvertToStore);
        Assert.False(resolved.ServerGameLog);
        Assert.False(resolved.ServerGameLogIncludeTribeLogs);
        Assert.False(resolved.ServerRconOutputTribeLogs);
        Assert.Equal("Winter", resolved.ActiveEvent);
    }

    [Fact]
    public void MixedOverrides_ResolveIndependently()
    {
        var cluster = new LaunchFlags { NoBattlEye = true, ExclusiveJoin = true, ServerPlatform = "ALL" };
        var instance = new LaunchFlags { ExclusiveJoin = false, UseStore = true };

        var resolved = LaunchFlagResolver.Resolve(cluster, instance);

        Assert.True(resolved.NoBattlEye);
        Assert.False(resolved.ExclusiveJoin);
        Assert.True(resolved.UseStore);
        Assert.Equal("ALL", resolved.ServerPlatform);
        Assert.Null(resolved.NoWildBabies);
    }

    [Theory]
    [InlineData("-a", "-b", "-a -b")]
    [InlineData("  -a  ", "  -b  ", "-a -b")]
    [InlineData("-a", null, "-a")]
    [InlineData("-a", "   ", "-a")]
    [InlineData(null, "-b", "-b")]
    [InlineData("", "-b", "-b")]
    [InlineData(null, null, null)]
    [InlineData("", "  ", null)]
    public void AdditionalArgs_AreClusterThenInstance(string? clusterText, string? instanceText, string? expected)
    {
        var resolved = LaunchFlagResolver.Resolve(
            new LaunchFlags { AdditionalArgs = clusterText },
            new LaunchFlags { AdditionalArgs = instanceText });

        Assert.Equal(expected, resolved.AdditionalArgs);
    }

    [Fact]
    public void Resolve_DoesNotMutateInputs()
    {
        var cluster = new LaunchFlags { NoBattlEye = true, AdditionalArgs = "-a" };
        var instance = new LaunchFlags { NoBattlEye = false, AdditionalArgs = "-b" };

        LaunchFlagResolver.Resolve(cluster, instance);

        Assert.True(cluster.NoBattlEye);
        Assert.Equal("-a", cluster.AdditionalArgs);
        Assert.False(instance.NoBattlEye);
        Assert.Equal("-b", instance.AdditionalArgs);
    }

    [Fact]
    public void NullInstance_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchFlagResolver.Resolve(new LaunchFlags(), null!));
    }

    private static LaunchFlags AllSet(bool value, string platform, string activeEvent) => new()
    {
        NoBattlEye = value,
        ServerPlatform = platform,
        ExclusiveJoin = value,
        NoWildBabies = value,
        PreventSpawnAnimations = value,
        UseStore = value,
        ConvertToStore = value,
        ServerGameLog = value,
        ServerGameLogIncludeTribeLogs = value,
        ServerRconOutputTribeLogs = value,
        ActiveEvent = activeEvent,
    };
}
