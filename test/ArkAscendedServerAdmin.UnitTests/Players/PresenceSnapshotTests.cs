using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.UnitTests.Players;

/// <summary>The evidence-ordering rule a <c>ListPlayers</c> reply is sorted by before the tracker writes anything.</summary>
public class PresenceSnapshotTests
{
    private const int Island = 1;
    private const int Center = 2;
    private const string Alice = "0002aaaa0002aaaa0002aaaa0002aaaa";
    private const string Bob = "0002bbbb0002bbbb0002bbbb0002bbbb";

    private static readonly DateTimeOffset _sentAt = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _older = _sentAt - TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset _newer = _sentAt + TimeSpan.FromMinutes(1);

    [Fact]
    public void AReplyOutranksOnlyEvidenceOlderThanTheMomentItWasSent()
    {
        Assert.True(PresenceSnapshot.Outranks(_sentAt, _older));
        Assert.False(PresenceSnapshot.Outranks(_sentAt, _sentAt));
        Assert.False(PresenceSnapshot.Outranks(_sentAt, _newer));
    }

    [Fact]
    public void AListedPlayerWithNoRow_IsANewcomer()
    {
        var changes = PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Alice", Alice)], []);

        Assert.Equal([Alice], changes.Newcomers.Select(p => p.EosId));
        Assert.Empty(changes.Online);
        Assert.Empty(changes.Offline);
        Assert.False(changes.IsEmpty);
    }

    [Fact]
    public void AListedPlayerWhoseRowIsOlder_GoesOnlineOnTheInstance()
    {
        var row = Row(Alice, "Alice", Island, online: false, _older);

        var changes = PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Alice the Second", Alice)], [row]);

        var (online, listed) = Assert.Single(changes.Online);
        Assert.Same(row, online);
        Assert.Equal("Alice the Second", listed.Name);
        Assert.Empty(changes.Newcomers);
    }

    [Fact]
    public void AListedPlayerWhoseRowIsNewer_IsLeftAlone()
    {
        // A leave line that arrived while the command was in flight says more than the reply does.
        var changes = PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Alice", Alice)], [Row(Alice, "Alice", Island, online: false, _newer)]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void ARowOnlineOnTheInstanceAndMissingFromTheReply_GoesOffline()
    {
        var changes = PresenceSnapshot.Decide(Island, _sentAt, [], [Row(Alice, "Alice", Island, online: true, _older)]);

        Assert.Equal([Alice], changes.Offline.Select(r => r.EosId));
    }

    [Fact]
    public void AMissingRowWithNewerEvidence_StaysOnline()
    {
        var changes = PresenceSnapshot.Decide(Island, _sentAt, [], [Row(Alice, "Alice", Island, online: true, _newer)]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void ARowOnAnotherInstance_IsOnlyTouchedWhenTheReplyListsItAndOutranksIt()
    {
        var elsewhere = Row(Bob, "Bob", Center, online: true, _older);

        Assert.True(PresenceSnapshot.Decide(Island, _sentAt, [], [elsewhere]).IsEmpty);
        Assert.Same(elsewhere, Assert.Single(PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Bob", Bob)], [elsewhere]).Online).Row);
    }

    [Fact]
    public void APlayerWhoTransferredAway_IsNotDraggedBackByADelayedReply()
    {
        // The reply was sent on the island before the player's join line on the center arrived.
        var transferred = Row(Alice, "Alice", Center, online: true, _newer);

        var changes = PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Alice", Alice)], [transferred]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void IdsCompareCaseInsensitively_AndARepeatedIdIsCountedOnce()
    {
        var row = Row(Alice.ToUpperInvariant(), "Alice", Island, online: false, _older);

        var changes = PresenceSnapshot.Decide(Island, _sentAt, [new ListedPlayer("Alice", Alice), new ListedPlayer("Alice again", Alice)], [row]);

        Assert.Same(row, Assert.Single(changes.Online).Row);
        Assert.Empty(changes.Newcomers);
    }

    private static KnownPlayer Row(string eosId, string name, int instanceId, bool online, DateTimeOffset lastSeenAt) =>
        new()
        {
            Name = name,
            EosId = eosId,
            FirstSeenAt = lastSeenAt,
            LastSeenAt = lastSeenAt,
            IsOnline = online,
            LastInstanceId = instanceId,
        };
}
