using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Rcon;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

public class PlayerCommandsTests
{
    private const string EosA = "0002aaaa0002aaaa0002aaaa0002aaaa";
    private const string EosB = "0002bbbb0002bbbb0002bbbb0002bbbb";

    private static async Task<int> CreateAsync(CommandTestHost host, string name, int gamePort, int rconPort, CancellationToken ct)
    {
        var created = await host.Instances.CreateAsync(
            new InstanceDraft { Name = name, MapId = await host.MapIdAsync(ct), SessionName = name, GamePort = gamePort, RconPort = rconPort }, ct);
        Assert.True(created.Succeeded, created.Error);
        return created.Value;
    }

    private static async Task<int> SeedAsync(CommandTestHost host, string name, string eosId, int? lastInstanceId, bool online, CancellationToken ct)
    {
        await using var db = host.Db();
        var row = new KnownPlayer
        {
            Name = name,
            EosId = eosId,
            FirstSeenAt = DateTimeOffset.UnixEpoch,
            LastSeenAt = DateTimeOffset.UnixEpoch,
            LastInstanceId = lastInstanceId,
            IsOnline = online,
        };
        db.KnownPlayers.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    [Fact]
    public async Task EveryMethod_RefusesWhenTheGuardDenies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        host.Guard.Deny = true;

        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.ListAsync(ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.ListOnlineAsync(1, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.KickPlayerAsync(1, EosA, ct));
        await Assert.ThrowsAsync<NotAuthorizedException>(() => host.Players.DeleteAsync(1, ct));
        Assert.Empty(host.Rcon.Calls);
    }

    [Fact]
    public async Task Kick_SendsKickPlayerWithTheId_AndReturnsTheReply()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        host.Rcon.Replies[$"KickPlayer {EosA}"] = "Kicked";

        var result = await host.Players.KickPlayerAsync(alpha, $"  {EosA} ", ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Kicked", result.Value);
        var call = Assert.Single(host.Rcon.Calls);
        Assert.Equal((27020, $"KickPlayer {EosA}"), (call.Endpoint.Port, call.Command));
    }

    [Fact]
    public async Task Kick_WithoutAnId_OrOnAStoppedInstance_SaysSoWithoutAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);

        var blank = await host.Players.KickPlayerAsync(alpha, "   ", ct);
        var stopped = await host.Players.KickPlayerAsync(alpha, EosA, ct);

        Assert.Equal("The player has no id to kick by.", blank.Error);
        Assert.Equal("The instance is not running, so there is nothing to send the command to.", stopped.Error);
        Assert.Empty(host.Rcon.Calls);
    }

    [Fact]
    public async Task Kick_WhenRconFails_ReportsTheFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        host.Rcon.FailuresByPort[27020] = new RconException(RconFailure.Timeout, "Timed out.");

        var result = await host.Players.KickPlayerAsync(alpha, EosA, ct);

        Assert.False(result.Succeeded);
        Assert.Equal("RCON timeout failure: Timed out.", result.Error);
    }

    [Fact]
    public async Task ListOnline_WhenTheInstanceIsNotRunning_SaysSoWithoutAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var id = await CreateAsync(host, "Idle", 7777, 27020, ct);
        host.ProcessManager.Set(id, InstanceState.Starting);

        var result = await host.Players.ListOnlineAsync(id, ct);

        Assert.Equal("The instance is not running, so there is no server to ask.", result.Error);
        Assert.Empty(host.Rcon.Calls);
    }

    [Fact]
    public async Task ListOnline_AsksThatInstanceOnly_RecordsThePlayers_AndReportsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        var bravo = await CreateAsync(host, "Bravo", 7779, 27021, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        host.ProcessManager.Set(bravo, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        await host.WriteGeneratedSettingsAsync("bravo", "pw", 27021, ct);
        await SeedAsync(host, "Old Name", EosA, null, false, ct);
        host.Rcon.Replies[RconCommands.ListPlayers] = $"0. New Name, {EosA.ToUpperInvariant()}\r\n1. Two, {EosB}\r\n";

        var result = await host.Players.ListOnlineAsync(alpha, ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["New Name", "Two"], result.Value!.Players.Select(p => p.Name));
        Assert.Equal(CommandTestHost.Now, result.Value.AsOf);
        var call = Assert.Single(host.Rcon.Calls);
        Assert.Equal((27020, RconCommands.ListPlayers), (call.Endpoint.Port, call.Command));

        var players = await host.Players.ListAsync(ct);
        Assert.Equal(["New Name", "Two"], players.Select(p => p.Name));
        Assert.All(players, p => Assert.True(p.IsOnline));
        Assert.All(players, p => Assert.Equal("Alpha", p.LastInstance?.Name));
        Assert.Equal((EosA, DateTimeOffset.UnixEpoch, CommandTestHost.Now), (players[0].EosId, players[0].FirstSeenAt, players[0].LastSeenAt));
        Assert.Equal((CommandTestHost.Now, CommandTestHost.Now), (players[1].FirstSeenAt, players[1].LastSeenAt));
    }

    [Fact]
    public async Task ListOnline_WithNoPlayersConnected_ReturnsNobody_AndMarksTheInstancesPlayersOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        await SeedAsync(host, "Gone", EosA, alpha, true, ct);
        host.Rcon.Replies[RconCommands.ListPlayers] = RconCommands.NoPlayersReply;

        var result = await host.Players.ListOnlineAsync(alpha, ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Value!.Players);
        var player = Assert.Single(await host.Players.ListAsync(ct));
        Assert.False(player.IsOnline);
    }

    [Fact]
    public async Task ListOnline_WithoutCredentials_Explains()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);

        var result = await host.Players.ListOnlineAsync(alpha, ct);

        Assert.Equal("RCON credentials could not be read from the generated GameUserSettings.ini.", result.Error);
        Assert.Empty(host.Rcon.Calls);
    }

    [Fact]
    public async Task ListOnline_WhenRconFails_ReportsTheFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        host.ProcessManager.Set(alpha, InstanceState.Running);
        await host.WriteGeneratedSettingsAsync("alpha", "pw", 27020, ct);
        host.Rcon.FailuresByPort[27020] = new RconException(RconFailure.Timeout, "Timed out.");

        var result = await host.Players.ListOnlineAsync(alpha, ct);

        Assert.Equal("RCON timeout failure: Timed out.", result.Error);
        Assert.Empty(await host.Players.ListAsync(ct));
    }

    [Fact]
    public async Task List_OrdersByName_AndLoadsTheLastInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var alpha = await CreateAsync(host, "Alpha", 7777, 27020, ct);
        await SeedAsync(host, "Zed", EosA, alpha, true, ct);
        await SeedAsync(host, "Amy", EosB, null, false, ct);

        var players = await host.Players.ListAsync(ct);

        Assert.Equal(["Amy", "Zed"], players.Select(p => p.Name));
        Assert.Null(players[0].LastInstance);
        Assert.Equal("Alpha", players[1].LastInstance?.Name);
    }

    [Fact]
    public async Task Delete_RemovesTheRow_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var id = await SeedAsync(host, "Gone", "0002gone", null, false, ct);

        Assert.True((await host.Players.DeleteAsync(id, ct)).Succeeded);
        Assert.True((await host.Players.DeleteAsync(id, ct)).Succeeded);
        Assert.Empty(await host.Players.ListAsync(ct));
    }
}
