using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Commands;

/// <param name="Players">The instance's <c>ListPlayers</c> reply, parsed.</param>
/// <param name="AsOf">When the reply arrived.</param>
public sealed record OnlinePlayers(IReadOnlyList<ListedPlayer> Players, DateTimeOffset AsOf);

/// <summary>
/// Scoped, guarded facade for the Players page and the instance Players tab. The table itself is fed by
/// <see cref="Players.IPlayerTracker"/> from the game log; this facade reads it, asks one instance who is
/// on right now, and forgets rows.
/// </summary>
public interface IPlayerCommands
{
    /// <summary>Every known player with <see cref="KnownPlayer.LastInstance"/> loaded, by name.</summary>
    Task<IReadOnlyList<KnownPlayer>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <c>ListPlayers</c> on one Running instance and merges the reply into the table.</summary>
    Task<CommandResult<OnlinePlayers>> ListOnlineAsync(int instanceId, CancellationToken cancellationToken = default);

    Task<CommandResult> DeleteAsync(int knownPlayerId, CancellationToken cancellationToken = default);
}
