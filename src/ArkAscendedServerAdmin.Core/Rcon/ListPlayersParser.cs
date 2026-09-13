using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Rcon;

public sealed record ListedPlayer(string Name, string EosId);

/// <summary>
/// Parses the reply to <see cref="RconCommands.ListPlayers"/>. Verified against a live ASA server on
/// 2026-09-13 with one player connected: <c>0. HeloRaptr, 0002f16bad3d4330b6097fcec38c5610</c>, the same
/// id the <c>ShooterGame.log</c> join line carries as <c>UniqueNetId</c>. The parser stays tolerant: it
/// accepts <c>0. Name, &lt;id&gt;</c> and <c>Name, &lt;id&gt;</c> lines where the id is a 32-hex-digit EOS id
/// or a 17-digit Steam id, and ignores everything else.
/// </summary>
public static partial class ListPlayersParser
{
    public static IReadOnlyList<ListedPlayer> Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var players = new List<ListedPlayer>();
        foreach (var rawLine in reply.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(RconCommands.NoPlayersReply, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = LinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            var id = match.Groups["id"].Value;
            if (name.Length > 0 && !players.Any(p => p.EosId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                players.Add(new ListedPlayer(name, id));
            }
        }

        return players;
    }

    [GeneratedRegex(@"^(?:\d+\.\s*)?(?<name>.+?),\s*(?<id>[0-9a-fA-F]{32}|\d{17})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();
}
