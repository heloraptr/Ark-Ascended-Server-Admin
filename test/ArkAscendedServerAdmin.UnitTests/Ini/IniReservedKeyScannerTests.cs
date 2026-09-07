using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.UnitTests.Ini;

public class IniReservedKeyScannerTests
{
    [Fact]
    public void Find_ReportsEveryReservedKeyWithLineAndSection()
    {
        const string text =
            "AltSaveDirectoryName=Foo\n" +
            "[SessionSettings]\n" +
            "SessionName=My Server\n" +
            "Port=7777\n" +
            "; Port=1 (comment, ignored)\n" +
            "[ServerSettings]\n" +
            "ServerAdminPassword=secret\n" +
            "+sessionname=dup\n" +
            "RCONEnabled=True\n" +
            "rconport=27020\n" +
            "[/Script/Engine.GameSession]\n" +
            "MaxPlayers=70\n";

        var hits = IniReservedKeyScanner.Find(text);

        Assert.Equal(
            [
                new IniReservedKeyHit(1, string.Empty, "AltSaveDirectoryName"),
                new IniReservedKeyHit(3, "SessionSettings", "SessionName"),
                new IniReservedKeyHit(4, "SessionSettings", "Port"),
                new IniReservedKeyHit(8, "ServerSettings", "+sessionname"),
                new IniReservedKeyHit(9, "ServerSettings", "RCONEnabled"),
                new IniReservedKeyHit(10, "ServerSettings", "rconport"),
                new IniReservedKeyHit(12, "/Script/Engine.GameSession", "MaxPlayers"),
            ],
            hits);
    }

    [Fact]
    public void Find_CleanText_ReturnsNothing()
    {
        const string text = "[ServerSettings]\nServerAdminPassword=secret\nTamingSpeedMultiplier=2\n";

        Assert.Empty(IniReservedKeyScanner.Find(text));
    }

    [Fact]
    public void Find_HandlesBomAndCrLf()
    {
        var hits = IniReservedKeyScanner.Find(IniTextTests.Bom + "[SessionSettings]\r\nPort=1\r\n");

        Assert.Equal([new IniReservedKeyHit(2, "SessionSettings", "Port")], hits);
    }
}
