using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public class IniGeneratorTests
{
    /// <summary>A clean source: no reserved keys, so a plain generation produces no warnings.</summary>
    private const string GameUserSettings =
        "[ServerSettings]\r\n" +
        "ServerAdminPassword=hunter2\r\n" +
        "DifficultyOffset=1.0\r\n" +
        "\r\n" +
        "[SessionSettings]\r\n" +
        "; session name and port are written by the manager\r\n" +
        "\r\n" +
        "[/Script/Engine.GameSession]\r\n" +
        "ListenServerTetherDistanceMultiplier=1.0\r\n";

    private const string Game =
        "[/Script/ShooterGame.ShooterGameMode]\r\n" +
        "bDisableStructurePlacementCollision=True\r\n";

    private static GenerationInput Input(
        string game = Game,
        string gameUserSettings = GameUserSettings,
        string sessionName = "My Server",
        int gamePort = 7777,
        int rconPort = 27020,
        int maxPlayers = 20,
        IReadOnlyList<IniOverrideSpec>? overrides = null,
        string clusterWhitelist = "",
        string instanceWhitelist = "") =>
        new(game, gameUserSettings, sessionName, gamePort, rconPort, maxPlayers, overrides ?? [], clusterWhitelist, instanceWhitelist);

    [Fact]
    public void ManagerKeys_LandInTheRightSections()
    {
        var result = IniGenerator.Generate(Input());

        var gus = IniText.Parse(result.GameUserSettingsIni);
        Assert.Equal("My Server", gus.Get("SessionSettings", "SessionName"));
        Assert.Equal("7777", gus.Get("SessionSettings", "Port"));
        Assert.Equal("True", gus.Get("ServerSettings", "RCONEnabled"));
        Assert.Equal("27020", gus.Get("ServerSettings", "RCONPort"));
        Assert.Equal("20", gus.Get("/Script/Engine.GameSession", "MaxPlayers"));

        // User-owned content is untouched and nothing needed replacing.
        Assert.Equal("1.0", gus.Get("ServerSettings", "DifficultyOffset"));
        Assert.Equal(Game, result.GameIni);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            "[ServerSettings]\r\n" +
            "ServerAdminPassword=hunter2\r\n" +
            "DifficultyOffset=1.0\r\n" +
            "RCONEnabled=True\r\n" +
            "RCONPort=27020\r\n" +
            "\r\n" +
            "[SessionSettings]\r\n" +
            "; session name and port are written by the manager\r\n" +
            "SessionName=My Server\r\n" +
            "Port=7777\r\n" +
            "\r\n" +
            "[/Script/Engine.GameSession]\r\n" +
            "ListenServerTetherDistanceMultiplier=1.0\r\n" +
            "MaxPlayers=20\r\n",
            result.GameUserSettingsIni);
    }

    [Fact]
    public void ManagerKeys_CreateMissingSectionsAtEnd()
    {
        var result = IniGenerator.Generate(Input(gameUserSettings: "[ServerSettings]\r\nServerAdminPassword=x\r\n"));

        Assert.Equal(
            "[ServerSettings]\r\n" +
            "ServerAdminPassword=x\r\n" +
            "RCONEnabled=True\r\n" +
            "RCONPort=27020\r\n" +
            "\r\n" +
            "[SessionSettings]\r\n" +
            "SessionName=My Server\r\n" +
            "Port=7777\r\n" +
            "\r\n" +
            "[/Script/Engine.GameSession]\r\n" +
            "MaxPlayers=20\r\n",
            result.GameUserSettingsIni);
    }

    [Fact]
    public void ReservedKeysInSource_AreReplacedWithWarnings_AndGeneratedValuesWin()
    {
        const string source =
            "[SessionSettings]\r\n" +
            "SessionName=Old\r\n" +
            "Port=7787\r\n" +
            "\r\n" +
            "[ServerSettings]\r\n" +
            "+SessionName=x\r\n" +
            "ServerAdminPassword=pw\r\n";

        var result = IniGenerator.Generate(Input(gameUserSettings: source));

        var gus = IniText.Parse(result.GameUserSettingsIni);
        Assert.Equal("7777", gus.Get("SessionSettings", "Port"));
        Assert.Equal("My Server", gus.Get("SessionSettings", "SessionName"));
        Assert.Null(gus.Get("ServerSettings", "SessionName"));
        Assert.DoesNotContain("7787", result.GameUserSettingsIni, StringComparison.Ordinal);
        Assert.DoesNotContain("+SessionName", result.GameUserSettingsIni, StringComparison.Ordinal);

        Assert.Contains(
            "Reserved key 'Port' in [SessionSettings] of GameUserSettings.ini (line 3) was replaced by the manager value.",
            result.Warnings);
        Assert.Contains(
            "Reserved key 'SessionName' in [SessionSettings] of GameUserSettings.ini (line 2) was replaced by the manager value.",
            result.Warnings);
        Assert.Contains(
            "Reserved key '+SessionName' in [ServerSettings] of GameUserSettings.ini (line 6) was replaced by the manager value.",
            result.Warnings);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void ReservedKeysInGameIni_AreRemovedWithWarnings()
    {
        const string game = "[/Script/Engine.GameSession]\r\nMaxPlayers=99\r\nOther=1\r\n";

        var result = IniGenerator.Generate(Input(game: game));

        Assert.Equal("[/Script/Engine.GameSession]\r\nOther=1\r\n", result.GameIni);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Reserved key 'MaxPlayers' in [/Script/Engine.GameSession] of Game.ini (line 2)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Overrides_NormalApplied_ReservedSkippedWithWarning()
    {
        var overrides = new[]
        {
            new IniOverrideSpec(IniFile.Game, "/Script/ShooterGame.ShooterGameMode", "TamingSpeedMultiplier", "2"),
            new IniOverrideSpec(IniFile.GameUserSettings, "ServerSettings", "XPMultiplier", "1.5"),
            new IniOverrideSpec(IniFile.GameUserSettings, "SessionSettings", "Port", "9999"),
        };

        var result = IniGenerator.Generate(Input(overrides: overrides));

        Assert.Equal(Game + "TamingSpeedMultiplier=2\r\n", result.GameIni);
        var gus = IniText.Parse(result.GameUserSettingsIni);
        Assert.Equal("1.5", gus.Get("ServerSettings", "XPMultiplier"));
        Assert.Equal("7777", gus.Get("SessionSettings", "Port"));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Override [SessionSettings] Port for GameUserSettings.ini was skipped: the key is reserved and written by the manager.", warning);
    }

    [Fact]
    public void Overrides_MalformedSkippedWithWarning_NeverCorruptsFile()
    {
        var overrides = new[] { new IniOverrideSpec(IniFile.Game, "Bad]", "Key", "v") };

        var result = IniGenerator.Generate(Input(overrides: overrides));

        Assert.Equal(Game, result.GameIni);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Override [Bad]] Key for Game.ini was skipped:", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Overrides_ReplaceExistingKeyInPlace()
    {
        var overrides = new[] { new IniOverrideSpec(IniFile.Game, "/Script/ShooterGame.ShooterGameMode", "bDisableStructurePlacementCollision", "False") };

        var result = IniGenerator.Generate(Input(overrides: overrides));

        Assert.Equal("[/Script/ShooterGame.ShooterGameMode]\r\nbDisableStructurePlacementCollision=False\r\n", result.GameIni);
    }

    [Fact]
    public void ServerAdminPassword_IsReadFromGeneratedText()
    {
        Assert.Equal("hunter2", IniGenerator.Generate(Input()).ServerAdminPassword);
        Assert.Equal("padded", IniGenerator.Generate(Input(gameUserSettings: "[ServerSettings]\nServerAdminPassword=  padded \n")).ServerAdminPassword);
    }

    [Fact]
    public void ServerAdminPassword_IsNullWhenMissingOrEmpty()
    {
        Assert.Null(IniGenerator.Generate(Input(gameUserSettings: "[ServerSettings]\nDifficultyOffset=1\n")).ServerAdminPassword);
        Assert.Null(IniGenerator.Generate(Input(gameUserSettings: "[ServerSettings]\nServerAdminPassword=\n")).ServerAdminPassword);
        Assert.Null(IniGenerator.Generate(Input(gameUserSettings: "[ServerSettings]\nServerAdminPassword=   \n")).ServerAdminPassword);
        Assert.Null(IniGenerator.Generate(Input(gameUserSettings: string.Empty)).ServerAdminPassword);
    }

    [Fact]
    public void ServerAdminPassword_OverrideIsHonored()
    {
        var overrides = new[] { new IniOverrideSpec(IniFile.GameUserSettings, "ServerSettings", "ServerAdminPassword", "fromOverride") };

        Assert.Equal("fromOverride", IniGenerator.Generate(Input(overrides: overrides)).ServerAdminPassword);
    }

    [Fact]
    public void Whitelist_UnionDedupesSkipsCommentsAndPreservesOrder()
    {
        var result = IniGenerator.Generate(Input(
            clusterWhitelist: "; cluster admins\r\n000111\r\n\r\n  000222  \r\n# hash comment\n000111\n",
            instanceWhitelist: "000222\n000333\n\n000444"));

        Assert.Equal(["000111", "000222", "000333", "000444"], result.AdminWhitelist);
    }

    [Fact]
    public void Whitelist_EmptyInputs_ProduceEmptyList()
    {
        Assert.Empty(IniGenerator.Generate(Input()).AdminWhitelist);
    }

    [Theory]
    [InlineData("Bad?Name", "'?'")]
    [InlineData("Bad=Name", "'='")]
    [InlineData("Bad\nName", "line breaks")]
    [InlineData("", "empty")]
    public void TypedValues_InvalidSessionName_Throws(string sessionName, string expectedFragment)
    {
        var ex = Assert.Throws<ArgumentException>(() => IniGenerator.Generate(Input(sessionName: sessionName)));

        Assert.Contains("SessionName", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 27020, 20)]
    [InlineData(65536, 27020, 20)]
    [InlineData(7777, 0, 20)]
    [InlineData(7777, 27020, 0)]
    [InlineData(7777, 27020, 501)]
    public void TypedValues_OutOfRange_Throws(int gamePort, int rconPort, int maxPlayers)
    {
        Assert.Throws<ArgumentException>(() => IniGenerator.Generate(Input(gamePort: gamePort, rconPort: rconPort, maxPlayers: maxPlayers)));
    }

    [Fact]
    public void TypedValues_AllProblemsReportedTogether()
    {
        var ex = Assert.Throws<ArgumentException>(() => IniGenerator.Generate(Input(sessionName: "a?b", gamePort: 0, rconPort: 70000, maxPlayers: 0)));

        Assert.Contains("SessionName", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GamePort", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RconPort", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MaxPlayers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_IsIdempotent_OnItsOwnOutput()
    {
        var first = IniGenerator.Generate(Input());
        var second = IniGenerator.Generate(Input(game: first.GameIni, gameUserSettings: first.GameUserSettingsIni));

        Assert.Equal(first.GameIni, second.GameIni);
        Assert.Equal(first.GameUserSettingsIni, second.GameUserSettingsIni);
        Assert.Equal(5, second.Warnings.Count);
    }
}
