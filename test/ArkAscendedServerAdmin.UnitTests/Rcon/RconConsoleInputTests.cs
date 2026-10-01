using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.UnitTests.Rcon;

public sealed class RconConsoleInputTests
{
    private static readonly IReadOnlyList<RconCommandInfo> Catalog =
    [
        new("SaveWorld", "", "Save.", "World"),
        new("ServerChat", "<message>", "Chat.", "Chat"),
        new("ServerChatTo", "\"<EOS id>\" <message>", "Whisper.", "Chat"),
        new("ListPlayers", "", "List.", "Players"),
    ];

    private static RconConsoleInput Create(params string[] history)
    {
        var input = new RconConsoleInput(Catalog);
        input.SetHistory(history);
        return input;
    }

    [Fact]
    public void Typing_the_first_word_opens_the_list_and_arguments_close_it()
    {
        var input = Create();

        input.TextChanged("ser");
        Assert.True(input.IsListOpen);
        Assert.Equal(["ServerChat", "ServerChatTo"], input.Items.Select(c => c.Name));
        Assert.Equal(-1, input.Highlight);

        input.TextChanged("ServerChat hi");
        Assert.False(input.IsListOpen);
        Assert.Equal("ServerChat", input.Hint?.Name);

        input.TextChanged("nothing");
        Assert.False(input.IsListOpen);
        Assert.Null(input.Hint);
    }

    [Fact]
    public void ArrowUp_walks_history_even_with_the_list_open_and_ArrowDown_restores_the_draft()
    {
        var input = Create("ListPlayers", "SaveWorld");
        input.TextChanged("ser");

        Assert.Equal(RconKeyOutcome.Handled, input.Key("ArrowUp"));
        Assert.Equal("SaveWorld", input.Text);
        Assert.False(input.IsListOpen); // a recalled entry does not reopen suggestions

        input.Key("ArrowUp");
        Assert.Equal("ListPlayers", input.Text);
        input.Key("ArrowUp");
        Assert.Equal("ListPlayers", input.Text); // stays on the oldest

        input.Key("ArrowDown");
        Assert.Equal("SaveWorld", input.Text);
        input.Key("ArrowDown");
        Assert.Equal("ser", input.Text); // the draft is back
        Assert.Equal(-1, input.HistoryIndex);
    }

    [Fact]
    public void ArrowDown_at_the_draft_moves_into_the_open_list_then_the_arrows_move_the_highlight()
    {
        var input = Create("ListPlayers");
        input.TextChanged("ser");

        input.Key("ArrowDown");
        Assert.Equal(0, input.Highlight);
        input.Key("ArrowDown");
        Assert.Equal(1, input.Highlight);
        input.Key("ArrowDown");
        Assert.Equal(1, input.Highlight); // stays on the last item

        input.Key("ArrowUp");
        Assert.Equal(0, input.Highlight);
        Assert.Equal("ser", input.Text);
        input.Key("ArrowUp");
        Assert.Equal(-1, input.Highlight); // back in the input, nothing highlighted
        Assert.Equal("ser", input.Text);

        input.Key("ArrowUp");
        Assert.Equal("ListPlayers", input.Text); // and now history again
    }

    [Fact]
    public void ArrowDown_with_no_list_and_no_recalled_entry_does_nothing()
    {
        var input = Create("ListPlayers");
        input.TextChanged("SaveWorld now");

        Assert.Equal(RconKeyOutcome.Handled, input.Key("ArrowDown"));
        Assert.Equal("SaveWorld now", input.Text);
        Assert.Equal(-1, input.Highlight);
    }

    [Fact]
    public void Tab_accepts_the_top_match_or_the_highlighted_item_only_while_the_list_is_open()
    {
        var input = Create();
        input.TextChanged("serv");

        Assert.Equal(RconKeyOutcome.Handled, input.Key("Tab"));
        Assert.Equal("ServerChat ", input.Text);
        Assert.False(input.IsListOpen);

        Assert.Equal(RconKeyOutcome.Ignored, input.Key("Tab")); // closed: focus moves on as usual

        input.TextChanged("serv");
        input.Key("ArrowDown");
        input.Key("ArrowDown");
        input.Key("Tab");
        Assert.Equal("ServerChatTo ", input.Text);

        input.TextChanged("save");
        input.Key("Tab");
        Assert.Equal("SaveWorld", input.Text); // no arguments, no trailing space
    }

