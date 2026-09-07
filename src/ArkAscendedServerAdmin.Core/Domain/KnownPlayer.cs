namespace ArkAscendedServerAdmin.Domain;

/// <summary>A player seen via <c>ListPlayers</c>; exists so the owner can find their own EOS id.</summary>
public sealed class KnownPlayer
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string EosId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
