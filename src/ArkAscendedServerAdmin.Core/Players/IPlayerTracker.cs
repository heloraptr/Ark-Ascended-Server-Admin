using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Players;

/// <summary>
/// Keeps the known players table current. Follows every instance console for join and leave lines
/// (live and backfilled), marks an instance's players offline when its process is gone, and merges
/// <c>ListPlayers</c> replies when the owner asks an instance who is on.
/// </summary>
public interface IPlayerTracker
{
    /// <summary>Raised on a background thread after the table changed; pages reload their copy.</summary>
    event Action? Changed;

    /// <summary>
    /// Records a <c>ListPlayers</c> reply: everyone listed is online on the instance now; anyone the table
    /// thought was online there but who is missing from the reply is marked offline.
    /// </summary>
    Task RecordListedAsync(int instanceId, IReadOnlyList<ListedPlayer> players, CancellationToken cancellationToken);
}
