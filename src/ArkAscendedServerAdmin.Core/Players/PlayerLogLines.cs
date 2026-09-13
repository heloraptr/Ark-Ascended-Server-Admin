using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Players;

public enum PlayerPresence
{
    Joined,
    Left,
}

/// <param name="Name">The player's display name as the game logged it.</param>
/// <param name="EosId">The <c>UniqueNetId</c> token: a 32-hex-digit EOS id or a 17-digit Steam id.</param>
/// <param name="Platform">The <c>Platform:</c> token, or null when it was empty.</param>
/// <param name="Presence">Joined or left.</param>
/// <param name="At">The line's own timestamp (the game writes it in UTC), or null when the prefix was missing.</param>
public sealed record PlayerLogEvent(string Name, string EosId, string? Platform, PlayerPresence Presence, DateTimeOffset? At);

/// <summary>
/// Recognizes the join and leave lines of <c>ShooterGame.log</c>. Captured 2026-09-13 from a live server:
/// <c>[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: HeloRaptr [UniqueNetId:0002f16bad3d4330b6097fcec38c5610 Platform:None] joined this ARK!</c>
/// and the matching <c>left this ARK!</c>. The bracketed stamp is UTC (the same file's <c>Log file open</c>
/// line shows local time four hours earlier). Both prefixes are optional so a line pasted without them
/// still parses; the id is a 32-hex-digit EOS id or a 17-digit Steam id.
/// </summary>
public static partial class PlayerLogLines
{
    private const string StampFormat = "yyyy.MM.dd-HH.mm.ss:fff";

    public static PlayerLogEvent? TryParse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.Contains("this ARK!", StringComparison.Ordinal))
        {
            return null;
        }

        var match = LinePattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        DateTimeOffset? at = null;
        var stamp = match.Groups["stamp"];
        if (stamp.Success && DateTimeOffset.TryParseExact(stamp.Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            at = parsed;
        }

        var platform = match.Groups["platform"].Value.Trim();
        return new PlayerLogEvent(
            match.Groups["name"].Value.Trim(),
            match.Groups["id"].Value,
            platform.Length == 0 ? null : platform,
            match.Groups["verb"].Value == "joined" ? PlayerPresence.Joined : PlayerPresence.Left,
            at);
    }

    [GeneratedRegex(
        @"^(?:\[(?<stamp>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3})\]\[\s*\d+\])?(?:\d{4}\.\d{2}\.\d{2}_\d{2}\.\d{2}\.\d{2}:\s*)?(?<name>.+?)\s+\[UniqueNetId:(?<id>[0-9a-fA-F]{32}|\d{17})\s+Platform:(?<platform>[^\]]*)\]\s+(?<verb>joined|left) this ARK!\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();
}
