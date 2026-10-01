using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.UnitTests.Processes;

/// <summary>Expected strings follow the <c>CommandLineToArgvW</c> rules (and what <c>ProcessStartInfo.ArgumentList</c> produces).</summary>
public class WindowsCommandLineTests
{
    [Fact]
    public void QuotesTheProgram_AndLeavesPlainArgumentsAlone()
    {
        var line = WindowsCommandLine.Build(@"C:\Ark Data\SteamCMD\steamcmd.exe", ["+login", "anonymous", "+app_update", "2430930", "+quit"]);

        Assert.Equal("\"C:\\Ark Data\\SteamCMD\\steamcmd.exe\" +login anonymous +app_update 2430930 +quit", line);
    }

    [Theory]
    [InlineData(@"C:\Ark Data\Server", "\"C:\\Ark Data\\Server\"")]
    [InlineData(@"C:\Ark\Server", @"C:\Ark\Server")]
    [InlineData("", "\"\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Ark Data\", "\"C:\\Ark Data\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    public void QuotesAndEscapesArgumentsThatNeedIt(string argument, string expected)
    {
        Assert.Equal("\"x.exe\" " + expected, WindowsCommandLine.Build("x.exe", [argument]));
    }

    [Fact]
    public void RefusesAQuoteInTheProgramPath()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\"b.exe", []));
    }
}
