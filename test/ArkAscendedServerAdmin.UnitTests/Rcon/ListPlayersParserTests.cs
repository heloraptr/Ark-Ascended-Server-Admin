using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.UnitTests.Rcon;

public sealed class ListPlayersParserTests
{
    [Fact]
    public void Parses_numbered_lines_with_eos_ids()
    {
        var reply = "0. Survivor One, 0002d4b9c8a44e5b8f1d2a3b4c5d6e7f\n1. Two Words Name, ABCDEF0123456789ABCDEF0123456789\n";

        var players = ListPlayersParser.Parse(reply);

        Assert.Equal(2, players.Count);
        Assert.Equal("Survivor One", players[0].Name);
        Assert.Equal("0002d4b9c8a44e5b8f1d2a3b4c5d6e7f", players[0].EosId);
        Assert.Equal("Two Words Name", players[1].Name);
    }

    [Fact]
    public void Accepts_unnumbered_lines_and_steam_ids()
    {
        var players = ListPlayersParser.Parse("Name, 76561198000000001\r\n");

        var player = Assert.Single(players);
        Assert.Equal("76561198000000001", player.EosId);
    }

    [Fact]
    public void Ignores_the_no_players_reply_and_noise()
    {
        var players = ListPlayersParser.Parse("No Players Connected\nsome unrelated line\n\n");

        Assert.Empty(players);
    }

    [Fact]
    public void Deduplicates_by_id_ignoring_case()
    {
        var reply = "0. A, abcdef0123456789abcdef0123456789\n1. A again, ABCDEF0123456789ABCDEF0123456789\n";

        var players = ListPlayersParser.Parse(reply);

        Assert.Single(players);
    }

    [Fact]
    public void Keeps_commas_inside_names()
    {
        var players = ListPlayersParser.Parse("0. Last, First, 0002d4b9c8a44e5b8f1d2a3b4c5d6e7f");

        var player = Assert.Single(players);
        Assert.Equal("Last, First", player.Name);
    }

    [Fact]
    public void Parses_the_reply_captured_from_a_live_server_on_2026_09_13()
    {
        // One player connected to an ASA dedicated server (build 25241345); the log's join line carried the same id.
        var players = ListPlayersParser.Parse("0. Survivor42, 0002c0ffee11d00d4242beef00c0ffee\n");

        var player = Assert.Single(players);
        Assert.Equal(("Survivor42", "0002c0ffee11d00d4242beef00c0ffee"), (player.Name, player.EosId));
    }
}