    [Fact]
    public void Enter_sends_what_is_typed_unless_an_item_was_highlighted_with_the_arrows()
    {
        var input = Create();
        input.TextChanged("serv");
        Assert.Equal(RconKeyOutcome.Send, input.Key("Enter"));
        Assert.Equal("serv", input.Text);

        input.Key("ArrowDown");
        Assert.Equal(RconKeyOutcome.Handled, input.Key("Enter"));
        Assert.Equal("ServerChat ", input.Text);
        Assert.False(input.IsListOpen);
    }

    [Fact]
    public void Escape_closes_the_list_and_clears_the_highlight()
    {
        var input = Create();
        input.TextChanged("serv");
        input.Key("ArrowDown");

        Assert.Equal(RconKeyOutcome.Handled, input.Key("Escape"));
        Assert.False(input.IsListOpen);
        Assert.Equal(-1, input.Highlight);
        Assert.Equal("serv", input.Text);
        Assert.Equal(RconKeyOutcome.Send, input.Key("Enter"));
    }

    [Fact]
    public void Browsing_shows_the_whole_catalog_until_typing_filters_it()
    {
        var input = Create();

        input.ToggleBrowse();
        Assert.True(input.IsBrowsing);
        Assert.Equal(Catalog.Count, input.Items.Count);

        input.Key("ArrowDown");
        input.Key("ArrowDown");
        input.Key("Enter");
        Assert.Equal("ServerChat ", input.Text);
        Assert.False(input.IsBrowsing);

        input.ToggleBrowse();
        input.TextChanged("list");
        Assert.False(input.IsBrowsing);
        Assert.Equal("ListPlayers", Assert.Single(input.Items).Name);
    }

    [Fact]
    public void Sent_empties_the_input_and_reset_drops_everything_for_another_instance()
    {
        var input = Create("ListPlayers");
        input.TextChanged("draft");
        input.Key("ArrowUp");

        input.Sent(input.Session, "ListPlayers");
        Assert.Equal(string.Empty, input.Text);
        Assert.Equal(-1, input.HistoryIndex);

        input.TextChanged("ser");
        input.Key("ArrowDown");
        input.Reset(["SaveWorld"]);
        Assert.Equal(string.Empty, input.Text);
        Assert.False(input.IsListOpen);
        Assert.Equal(-1, input.Highlight);
        input.Key("ArrowUp");
        Assert.Equal("SaveWorld", input.Text);
        input.Key("ArrowDown");
        Assert.Equal(string.Empty, input.Text); // the old draft did not carry over
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var input = Create();
        input.TextChanged("ser");

        Assert.Equal(RconKeyOutcome.Ignored, input.Key("a"));
        Assert.True(input.IsListOpen);
    }

    [Fact]
    public void Sent_appends_locally_without_repeating_the_newest_entry()
    {
        var input = Create("ListPlayers");
        input.TextChanged("SaveWorld");

        input.Sent(input.Session, "SaveWorld");
        input.Sent(input.Session, "SaveWorld");

        Assert.Equal(["ListPlayers", "SaveWorld"], input.History);
        Assert.Equal(string.Empty, input.Text);
    }

    /// <summary>
    /// A slow send on one instance finishing after the panel was reset for another must not leak into the other
    /// instance's history or wipe what the user has started typing there.
    /// </summary>
    [Fact]
    public void A_send_that_finishes_after_a_reset_leaves_the_new_instance_alone()
    {
        var input = Create("ListPlayers");
        input.TextChanged("DoExit");
        var session = input.Session;

        input.Reset(["SaveWorld"]); // the page moved to another instance while DoExit was in flight
        input.TextChanged("Broad");
        input.Sent(session, "DoExit");

        Assert.Equal(["SaveWorld"], input.History);
        Assert.Equal("Broad", input.Text);
        input.Key("ArrowUp");
        Assert.Equal("SaveWorld", input.Text);
    }
}
