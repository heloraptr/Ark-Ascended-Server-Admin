using ArkAscendedServerAdmin.Players;

namespace ArkAscendedServerAdmin.UnitTests.Players;

public sealed class PlayerLogLinesTests
{
    // Captured 2026-09-13 from a live server; the bracketed stamp is UTC (the log's "Log file open" line
    // showed 14:31 local for an 18:31 stamp).
    private const string JoinLine = "[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: Survivor42 [UniqueNetId:0002c0ffee11d00d4242beef00c0ffee Platform:None] joined this ARK!";
    private const string LeaveLine = "[2026.09.13-18.50.08:364][320]2026.09.13_18.50.08: Survivor42 [UniqueNetId:0002c0ffee11d00d4242beef00c0ffee Platform:None] left this ARK!";

    [Fact]
    public void Parses_the_captured_join_line_with_its_utc_stamp()
    {
        var parsed = PlayerLogLines.TryParse(JoinLine);

        Assert.NotNull(parsed);
        Assert.Equal("Survivor42", parsed.Name);
        Assert.Equal("0002c0ffee11d00d4242beef00c0ffee", parsed.EosId);
        Assert.Equal("None", parsed.Platform);
        Assert.Equal(PlayerPresence.Joined, parsed.Presence);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 18, 48, 40, 782, TimeSpan.Zero), parsed.At);
    }

    [Fact]
    public void Parses_the_captured_leave_line()
    {
        var parsed = PlayerLogLines.TryParse(LeaveLine);

        Assert.NotNull(parsed);
        Assert.Equal(PlayerPresence.Left, parsed.Presence);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 18, 50, 8, 364, TimeSpan.Zero), parsed.At);
    }

    [Fact]
    public void Accepts_a_line_without_prefixes_and_reports_no_time()
    {
        var parsed = PlayerLogLines.TryParse("Some Body [UniqueNetId:76561198000000001 Platform:Steam] joined this ARK!");

        Assert.NotNull(parsed);
        Assert.Equal(("Some Body", "76561198000000001", "Steam"), (parsed.Name, parsed.EosId, parsed.Platform));
        Assert.Null(parsed.At);
    }

    [Fact]
    public void Keeps_brackets_in_the_name_and_nulls_an_empty_platform()
    {
        var parsed = PlayerLogLines.TryParse("[2026.09.13-18.48.40:782][  1]2026.09.13_18.48.40: Two Words [Tribe] [UniqueNetId:ABCDEF0123456789ABCDEF0123456789 Platform:] left this ARK!");

        Assert.NotNull(parsed);
        Assert.Equal("Two Words [Tribe]", parsed.Name);
        Assert.Null(parsed.Platform);
        Assert.Equal(PlayerPresence.Left, parsed.Presence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No Players Connected")]
    [InlineData("Survivor42 joined this ARK!")]
    [InlineData("[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: Survivor42 [UniqueNetId:notanid Platform:None] joined this ARK!")]
    [InlineData("[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: Survivor42 (Global): someone joined this ARK!")]
    public void Ignores_everything_else(string line)
    {
        Assert.Null(PlayerLogLines.TryParse(line));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerLogLines.TryParse(null!));
    }
}
