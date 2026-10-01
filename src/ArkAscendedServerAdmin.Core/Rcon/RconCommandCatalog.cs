namespace ArkAscendedServerAdmin.Rcon;

/// <summary>
/// One entry of the console's command suggestions: <paramref name="Name"/> as typed, the arguments it takes in
/// <paramref name="Syntax"/> (empty when it takes none), a one-line <paramref name="Description"/>, and the
/// <paramref name="Group"/> it is listed under when the full list is browsed. <paramref name="Note"/> is a warning shown
/// apart from the description wherever the command appears; null for most commands.
/// </summary>
public sealed record RconCommandInfo(string Name, string Syntax, string Description, string Group, string? Note = null)
{
    public bool TakesArguments => Syntax.Length > 0;
}

/// <summary>
/// Commonly used ASA admin commands that work over RCON, i.e. without a player pawn behind them. Checked against the
/// ARK wiki's console commands page; ASA identifies players by EOS id (32 hex characters, as <c>ListPlayers</c>
/// prints them), not by Steam id. Anything the game accepts can still be typed; this list only feeds suggestions.
/// </summary>
public static class RconCommandCatalog
{
    public const string PlayersGroup = "Players";
    public const string ChatGroup = "Chat and messages";
    public const string WorldGroup = "World and server";

    public static IReadOnlyList<RconCommandInfo> All { get; } =
    [
        new("SaveWorld", "", "Writes the world to disk now.", WorldGroup),
        new("DoExit", "", "Saves and shuts the server down; the manager notices the exit.", WorldGroup),
        new("DestroyWildDinos", "", "Removes every wild creature so they respawn; tames are untouched.", WorldGroup),
        new("SetTimeOfDay", "<hh:mm[:ss]>", "Sets the in-game time of day.", WorldGroup),
        new("GetGameLog", "", "Returns the latest game log entries and writes them to a dated file.", WorldGroup),

        new("Broadcast", "<message>", "Shows a message in the middle of every player's screen.", ChatGroup),
        new("ServerChat", "<message>", "Sends a chat message to every player.", ChatGroup),
        new("ServerChatTo", "\"<EOS id>\" <message>", "Sends a private chat message to one player by EOS id.", ChatGroup),
        new("ServerChatToPlayer", "\"<player name>\" <message>", "Sends a private chat message to one player by name.", ChatGroup),
        new("GetChat", "", "Returns the recent chat, the same lines the players see.", ChatGroup),
        new("SetMessageOfTheDay", "<message>", "Sets the message shown to players when they join.", ChatGroup),

        new("ListPlayers", "", "Lists connected players with their EOS ids.", PlayersGroup),
        new("KickPlayer", "<EOS id>", "Disconnects a player; they can join again at once.", PlayersGroup),
        new("AllowPlayerToJoinNoCheck", "<EOS id>", "Adds a player to the server's join whitelist; applies at once.", PlayersGroup),
        new("DisallowPlayerToJoinNoCheck", "<EOS id>", "Removes a player from the server's join whitelist.", PlayersGroup),
        new("RenamePlayer", "\"<current name>\" <new name>", "Renames a player's survivor.", PlayersGroup),
        new("RenameTribe", "\"<tribe name>\" <new name>", "Renames a tribe.", PlayersGroup),
        new("GetTribeIdPlayerList", "<tribe id>", "Lists the members of a tribe.", PlayersGroup),
        new("BanPlayer", "<EOS id>", "Bans a player and disconnects them.", PlayersGroup, SharedBanListNote("Bans the player")),
        new("UnbanPlayer", "<EOS id>", "Lifts a player's ban.", PlayersGroup, SharedBanListNote("Lifts the ban")),
    ];

    /// <summary>The game keeps one <c>BanList.txt</c> in the shared install, so a ban or unban reaches every instance.</summary>
    private static string SharedBanListNote(string action) =>
        $"{action} on every managed instance on this machine, not just this one: they all share one ban list.";

    /// <summary>Group names in the order the full list shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } = [WorldGroup, ChatGroup, PlayersGroup];

    /// <summary>The entry named <paramref name="name"/>, ignoring case; null when it is not in the catalog.</summary>
    public static RconCommandInfo? Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Filtering and ranking for the console's command suggestions; pure so the rules are unit-tested.</summary>
public static class RconCommandSuggester
{
    /// <summary>
    /// Suggestions for what is in the input. Empty once the first word is followed by whitespace (the user is
    /// typing arguments) or when nothing is typed. Otherwise names that start with the text come first, then names
    /// that contain it, each in alphabetical order; matching ignores case.
    /// </summary>
    public static IReadOnlyList<RconCommandInfo> Suggest(string? input, IReadOnlyList<RconCommandInfo> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = (input ?? string.Empty).TrimStart();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return [];
        }

        return catalog
            .Select(c => (Command: c, Index: c.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m.Index >= 0)
            .OrderBy(m => m.Index == 0 ? 0 : 1)
            .ThenBy(m => m.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Command)
            .ToList();
    }

    /// <summary>
    /// The catalog entry whose arguments are being typed: the first word matches a name exactly (ignoring case)
    /// and is followed by whitespace. Null otherwise.
    /// </summary>
    public static RconCommandInfo? ArgumentHint(string? input, IReadOnlyList<RconCommandInfo> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = (input ?? string.Empty).TrimStart();
        var end = text.IndexOfAny([' ', '\t']);
        if (end <= 0)
        {
            return null;
        }

        var word = text[..end];
        return catalog.FirstOrDefault(c => string.Equals(c.Name, word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What accepting <paramref name="command"/> puts in the input: its name, plus a space when it takes arguments.</summary>
    public static string Accept(RconCommandInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.TakesArguments ? command.Name + " " : command.Name;
    }
}
