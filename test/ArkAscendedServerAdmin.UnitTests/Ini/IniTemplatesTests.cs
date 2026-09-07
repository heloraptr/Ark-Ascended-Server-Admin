using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public class IniTemplatesTests
{
    [Fact]
    public void GameUserSettings_ParsesCleanly_WithAnEmptyAdminPassword()
    {
        var text = IniText.Parse(IniTemplates.DefaultGameUserSettings);

        Assert.Equal(["ServerSettings", "SessionSettings", "/Script/Engine.GameSession"], text.Sections);
        Assert.Equal(string.Empty, text.Get("ServerSettings", "ServerAdminPassword"));
        Assert.Equal(IniTemplates.DefaultGameUserSettings, text.ToString());
    }

    [Fact]
    public void GameIni_ParsesCleanly()
    {
        var text = IniText.Parse(IniTemplates.DefaultGameIni);

        Assert.Equal(["/Script/ShooterGame.ShooterGameMode"], text.Sections);
        Assert.Equal(IniTemplates.DefaultGameIni, text.ToString());
    }

    [Fact]
    public void Templates_ContainNoReservedKeys()
    {
        Assert.Empty(IniReservedKeyScanner.Find(IniTemplates.DefaultGameUserSettings));
        Assert.Empty(IniReservedKeyScanner.Find(IniTemplates.DefaultGameIni));
    }

    [Fact]
    public void Templates_GenerateWithoutWarnings()
    {
        var result = IniGenerator.Generate(new GenerationInput(
            IniTemplates.DefaultGameIni, IniTemplates.DefaultGameUserSettings, "Fresh", 7777, 27020, 20, [], string.Empty, string.Empty));

        Assert.Empty(result.Warnings);
        Assert.Null(result.ServerAdminPassword);
        Assert.Equal("Fresh", IniText.Parse(result.GameUserSettingsIni).Get("SessionSettings", "SessionName"));
    }
}
