using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.UnitTests.Rcon;

public sealed class RconCommandSuggesterTests
{
    private static readonly IReadOnlyList<RconCommandInfo> Catalog =
    [
        new("SaveWorld", "", "Save.", "World"),
        new("ServerChat", "<message>", "Chat.", "Chat"),
        new("ServerChatTo", "\"<EOS id>\" <message>", "Whisper.", "Chat"),
        new("ListPlayers", "", "List.", "Players"),
        new("BanPlayer", "<EOS id>", "Ban.", "Players"),
        new("KickPlayer", "<EOS id>", "Kick.", "Players"),
    ];

    [Fact]
    public void Prefix_matches_come_before_substring_matches_and_ignore_case()
    {
        var names = RconCommandSuggester.Suggest("pla", Catalog).Select(c => c.Name);

        Assert.Equal(["BanPlayer", "KickPlayer", "ListPlayers"], names);
        Assert.Equal(["SaveWorld", "ServerChat", "ServerChatTo", "ListPlayers"], RconCommandSuggester.Suggest("S", Catalog).Select(c => c.Name));
    }

    [Fact]
    public void Prefix_group_is_alphabetical_then_contains_group()
    {
        var names = RconCommandSuggester.Suggest("ser", Catalog).Select(c => c.Name).ToList();

        Assert.Equal(["ServerChat", "ServerChatTo"], names);

        var ranked = RconCommandSuggester.Suggest("PLAYER", Catalog).Select(c => c.Name).ToList();
        Assert.Equal(["BanPlayer", "KickPlayer", "ListPlayers"], ranked);

        var mixed = RconCommandSuggester.Suggest("l", Catalog).Select(c => c.Name).ToList();
        Assert.Equal("ListPlayers", mixed[0]); // the only prefix match leads
        Assert.Contains("SaveWorld", mixed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ServerChat ")]
    [InlineData("ServerChat hello")]
    [InlineData("zzz")]
    public void Nothing_is_suggested_without_a_first_word_match_or_once_arguments_start(string input)
    {
        Assert.Empty(RconCommandSuggester.Suggest(input, Catalog));
    }

    [Fact]
    public void Leading_whitespace_is_ignored()
    {
        Assert.Equal("SaveWorld", Assert.Single(RconCommandSuggester.Suggest("  savew", Catalog)).Name);
    }

    [Fact]
    public void Argument_hint_names_the_exact_command_being_given_arguments()
    {
        Assert.Equal("ServerChatTo", RconCommandSuggester.ArgumentHint("serverchatto \"abc\" hi", Catalog)?.Name);
        Assert.Equal("ServerChat", RconCommandSuggester.ArgumentHint("ServerChat ", Catalog)?.Name);
        Assert.Null(RconCommandSuggester.ArgumentHint("ServerChat", Catalog));
        Assert.Null(RconCommandSuggester.ArgumentHint("Unknown arg", Catalog));
        Assert.Null(RconCommandSuggester.ArgumentHint("", Catalog));
    }

    [Fact]
    public void Accept_adds_a_space_only_when_the_command_takes_arguments()
    {
        Assert.Equal("SaveWorld", RconCommandSuggester.Accept(Catalog[0]));
        Assert.Equal("ServerChat ", RconCommandSuggester.Accept(Catalog[1]));
    }

    [Fact]
    public void Catalog_names_are_unique_and_every_entry_is_complete()
    {
        var all = RconCommandCatalog.All;

        Assert.InRange(all.Count, 15, 40);
        Assert.Equal(all.Count, all.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.Contains(c.Group, RconCommandCatalog.Groups);
            Assert.DoesNotContain(' ', c.Name);
        });

        // Listed group by group, so the browsed list needs no sorting.
        var groupOrder = all.Select(c => RconCommandCatalog.Groups.ToList().IndexOf(c.Group)).ToList();
        Assert.Equal(groupOrder.Order(), groupOrder);
    }

    [Fact]
    public void Ban_commands_say_the_list_is_shared_by_the_box()
    {
        Assert.Contains("every server on this box", RconCommandCatalog.Find("banplayer")!.Description, StringComparison.Ordinal);
        Assert.Contains("every server on this box", RconCommandCatalog.Find("UnbanPlayer")!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Player_commands_take_eos_ids_not_steam_ids()
    {
        Assert.All(RconCommandCatalog.All, c => Assert.DoesNotContain("Steam", c.Syntax, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("<EOS id>", RconCommandCatalog.Find("KickPlayer")!.Syntax);
    }
}
