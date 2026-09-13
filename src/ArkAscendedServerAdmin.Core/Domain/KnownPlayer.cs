namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// A player the manager has seen. Rows are written from the <c>joined this ARK!</c> / <c>left this ARK!</c>
/// lines of <c>ShooterGame.log</c> as they happen, and from <c>ListPlayers</c> when the owner asks an instance
/// who is on. Exists so the owner can find an EOS id for a whitelist and see who is online where.
/// </summary>
public sealed class KnownPlayer
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string EosId { get; set; }

    /// <summary>The <c>Platform:</c> token of the join line (<c>None</c> for an EOS login); null until a join line is seen.</summary>
    public string? Platform { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The newest evidence of the player: a join, a leave, or a <c>ListPlayers</c> reply.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? LastJoinedAt { get; set; }

    public DateTimeOffset? LastLeftAt { get; set; }

    /// <summary>True from a join line (or a <c>ListPlayers</c> reply) until a leave line, a later <c>ListPlayers</c> without them, or the instance goes down.</summary>
    public bool IsOnline { get; set; }

    /// <summary>The instance the player was last seen on; cleared when that instance is deleted.</summary>
    public int? LastInstanceId { get; set; }

    public Instance? LastInstance { get; set; }
}
