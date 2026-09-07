using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.UnitTests.Launch;

public class LaunchCommandLineTests
{
    private const string QuotedExe =
        "\"D:\\ArkData\\Instances\\alpha\\ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe\" TheIsland_WP?listen?AltSaveDirectoryName=alpha -log -servergamelog -port=7787 -WinLiveMaxPlayers=4 -NoBattlEye";

    private const string UnquotedExe =
        "D:\\ArkData\\Instances\\alpha\\ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe TheIsland_WP?listen?AltSaveDirectoryName=alpha -log -port=7787";

    private const string UnquotedExeWithSpaces =
        "D:\\Ark Data\\Instances\\alpha\\ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe TheIsland_WP?listen?AltSaveDirectoryName=alpha -log";

    [Theory]
    [InlineData(QuotedExe)]
    [InlineData(UnquotedExe)]
    [InlineData(UnquotedExeWithSpaces)]
    public void ParseMapTokens_ReturnsTokensAfterMapName(string commandLine)
    {
        var tokens = LaunchCommandLine.ParseMapTokens(commandLine);

        Assert.Equal(["listen", "AltSaveDirectoryName=alpha"], tokens);
    }

    [Fact]
    public void ParseMapTokens_QuotedMapString_IsOneArgument()
    {
        var tokens = LaunchCommandLine.ParseMapTokens("\"C:\\x\\ArkAscendedServer.exe\" \"TheIsland_WP?listen?AltSaveDirectoryName=alpha\" -log");

        Assert.Equal(["listen", "AltSaveDirectoryName=alpha"], tokens);
    }

    [Theory]
    [InlineData("\"C:\\x\\ArkAscendedServer.exe\"")]
    [InlineData("C:\\x\\ArkAscendedServer.exe")]
    [InlineData("C:\\x\\ArkAscendedServer.exe TheIsland_WP")]
    [InlineData("C:\\x\\ArkAscendedServer.exe -log -port=7777")]
    [InlineData("")]
    public void ParseMapTokens_NoMapParameters_ReturnsEmpty(string commandLine)
    {
        Assert.Empty(LaunchCommandLine.ParseMapTokens(commandLine));
    }

    [Theory]
    [InlineData(QuotedExe)]
    [InlineData(UnquotedExe)]
    [InlineData(UnquotedExeWithSpaces)]
    public void TryGetAltSaveDirectoryName_ReturnsExactValue(string commandLine)
    {
        Assert.Equal("alpha", LaunchCommandLine.TryGetAltSaveDirectoryName(commandLine));
    }

    [Fact]
    public void TryGetAltSaveDirectoryName_KeyIsCaseInsensitive_ValueIsVerbatim()
    {
        var value = LaunchCommandLine.TryGetAltSaveDirectoryName("x.exe TheIsland_WP?altsavedirectoryname=Alpha");

        Assert.Equal("Alpha", value);
    }

    [Theory]
    [InlineData("x.exe TheIsland_WP?listen -log")]
    [InlineData("x.exe TheIsland_WP?listen?AltSaveDirectoryName -log")]
    [InlineData("x.exe TheIsland_WP?listen?AltSaveDirectoryName= -log")]
    [InlineData("x.exe TheIsland_WP?listen?AltSaveDirectoryNameX=alpha -log")]
    [InlineData("x.exe -log AltSaveDirectoryName=alpha")]
    public void TryGetAltSaveDirectoryName_MissingToken_ReturnsNull(string commandLine)
    {
        Assert.Null(LaunchCommandLine.TryGetAltSaveDirectoryName(commandLine));
    }

    [Fact]
    public void MatchesSlug_ExactMatchOnly()
    {
        const string alpha2 = "\"C:\\x\\ArkAscendedServer.exe\" TheIsland_WP?listen?AltSaveDirectoryName=alpha2 -log";

        Assert.True(LaunchCommandLine.MatchesSlug(QuotedExe, "alpha"));
        Assert.False(LaunchCommandLine.MatchesSlug(alpha2, "alpha"));
        Assert.True(LaunchCommandLine.MatchesSlug(alpha2, "alpha2"));
        Assert.False(LaunchCommandLine.MatchesSlug(QuotedExe, "alph"));
        Assert.False(LaunchCommandLine.MatchesSlug(QuotedExe, "Alpha"));
    }

    [Fact]
    public void MatchesSlug_UsesOnlyTheMapString()
    {
        const string decoy = "\"C:\\x\\ArkAscendedServer.exe\" TheIsland_WP?listen -AltSaveDirectoryName=alpha -log";

        Assert.False(LaunchCommandLine.MatchesSlug(decoy, "alpha"));
    }

    [Fact]
    public void MatchesSlug_BlankSlug_Throws()
    {
        Assert.Throws<ArgumentException>(() => LaunchCommandLine.MatchesSlug(QuotedExe, " "));
    }

    [Fact]
    public void ParseMapTokens_NullCommandLine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchCommandLine.ParseMapTokens(null!));
    }

    [Fact]
    public void BuilderOutput_RoundTripsThroughMatcher()
    {
        var launch = LaunchArgumentBuilder.Build(new LaunchRequest("TheIsland_WP", "alpha", 7787, 4, null, null, [], [], new Domain.LaunchFlags()));
        var commandLine = "\"D:\\ArkData\\Instances\\alpha\\ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe\" " + launch.ToDisplayString();

        Assert.True(LaunchCommandLine.MatchesSlug(commandLine, "alpha"));
        Assert.False(LaunchCommandLine.MatchesSlug(commandLine, "alpha2"));
    }
}
