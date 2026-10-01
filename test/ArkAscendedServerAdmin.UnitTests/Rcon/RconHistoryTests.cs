using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.UnitTests.Rcon;

public sealed class RconHistoryTests
{
    [Fact]
    public void Append_drops_the_oldest_past_the_capacity()
    {
        var history = new List<string>();
        for (var i = 0; i < RconHistory.Capacity + 5; i++)
        {
            Assert.True(RconHistory.Append(history, $"cmd {i}"));
        }

        Assert.Equal(RconHistory.Capacity, history.Count);
        Assert.Equal("cmd 5", history[0]);
        Assert.Equal($"cmd {RconHistory.Capacity + 4}", history[^1]);
    }

    [Fact]
    public void Append_refuses_a_repeat_of_the_newest_entry_only()
    {
        var history = new List<string> { "ListPlayers", "SaveWorld" };

        Assert.False(RconHistory.Append(history, "  SaveWorld "));
        Assert.True(RconHistory.Append(history, "ListPlayers"));
        Assert.Equal(["ListPlayers", "SaveWorld", "ListPlayers"], history);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Broadcast one\ntwo")]
    [InlineData("Broadcast one\rtwo")]
    public void Clean_refuses_blank_and_multi_line_commands(string command)
    {
        Assert.Null(RconHistory.Clean(command));
        var history = new List<string>();
        Assert.False(RconHistory.Append(history, command));
        Assert.Empty(history);
    }

    [Fact]
    public void Overlong_commands_are_skipped_not_truncated()
    {
        var longest = "Broadcast " + new string('x', RconHistory.MaxCommandLength - 10);
        var tooLong = longest + "y";

        Assert.Equal(longest, RconHistory.Clean(longest));
        Assert.Null(RconHistory.Clean(tooLong));
    }

    [Fact]
    public void Normalize_reads_a_hand_edited_file()
    {
        var lines = new[] { "", "ListPlayers", "   ", "ListPlayers", "  SaveWorld  ", "", new string('x', RconHistory.MaxCommandLength + 1), "SaveWorld", "DoExit" };

        Assert.Equal(["ListPlayers", "SaveWorld", "DoExit"], RconHistory.Normalize(lines));
    }

    [Fact]
    public void Normalize_keeps_the_newest_entries()
    {
        var lines = Enumerable.Range(0, 500).Select(i => $"cmd {i}");

        var history = RconHistory.Normalize(lines);

        Assert.Equal(RconHistory.Capacity, history.Count);
        Assert.Equal("cmd 300", history[0]);
        Assert.Equal("cmd 499", history[^1]);
    }
}
