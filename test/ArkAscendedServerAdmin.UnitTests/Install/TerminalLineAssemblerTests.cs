using System.Text;
using ArkAscendedServerAdmin.Install;

namespace ArkAscendedServerAdmin.UnitTests.Install;

/// <summary>Inputs follow what ConPTY actually wrote for SteamCMD and <c>cmd.exe</c> during the live-output spike.</summary>
public class TerminalLineAssemblerTests
{
    /// <summary>The first frame and title of a real SteamCMD run, followed by its first lines.</summary>
    private const string SteamCmdStart =
        "\u001b[?9001h\u001b[?1004h\u001b[?25l\u001b[2J\u001b[m\u001b[HRedirecting stderr to 'C:\\SteamCMD\\logs\\stderr.txt'\r\n"
        + "\u001b]0;C:\\SteamCMD\\steamcmd.exe\u0007\u001b[?25h"
        + "[  0%] Checking for available updates...\r\n"
        + "[----] Verifying installation...\r\n"
        + "Loading Steam API...OK\r\n"
        + "\r\n"
        + " Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)\r\n"
        + "\u001b[?9001l\u001b[?1004l";

    private static readonly string[] _steamCmdLines =
    [
        "Redirecting stderr to 'C:\\SteamCMD\\logs\\stderr.txt'",
        "[  0%] Checking for available updates...",
        "[----] Verifying installation...",
        "Loading Steam API...OK",
        "",
        " Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)",
    ];

    [Fact]
    public void StripsTheSequences_AndKeepsTheLinesInOrder()
    {
        Assert.Equal(_steamCmdLines, Feed(SteamCmdStart));
    }

    [Fact]
    public void AnyChunking_GivesTheSameLines()
    {
        var bytes = Encoding.UTF8.GetBytes(SteamCmdStart);
        for (var size = 1; size <= 7; size++)
        {
            Assert.Equal(_steamCmdLines, FeedBytes(bytes, size));
        }
    }

    [Fact]
    public void AnEscapeSequenceSplitAcrossChunks_IsStillStripped()
    {
        var lines = new List<string>();
        var assembler = new TerminalLineAssembler(lines.Add);

        assembler.Append("before\u001b"u8);
        assembler.Append("[?2"u8);
        assembler.Append("5hafter\u001b]0;ti"u8);
        assembler.Append("tle\u001b"u8);
        assembler.Append("\\!\r"u8);
        assembler.Append("\n"u8);

        Assert.Equal(["beforeafter!"], lines);
    }

    [Fact]
    public void ALineSplitAcrossChunks_IsEmittedOnceWhenItEnds()
    {
        var lines = new List<string>();
        var assembler = new TerminalLineAssembler(lines.Add);

        assembler.Append("Loading Steam API..."u8);
        Assert.Empty(lines);
        assembler.Append("OK\r\nWaiting"u8);
        Assert.Equal(["Loading Steam API...OK"], lines);
        assembler.Append(" for user info...OK\n"u8);

        Assert.Equal(["Loading Steam API...OK", "Waiting for user info...OK"], lines);
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossChunks_IsDecoded()
    {
        var bytes = Encoding.UTF8.GetBytes("Café ✓\r\n");
        Assert.Equal(["Café ✓"], FeedBytes(bytes, 1));
    }

    [Fact]
    public void ABareCarriageReturn_OverwritesTheLine()
    {
        Assert.Equal(["50%", "done"], Feed("10%\r20%\r50%\r\ndone\n"));
    }

    [Fact]
    public void EmptyLinesMadeOnlyOfSequences_AreDropped_ButRealBlankLinesStay()
    {
        Assert.Equal(["a", "", "b"], Feed("a\r\n\u001b[?25l\u001b[m\r\n\r\nb\r\n"));
    }

    [Fact]
    public void CursorForward_BecomesSpaces()
    {
        Assert.Equal(["[  0%]", "x y"], Feed("[\u001b[2C0%]\r\nx\u001b[Cy\r\n"));
    }

    [Fact]
    public void OtherCsiAndSingleCharacterEscapes_AreDropped()
    {
        Assert.Equal(["abcdef"], Feed("a\u001b[1;1Hb\u001b[Kc\u001b7d\u001b(Be\u001b[38;5;196mf\r\n"));
    }

    [Fact]
    public void Complete_FlushesAnUnterminatedLastLine()
    {
        var lines = new List<string>();
        var assembler = new TerminalLineAssembler(lines.Add);

        assembler.Append("Unloading Steam API...OK"u8);
        assembler.Complete();

        Assert.Equal(["Unloading Steam API...OK"], lines);
    }

    [Fact]
    public void AnOverlongLine_IsCutAtTheLimit()
    {
        var lines = Feed(new string('x', TerminalLineAssembler.MaxLineLength + 5) + "\n");

        Assert.Equal([TerminalLineAssembler.MaxLineLength, 5], lines.Select(l => l.Length));
    }

    [Fact]
    public void ProgressLines_StillParse()
    {
        var lines = Feed("\u001b[?25l Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)\r\n\u001b[?25h");

        var line = Assert.Single(lines);
        Assert.True(SteamCmdOutput.TryParseProgress(line, out var progress));
        Assert.Equal(15.67, progress.Percent, precision: 3);
    }

    private static List<string> Feed(string text) => FeedBytes(Encoding.UTF8.GetBytes(text), int.MaxValue);

    private static List<string> FeedBytes(byte[] bytes, int chunkSize)
    {
        var lines = new List<string>();
        var assembler = new TerminalLineAssembler(lines.Add);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            assembler.Append(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }

        assembler.Complete();
        return lines;
    }
}
