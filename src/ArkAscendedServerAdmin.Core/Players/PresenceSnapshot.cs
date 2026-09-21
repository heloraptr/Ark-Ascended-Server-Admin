using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Players;

/// <summary>
/// What one <c>ListPlayers</c> reply justifies changing in the known players table, decided from the rows the
/// table already holds. A reply is a snapshot of the instance as of the moment the command was sent, so it
/// only outranks rows whose newest evidence is older than that moment: a join or leave line, or a later reply
/// on any instance, that arrived while the command was in flight stays in charge. Pure; the tracker persists
/// the result.
/// </summary>
public static class PresenceSnapshot
{
    /// <summary>True when a reply sent at <paramref name="sentAt"/> may overwrite a row whose newest evidence is <paramref name="lastSeenAt"/>.</summary>
    public static bool Outranks(DateTimeOffset sentAt, DateTimeOffset lastSeenAt) => lastSeenAt < sentAt;

    /// <summary>
    /// Sorts the reply against the rows: listed players without a row are newcomers; listed players whose row is
    /// older than the reply go online on the instance; rows online on the instance but missing from the reply, and
    /// older than it, go offline. Rows on other instances are only touched when the reply lists them and outranks
    /// them (the player transferred here). Ids compare case-insensitively.
    /// </summary>
    /// <param name="instanceId">The instance the reply came from.</param>
    /// <param name="sentAt">When the command was sent, taken before it went out.</param>
    /// <param name="listed">The parsed reply.</param>
    /// <param name="rows">Every row that is listed or online on the instance; extra rows are ignored.</param>
    public static PresenceChanges Decide(int instanceId, DateTimeOffset sentAt, IReadOnlyList<ListedPlayer> listed, IReadOnlyList<KnownPlayer> rows)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(rows);

        var byId = new Dictionary<string, KnownPlayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            byId.TryAdd(row.EosId, row);
        }

        var newcomers = new List<ListedPlayer>();
        var online = new List<(KnownPlayer Row, ListedPlayer Listed)>();
        var listedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in listed)
        {
            if (!listedIds.Add(player.EosId))
            {
                continue;
            }

            if (!byId.TryGetValue(player.EosId, out var row))
            {
                newcomers.Add(player);
            }
            else if (Outranks(sentAt, row.LastSeenAt))
            {
                online.Add((row, player));
            }
        }

        var offline = rows
            .Where(r => r.LastInstanceId == instanceId && r.IsOnline && !listedIds.Contains(r.EosId) && Outranks(sentAt, r.LastSeenAt))
            .ToList();

        return new PresenceChanges(newcomers, online, offline);
    }
}

/// <summary>The outcome of <see cref="PresenceSnapshot.Decide"/>.</summary>
/// <param name="Newcomers">Listed players with no row yet.</param>
/// <param name="Online">Existing rows the reply puts online on the instance, with their listed entry (the name may have changed).</param>
/// <param name="Offline">Rows online on the instance that the reply no longer lists.</param>
public sealed record PresenceChanges(
    IReadOnlyList<ListedPlayer> Newcomers,
    IReadOnlyList<(KnownPlayer Row, ListedPlayer Listed)> Online,
    IReadOnlyList<KnownPlayer> Offline)
{
    /// <summary>True when the reply changes nothing.</summary>
    public bool IsEmpty => Newcomers.Count == 0 && Online.Count == 0 && Offline.Count == 0;
}
