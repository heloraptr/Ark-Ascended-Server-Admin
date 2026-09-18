using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.UnitTests.Launch;

public class LaunchArgumentBuilderTests
{
    private const string ClusterDirectory = @"D:\ArkData\Clusters\main";

    [Fact]
    public void Standalone_DefaultFlags_ProducesExactOrderedList()
    {
        var arguments = LaunchArgumentBuilder.Build(Standalone()).Arguments;

        Assert.Equal(
            [
                "TheIsland_WP?listen?AltSaveDirectoryName=alpha",
                "-port=7777",
                "-WinLiveMaxPlayers=10",
                "-log",
                "-servergamelog",
                "-NoBattlEye",
            ],
            arguments);
    }

    [Fact]
    public void MapMod_ComesFirst_AndIsNotRepeated()
    {
        var request = Clustered() with { MapModId = 9, ClusterModIds = [3, 9], InstanceModIds = [4, 3] };

        var arguments = LaunchArgumentBuilder.Build(request).Arguments;

        Assert.Contains("-mods=9,3,4", arguments);
        Assert.Contains("Mod id 0 is not a valid CurseForge project id.", Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request with { MapModId = 0 })).Problems);
    }

    [Fact]
    public void Standalone_DoesNotEmitStdout()
    {
        var arguments = LaunchArgumentBuilder.Build(Standalone()).Arguments;

        Assert.DoesNotContain("-stdout", arguments);
        Assert.DoesNotContain("-FullStdOutLogOutput", arguments);
    }

    [Fact]
    public void Clustered_AddsClusterIdAndDirOverrideAfterMaxPlayers()
    {
        var arguments = LaunchArgumentBuilder.Build(Clustered()).Arguments;

        Assert.Equal(
            [
                "TheIsland_WP?listen?AltSaveDirectoryName=alpha",
                "-port=7777",
                "-WinLiveMaxPlayers=10",
                "-clusterid=main",
                $"-ClusterDirOverride={ClusterDirectory}",
                "-log",
                "-servergamelog",
                "-NoBattlEye",
            ],
            arguments);
    }

    [Fact]
    public void Mods_ClusterThenInstance_DedupedKeepingFirst_AfterClusterOptions()
    {
        var request = Clustered() with { ClusterModIds = [3, 1, 2], InstanceModIds = [2, 4, 1, 5] };

        var arguments = LaunchArgumentBuilder.Build(request).Arguments;

        Assert.Equal("-mods=3,1,2,4,5", arguments[5]);
        Assert.Equal("-log", arguments[6]);
    }

    [Fact]
    public void Mods_Omitted_WhenNone()
    {
        var arguments = LaunchArgumentBuilder.Build(Standalone()).Arguments;

        Assert.DoesNotContain(arguments, a => a.StartsWith("-mods", StringComparison.Ordinal));
    }

    [Fact]
    public void Mods_InstanceOnly_AreEmitted()
    {
        var request = Standalone() with { InstanceModIds = [927090] };

        var arguments = LaunchArgumentBuilder.Build(request).Arguments;

        Assert.Equal("-mods=927090", arguments[3]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NoBattlEye_DefaultsOn_OnlyFalseEnablesBattlEye(bool? flag, bool emitted)
    {
        var request = Standalone(new LaunchFlags { NoBattlEye = flag });

        var arguments = LaunchArgumentBuilder.Build(request).Arguments;

        Assert.Equal(emitted, arguments.Contains("-NoBattlEye"));
    }

    [Fact]
    public void LogAndServerGameLog_AreAlwaysEmitted()
    {
        var arguments = LaunchArgumentBuilder.Build(Standalone()).Arguments;

        Assert.Equal(["-log", "-servergamelog"], arguments.SkipWhile(a => a != "-log").Take(2));
    }

    public static TheoryData<string, Func<bool?, LaunchFlags>> BoolFlags => new()
    {
        { "-ServerGameLogIncludeTribeLogs", v => new LaunchFlags { ServerGameLogIncludeTribeLogs = v } },
        { "-ServerRconOutputTribeLogs", v => new LaunchFlags { ServerRconOutputTribeLogs = v } },
        { "-exclusivejoin", v => new LaunchFlags { ExclusiveJoin = v } },
        { "-NoWildBabies", v => new LaunchFlags { NoWildBabies = v } },
        { "-UseStore", v => new LaunchFlags { UseStore = v } },
        { "-ConvertToStore", v => new LaunchFlags { ConvertToStore = v } },
    };

    [Theory]
    [MemberData(nameof(BoolFlags))]
    public void BoolFlag_EmittedOnlyWhenTrue(string option, Func<bool?, LaunchFlags> flags)
    {
        Assert.Contains(option, LaunchArgumentBuilder.Build(Standalone(flags(true))).Arguments);
        Assert.DoesNotContain(option, LaunchArgumentBuilder.Build(Standalone(flags(false))).Arguments);
        Assert.DoesNotContain(option, LaunchArgumentBuilder.Build(Standalone(flags(null))).Arguments);
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("PC+XSX")]
    public void ServerPlatform_EmittedWhenSet(string platform)
    {
        var arguments = LaunchArgumentBuilder.Build(Standalone(new LaunchFlags { ServerPlatform = platform })).Arguments;

        Assert.Contains($"-ServerPlatform={platform}", arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ServerPlatformAndActiveEvent_OmittedWhenBlank(string? value)
    {
        var flags = new LaunchFlags { ServerPlatform = value, ActiveEvent = value };

        var arguments = LaunchArgumentBuilder.Build(Standalone(flags)).Arguments;

        Assert.DoesNotContain(arguments, a => a.StartsWith("-ServerPlatform", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.StartsWith("-ActiveEvent", StringComparison.Ordinal));
    }

    [Fact]
    public void AllTypedFlags_EmitInDocumentedOrder_BeforeAdditionalArgs()
    {
        var flags = new LaunchFlags
        {
            NoBattlEye = true,
            ServerPlatform = "PC",
            ExclusiveJoin = true,
            NoWildBabies = true,
            UseStore = true,
            ConvertToStore = true,
            ServerGameLogIncludeTribeLogs = true,
            ServerRconOutputTribeLogs = true,
            ActiveEvent = "Summer",
            AdditionalArgs = "-culture=en -foo=\"a b\"",
        };

        var arguments = LaunchArgumentBuilder.Build(Clustered(flags) with { ClusterModIds = [1] }).Arguments;

        Assert.Equal(
            [
                "TheIsland_WP?listen?AltSaveDirectoryName=alpha",
                "-port=7777",
                "-WinLiveMaxPlayers=10",
                "-clusterid=main",
                $"-ClusterDirOverride={ClusterDirectory}",
                "-mods=1",
                "-log",
                "-servergamelog",
                "-ServerGameLogIncludeTribeLogs",
                "-ServerRconOutputTribeLogs",
                "-NoBattlEye",
                "-exclusivejoin",
                "-NoWildBabies",
                "-UseStore",
                "-ConvertToStore",
                "-ServerPlatform=PC",
                "-ActiveEvent=Summer",
                "-culture=en",
                "-foo=a b",
            ],
            arguments);
    }

    [Fact]
    public void AdditionalArgs_AppendedVerbatimAfterTypedFlags()
    {
        var flags = new LaunchFlags { ActiveEvent = "Summer", AdditionalArgs = "-crossplay -foo=\"a b\"" };

        var arguments = LaunchArgumentBuilder.Build(Standalone(flags)).Arguments;

        Assert.Equal(["-ActiveEvent=Summer", "-crossplay", "-foo=a b"], arguments.TakeLast(3));
    }

    [Theory]
    [InlineData("-port=1", "'-port=1'")]
    [InlineData("-mods=x", "'-mods=x'")]
    [InlineData("-NoBattlEye", "'-NoBattlEye'")]
    [InlineData("-ok a?b", "'a?b'")]
    [InlineData("-foo=\"a b", "unbalanced")]
    public void AdditionalArgs_RejectionCases_ThrowNamingTheToken(string text, string expectedFragment)
    {
        var request = Standalone(new LaunchFlags { AdditionalArgs = text });

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.Contains(expectedFragment, problem);
    }

    [Theory]
    [InlineData("ALL?SessionName=x")]
    [InlineData("A=B")]
    [InlineData("PC XSX")]
    public void ServerPlatform_UnsafeValue_Throws(string platform)
    {
        var request = Standalone(new LaunchFlags { ServerPlatform = platform });

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Server platform", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveEvent_WithDelimiter_Throws()
    {
        var request = Standalone(new LaunchFlags { ActiveEvent = "Summer?listen" });

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Active event", problem, StringComparison.Ordinal);
        Assert.Contains("'?'", problem);
    }

    [Theory]
    [InlineData("The Island_WP", "whitespace")]
    [InlineData("TheIsland_WP?listen", "'?'")]
    [InlineData("TheIsland=WP", "'='")]
    [InlineData("", "empty")]
    public void MapKey_UnsafeValue_Throws(string mapKey, string expectedFragment)
    {
        var request = Standalone() with { MapKey = mapKey };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Map key", problem, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, problem);
    }

    [Theory]
    [InlineData("al pha")]
    [InlineData("alpha?x")]
    [InlineData("alpha=x")]
    [InlineData(" ")]
    public void Slug_UnsafeValue_Throws(string slug)
    {
        var request = Standalone() with { Slug = slug };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Slug", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterKey_WithDelimiter_Throws()
    {
        var request = Clustered() with { ClusterKey = "main?x" };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Cluster key", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterKeyWithoutDirectory_Throws()
    {
        var request = Standalone() with { ClusterKey = "main" };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.Contains("both", problem);
    }

    [Fact]
    public void ClusterDirectoryWithoutKey_Throws()
    {
        var request = Standalone() with { ClusterDirectory = ClusterDirectory };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.Contains("both", problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65535)]
    public void GamePort_OutOfRange_Throws(int port)
    {
        var request = Standalone() with { GamePort = port };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Game port", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void MaxPlayers_OutOfRange_Throws(int maxPlayers)
    {
        var request = Standalone() with { MaxPlayers = maxPlayers };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("Max players", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveModId_Throws()
    {
        var request = Standalone() with { InstanceModIds = [0] };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        var problem = Assert.Single(ex.Problems);
        Assert.Contains("Mod id 0", problem);
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var request = Standalone(new LaunchFlags { AdditionalArgs = "-port=1" }) with { MapKey = "a b", GamePort = 0 };

        var ex = Assert.Throws<LaunchValidationException>(() => LaunchArgumentBuilder.Build(request));

        Assert.Equal(3, ex.Problems.Count);
        Assert.Contains("Map key", ex.Message);
        Assert.Contains("'-port=1'", ex.Message);
    }

    [Fact]
    public void NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchArgumentBuilder.Build(null!));
    }

    [Fact]
    public void DisplayString_QuotesArgumentsWithSpaces()
    {
        var launch = LaunchArgumentBuilder.Build(Clustered() with { ClusterDirectory = @"D:\Ark Data\Clusters\main" });

        var display = launch.ToDisplayString();

        Assert.Contains("\"-ClusterDirOverride=D:\\Ark Data\\Clusters\\main\"", display);
        Assert.StartsWith("TheIsland_WP?listen?AltSaveDirectoryName=alpha -port=7777 -WinLiveMaxPlayers=10 -clusterid=main ", display, StringComparison.Ordinal);
        Assert.EndsWith(" -log -servergamelog -NoBattlEye", display, StringComparison.Ordinal);
    }

    private static LaunchRequest Standalone(LaunchFlags? flags = null) =>
        new("TheIsland_WP", "alpha", 7777, 10, null, null, [], [], flags ?? new LaunchFlags());

    private static LaunchRequest Clustered(LaunchFlags? flags = null) =>
        Standalone(flags) with { ClusterKey = "main", ClusterDirectory = ClusterDirectory };
}
