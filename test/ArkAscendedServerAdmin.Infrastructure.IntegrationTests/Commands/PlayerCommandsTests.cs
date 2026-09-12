using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class PlayerCommandsTests
{
    // The parser only accepts 32-hex EOS ids or 17-digit Steam ids (format unverified, HANDOVER §5).
    private const string EosA = "0002aaaa0002aaaa0002aaaa0002aaaa";
    private const string EosB = "0002bbbb0002bbbb0002bbbb0002bbbb";

    private static async Task<int> CreateAsync(CommandTestHost host, string name, int gamePort, int rconPort, CancellationToken ct)
    {
        var created = await host.Instances.CreateAsync(
            new InstanceDraft { Name = name, MapId = await host.MapIdAsync(ct), SessionName = name, GamePort = gamePort, RconPort = rconPort }, ct);
        Assert.True(created.Succeeded, created.Error);
        return created.Value;
    }

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.ListAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.RefreshAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.DeleteAsync(1, ct));
    }

    [Fact]
    public async Task Refresh_WithNothingRunning_ExplainsThatItNeedsALiveServer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var id = await CreateAsync(host, "Idle", 7777, 27020, ct);
        host.ProcessManager.Set(id, InstanceState.Starting);

        var result = await host.Players.RefreshAsync(ct);

        Assert.Equal("No instance is running. ListPlayers needs a live server to ask.", result.Error);
        Assert.Empty(host.Rcon.Calls);
    }

    [Fact]
    public async Task Refresh_AsksEveryRunningInstance_MergesPlayers_AndNotesTheOnesItCouldNotAsk()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        var bravo = await CreateAsync(host, "Bravo", 7779, 27021, ct);
        var charlie = await CreateAsync(host, "Charlie", 7781, 27022, ct);
        var stopped = await CreateAsync(host, "Delta", 7783, 27023, ct);
        foreach (var id in new[] { alpha, bravo, charlie })
        {
            host.ProcessManager.Set(id, InstanceState.Running);
        }

        host.ProcessManager.Set(stopped, InstanceState.Stopped);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        await host.WriteGeneratedSettingsAsync("charlie", "pw", 27022, ct);
        host.Rcon.Replies[RconCommands.ListPlayers] = $"0. Survivor One, {EosA.ToUpperInvariant()}\r\n1. Two, {EosB}\r\n";
        host.Rcon.FailuresByPort[27022] = new RconException(RconFailure.Timeout, "Timed out.");

        var result = await host.Players.RefreshAsync(ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Value!.PlayersSeen);
        Assert.Equal(
            [
                "Alpha: 2 players.",
                "Bravo: RCON credentials could not be read from the generated GameUserSettings.ini.",
                "Charlie: RCON timeout failure: Timed out.",
            ],
            result.Value.Notes);
        Assert.Equal([27020, 27022], host.Rcon.Calls.Select(c => c.Endpoint.Port));
        Assert.All(host.Rcon.Calls, c => Assert.Equal(RconCommands.ListPlayers, c.Command));

        var players = await host.Players.ListAsync(ct);
        Assert.Equal(["Survivor One", "Two"], players.Select(p => p.Name));
        Assert.All(players, p => Assert.Equal((CommandTestHost.Now, CommandTestHost.Now), (p.FirstSeenAt, p.LastSeenAt)));
        Assert.Equal(EosA.ToUpperInvariant(), players[0].EosId);
    }

    [Fact]
    public async Task Refresh_UpdatesKnownPlayersInPlace_KeepingTheirFirstSeenTime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        await using (var db = host.Db())
        {
            db.KnownPlayers.Add(new KnownPlayer { Name = "Old Name", EosId = EosA, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch });
            await db.SaveChangesAsync(ct);
        }

        host.Rcon.Replies[RconCommands.ListPlayers] = $"0. New Name, {EosA.ToUpperInvariant()}\r\n";

        var result = await host.Players.RefreshAsync(ct);

        Assert.Equal(1, result.Value!.PlayersSeen);
        var player = Assert.Single(await host.Players.ListAsync(ct));
        Assert.Equal(("New Name", EosA, DateTimeOffset.UnixEpoch, CommandTestHost.Now), (player.Name, player.EosId, player.FirstSeenAt, player.LastSeenAt));
    }

    [Fact]
    public async Task Refresh_WithNoPlayersConnected_NotesItAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        host.Rcon.Replies[RconCommands.ListPlayers] = RconCommands.NoPlayersReply;

        var result = await host.Players.RefreshAsync(ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value!.PlayersSeen);
        Assert.Equal(["Alpha: no players connected."], result.Value.Notes);
        Assert.Empty(await host.Players.ListAsync(ct));
    }

    [Fact]
    public async Task Delete_RemovesTheRow_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        int id;
        await using (var db = host.Db())
        {
            var row = new KnownPlayer { Name = "Gone", EosId = "0002gone", FirstSeenAt = CommandTestHost.Now, LastSeenAt = CommandTestHost.Now };
            db.KnownPlayers.Add(row);
            await db.SaveChangesAsync(ct);
            id = row.Id;
        }

        Assert.True((await host.Players.DeleteAsync(id, ct)).Succeeded);
        Assert.True((await host.Players.DeleteAsync(id, ct)).Succeeded);
        Assert.Empty(await host.Players.ListAsync(ct));
    }
}
