using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Commands;

/// <param name="PlayersSeen">Distinct players reported by the running instances.</param>
/// <param name="Notes">Per-instance results, including instances that could not be queried.</param>
public sealed record PlayerRefreshResult(int PlayersSeen, IReadOnlyList<string> Notes);

/// <summary>Scoped, guarded facade for the Known Players page: on-demand <c>ListPlayers</c> so the owner can find an EOS id.</summary>
public interface IPlayerCommands
{
    Task<IReadOnlyList<KnownPlayer>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <c>ListPlayers</c> on every instance that is Running and merges the result into the table.</summary>
    Task<CommandResult<PlayerRefreshResult>> RefreshAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> DeleteAsync(int knownPlayerId, CancellationToken cancellationToken = default);
}
