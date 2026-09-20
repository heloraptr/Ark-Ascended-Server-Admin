using ArkAscendedServerAdmin.Processes;

namespace ArkAscendedServerAdmin.Players;

/// <summary>
/// Keeps the known players table current. Follows every instance console for join and leave lines
/// (live and backfilled), marks an instance's players offline when its process is gone, and merges
/// <c>ListPlayers</c> replies: the health probe's every 15 seconds and the owner's on demand.
/// </summary>
public interface IPlayerTracker
{
    /// <summary>Raised on a background thread after the table changed; pages reload their copy.</summary>
    event Action? Changed;

    /// <summary>
    /// Records a <c>ListPlayers</c> reply tagged with the session it came from: everyone listed is online on the
    /// instance as of <see cref="ProbeObservation.SentAt"/>; anyone the table thought was online there but who is
    /// missing from the reply is marked offline. A row whose newest evidence is at or after the sent-at time is
    /// left alone, so a slow reply never overwrites a join, a leave, or a later reply, on any instance. Returns
    /// false, touching nothing, when the observation's session is no longer the instance's live one.
    /// </summary>
    Task<bool> RecordListedAsync(ProbeObservation observation, CancellationToken cancellationToken);
}
