using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public class IniTextTests
{
    /// <summary>U+FEFF as a string, spelled out so it is visible in source.</summary>
    internal const string Bom = "\uFEFF";

    private const string GameIni =
        "; ARK: Survival Ascended Game.ini\r\n" +
        "\r\n" +
        "[/Script/ShooterGame.ShooterGameMode]\r\n" +
        "bDisableStructurePlacementCollision=True\r\n" +
        "ConfigOverrideItemMaxQuantity=(ItemClassString=\"PrimalItemResource_Wood_C\",Quantity=(MaxItemQuantity=1000, bIgnoreMultiplier=true))\r\n" +
        "ConfigOverrideItemMaxQuantity=(ItemClassString=\"PrimalItemResource_Stone_C\",Quantity=(MaxItemQuantity=1000, bIgnoreMultiplier=true))\r\n" +
        "+OverrideEngramEntries=(EngramIndex=1,EngramHidden=false)\r\n" +
        "+OverrideEngramEntries=(EngramIndex=2,EngramHidden=false)\r\n" +
        "  # indented hash comment\r\n" +
        "PerLevelStatsMultiplier_Player[7]=1.5\r\n" +
        "this line is junk\r\n" +
        "\r\n" +
        "[/Script/Engine.GameSession]\r\n" +
        "MaxPlayers=40\r\n";

    [Fact]
    public void RoundTrip_ReproducesTextExactly()
    {
        var ini = IniText.Parse(GameIni);

        Assert.Equal(GameIni, ini.ToString());
    }

    [Fact]
    public void Parse_StripsBom_AndNormalizesLineEndings()
    {
        var input = Bom + GameIni.Replace("\r\n", "\n", StringComparison.Ordinal);

        var ini = IniText.Parse(input);

        Assert.Equal(GameIni, ini.ToString());
    }

    [Fact]
    public void Parse_TextWithoutTrailingNewline_RendersWithOne()
    {
        var ini = IniText.Parse("[A]\nKey=1");

        Assert.Equal("[A]\r\nKey=1\r\n", ini.ToString());
    }

    [Fact]
    public void Parse_EmptyText_RendersEmpty()
    {
        Assert.Equal(string.Empty, IniText.Parse(string.Empty).ToString());
        Assert.Empty(IniText.Parse(string.Empty).Sections);
    }

    [Fact]
    public void Sections_ListsDistinctNamesInOrder()
    {
        var ini = IniText.Parse("[B]\nx=1\n[A]\ny=2\n[b]\nz=3\n");

        Assert.Equal(["B", "A"], ini.Sections);
    }

    [Fact]
    public void Get_ReturnsFirstMatch_CaseInsensitive_PrefixStripped()
    {
        var ini = IniText.Parse(GameIni);

        Assert.Equal("(EngramIndex=1,EngramHidden=false)", ini.Get("/script/shootergame.shootergamemode", "overrideengramentries"));
        Assert.Equal("(EngramIndex=1,EngramHidden=false)", ini.Get("/Script/ShooterGame.ShooterGameMode", "+OverrideEngramEntries"));
        Assert.Equal("40", ini.Get("/Script/Engine.GameSession", "maxplayers"));
        Assert.Null(ini.Get("/Script/Engine.GameSession", "Missing"));
        Assert.Null(ini.Get("Missing", "MaxPlayers"));
    }

    [Fact]
    public void Get_ValueIsTrimmed_AndKeepsEmbeddedEquals()
    {
        var ini = IniText.Parse("[S]\nKey = a=b=c  \n");

        Assert.Equal("a=b=c", ini.Get("S", "Key"));
    }

    [Fact]
    public void Set_ReplacesFirstInPlace_AndRemovesDuplicatesInSection()
    {
        var ini = IniText.Parse(GameIni);

        ini.Set("/Script/ShooterGame.ShooterGameMode", "ConfigOverrideItemMaxQuantity", "(ItemClassString=\"X\")");

        var expected = GameIni
            .Replace(
                "ConfigOverrideItemMaxQuantity=(ItemClassString=\"PrimalItemResource_Wood_C\",Quantity=(MaxItemQuantity=1000, bIgnoreMultiplier=true))\r\n",
                "ConfigOverrideItemMaxQuantity=(ItemClassString=\"X\")\r\n",
                StringComparison.Ordinal)
            .Replace(
                "ConfigOverrideItemMaxQuantity=(ItemClassString=\"PrimalItemResource_Stone_C\",Quantity=(MaxItemQuantity=1000, bIgnoreMultiplier=true))\r\n",
                string.Empty,
                StringComparison.Ordinal);
        Assert.Equal(expected, ini.ToString());
    }

    [Fact]
    public void Set_IsCaseInsensitive_AndMatchesPrefixedKeys()
    {
        var ini = IniText.Parse("[ServerSettings]\n+rconport=1\n");

        ini.Set("serversettings", "RCONPort", "27020");

        Assert.Equal("[ServerSettings]\r\nRCONPort=27020\r\n", ini.ToString());
        Assert.Equal("27020", ini.Get("ServerSettings", "rconport"));
    }

    [Fact]
    public void Set_AppendsAfterLastNonBlankLine_KeepingTrailingBlank()
    {
        var ini = IniText.Parse("[A]\nx=1\n\n[B]\ny=2\n");

        ini.Set("A", "New", "v");

        Assert.Equal("[A]\r\nx=1\r\nNew=v\r\n\r\n[B]\r\ny=2\r\n", ini.ToString());
    }

    [Fact]
    public void Set_AppendsToEmptySection()
    {
        var ini = IniText.Parse("[A]\n[B]\ny=2\n");

        ini.Set("A", "New", "v");

        Assert.Equal("[A]\r\nNew=v\r\n[B]\r\ny=2\r\n", ini.ToString());
    }

    [Fact]
    public void Set_CreatesMissingSectionAtEnd_PrecededByBlankLine()
    {
        var ini = IniText.Parse("[A]\nx=1\n");

        ini.Set("SessionSettings", "SessionName", "My Server");

        Assert.Equal("[A]\r\nx=1\r\n\r\n[SessionSettings]\r\nSessionName=My Server\r\n", ini.ToString());
        Assert.Equal(["A", "SessionSettings"], ini.Sections);
    }

    [Fact]
    public void Set_CreatesSectionInEmptyDocument_WithoutLeadingBlank()
    {
        var ini = IniText.Parse(string.Empty);

        ini.Set("A", "x", "1");

        Assert.Equal("[A]\r\nx=1\r\n", ini.ToString());
    }

    [Fact]
    public void Set_DoesNotAddSecondBlankWhenFileAlreadyEndsBlank()
    {
        var ini = IniText.Parse("[A]\nx=1\n\n");

        ini.Set("B", "y", "2");

        Assert.Equal("[A]\r\nx=1\r\n\r\n[B]\r\ny=2\r\n", ini.ToString());
    }

    [Fact]
    public void Set_RejectsMalformedSectionOrKey()
    {
        var ini = IniText.Parse(string.Empty);

        Assert.Throws<ArgumentException>(() => ini.Set("A]", "x", "1"));
        Assert.Throws<ArgumentException>(() => ini.Set("A", "x=y", "1"));
        Assert.Throws<ArgumentException>(() => ini.Set("A", "x", "1\n2"));
    }

    [Fact]
    public void RemoveKey_RemovesEveryOccurrenceAcrossSections_ReportingParsedLineNumbers()
    {
        var ini = IniText.Parse("Port=1\n[SessionSettings]\nPort=7787\nOther=1\n[ServerSettings]\n+port=9\n");

        var removed = ini.RemoveKey("port");

        Assert.Equal(
            [
                new IniRemoval(string.Empty, "Port", "1", 1),
                new IniRemoval("SessionSettings", "Port", "7787", 3),
                new IniRemoval("ServerSettings", "+port", "9", 6),
            ],
            removed);
        Assert.Equal("[SessionSettings]\r\nOther=1\r\n[ServerSettings]\r\n", ini.ToString());
    }

    [Fact]
    public void RemoveKey_MissingKey_ReturnsEmptyAndLeavesTextUntouched()
    {
        var ini = IniText.Parse(GameIni);

        Assert.Empty(ini.RemoveKey("Port"));
        Assert.Equal(GameIni, ini.ToString());
    }

    [Fact]
    public void CommentsAndJunk_AreNeverTreatedAsKeys()
    {
        var ini = IniText.Parse("[A]\n; Port=1\n# Port=2\n=3\nPort=4\n");

        Assert.Equal("4", ini.Get("A", "Port"));
        Assert.Single(ini.RemoveKey("Port"));
        Assert.Equal("[A]\r\n; Port=1\r\n# Port=2\r\n=3\r\n", ini.ToString());
    }
}
