using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Consoles;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;
using ArkAscendedServerAdmin.Players;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Rcon;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Players;

/// <summary>The tracker over a real SQLite database, fed directly and through the fake console and process manager.</summary>
public class PlayerTrackerTests
{
    private const string Eos = "0002c0ffee11d00d4242beef00c0ffee";
    private const string Other = "0002aaaa0002aaaa0002aaaa0002aaaa";
    private const string Stranger = "0002bbbb0002bbbb0002bbbb0002bbbb";
    private const string JoinLine = "[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: Survivor42 [UniqueNetId:0002c0ffee11d00d4242beef00c0ffee Platform:None] joined this ARK!";
    private const string LeaveLine = "[2026.09.13-18.50.08:364][320]2026.09.13_18.50.08: Survivor42 [UniqueNetId:0002c0ffee11d00d4242beef00c0ffee Platform:None] left this ARK!";
    private static readonly DateTimeOffset JoinedAt = new(2026, 9, 13, 18, 48, 40, 782, TimeSpan.Zero);
    private static readonly DateTimeOffset LeftAt = new(2026, 9, 13, 18, 50, 8, 364, TimeSpan.Zero);

    /// <summary>When the replies in these tests went out: after both log lines above.</summary>
    private static readonly DateTimeOffset ListedAt = new(2026, 9, 13, 19, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static PlayerLogEvent Join => PlayerLogLines.TryParse(JoinLine)!;

    private static PlayerLogEvent Leave => PlayerLogLines.TryParse(LeaveLine)!;

    private static async Task<int> CreateAsync(CommandTestHost host, string name, int gamePort, int rconPort, CancellationToken ct)
    {
        var created = await host.Instances.CreateAsync(
            new InstanceDraft { Name = name, MapId = await host.MapIdAsync(ct), SessionName = name, GamePort = gamePort, RconPort = rconPort }, ct);
        Assert.True(created.Succeeded, created.Error);
        return created.Value;
    }

    private static async Task<List<KnownPlayer>> RowsAsync(CommandTestHost host, CancellationToken ct)
    {
        await using var db = host.Db();
        return await db.KnownPlayers.AsNoTracking().Include(p => p.LastInstance).OrderBy(p => p.Name).ToListAsync(ct);
    }

    private static async Task<KnownPlayer> RowAsync(CommandTestHost host, CancellationToken ct) => Assert.Single(await RowsAsync(host, ct));

    /// <summary>A reply in the shape <c>ListPlayers</c> answers with, or its "no players" text when nobody is on.</summary>
    private static string Reply((string Name, string EosId)[] players) =>
        players.Length == 0
            ? RconCommands.NoPlayersReply
            : string.Concat(players.Select((p, i) => $"{i}. {p.Name}, {p.EosId}\r\n"));

    /// <summary>An observation carrying the instance's current session identity, as the probe and the facade both build one.</summary>
    private static ProbeObservation Observation(CommandTestHost host, int instanceId, DateTimeOffset sentAt, params (string Name, string EosId)[] players)
    {
        var runtime = host.ProcessManager.GetRuntime(instanceId);
        return new ProbeObservation(instanceId, runtime.Pid!.Value, runtime.ProcessStartTime!.Value, sentAt, Reply(players));
    }

    /// <summary>The console feed is queued, so a test waits for the table to reach the expected shape.</summary>
    private static async Task WaitUntilAsync(CommandTestHost host, Func<List<KnownPlayer>, bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition(await RowsAsync(host, ct)))
            {
                return;
            }

            await Task.Delay(25, ct);
        }

        Assert.Fail("The tracker did not apply the queued work in time.");
    }

    [Fact]
    public async Task Apply_JoinLine_RecordsThePlayerOnlineOnTheInstance_WithTheLogsOwnTime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        var changes = 0;
        host.Tracker.Changed += () => changes++;

        Assert.True(await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct));

        var row = await RowAsync(host, ct);
        Assert.Equal(("Survivor42", Eos, "None"), (row.Name, row.EosId, row.Platform));
        Assert.True(row.IsOnline);
        Assert.Equal("Island", row.LastInstance?.Name);
        Assert.Equal((JoinedAt, JoinedAt, JoinedAt), (row.FirstSeenAt, row.LastSeenAt, row.LastJoinedAt));
        Assert.Null(row.LastLeftAt);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Apply_LeaveLine_MarksOffline_AndKeepsTheFirstSeenTime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);

        Assert.True(await host.Tracker.ApplyAsync(island, Leave, CommandTestHost.Now, ct));

        var row = await RowAsync(host, ct);
        Assert.False(row.IsOnline);
        Assert.Equal((JoinedAt, LeftAt, JoinedAt, LeftAt), (row.FirstSeenAt, row.LastSeenAt, row.LastJoinedAt, row.LastLeftAt));
    }

    [Fact]
    public async Task Apply_LeaveForAPlayerNeverSeen_CreatesAnOfflineRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);

        await host.Tracker.ApplyAsync(island, Leave, CommandTestHost.Now, ct);

        var row = await RowAsync(host, ct);
        Assert.False(row.IsOnline);
        Assert.Equal((LeftAt, LeftAt, null, LeftAt), (row.FirstSeenAt, row.LastSeenAt, row.LastJoinedAt, row.LastLeftAt));
    }

    [Fact]
    public async Task Apply_IgnoresAnEventOlderThanWhatTheRowAlreadyKnows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        await host.Tracker.ApplyAsync(island, Leave, CommandTestHost.Now, ct);

        // A backfill replayed after a restart carries the join that preceded the leave already recorded.
        Assert.False(await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct));

        var row = await RowAsync(host, ct);
        Assert.False(row.IsOnline);
        Assert.Equal(LeftAt, row.LastSeenAt);
    }

    [Fact]
    public async Task Apply_WithoutAStamp_UsesTheObservedTime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        var unstamped = PlayerLogLines.TryParse($"Survivor42 [UniqueNetId:{Eos} Platform:None] joined this ARK!")!;

        await host.Tracker.ApplyAsync(island, unstamped, CommandTestHost.Now, ct);

        Assert.Equal(CommandTestHost.Now, (await RowAsync(host, ct)).LastJoinedAt);
    }

    [Fact]
    public async Task RecordListed_MarksListedOnline_AddsNewcomers_AndMarksTheMissingOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        var center = await CreateAsync(host, "Center", 7779, 27021, ct);
        host.ProcessManager.Set(island, InstanceState.Running);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);
        await host.Tracker.ApplyAsync(center, PlayerLogLines.TryParse($"Elsewhere [UniqueNetId:{Other} Platform:None] joined this ARK!")!, CommandTestHost.Now, ct);

        Assert.True(await host.Tracker.RecordListedAsync(Observation(host, island, ListedAt, ("Newcomer", Stranger)), ct));

        var rows = await RowsAsync(host, ct);
        Assert.Equal(["Elsewhere", "Newcomer", "Survivor42"], rows.Select(r => r.Name));
        Assert.True(rows[0].IsOnline, "a player on another instance is untouched");
        Assert.True(rows[1].IsOnline);
        Assert.Equal(("Island", ListedAt, ListedAt), (rows[1].LastInstance?.Name, rows[1].FirstSeenAt, rows[1].LastSeenAt));
        Assert.False(rows[2].IsOnline, "listed on the island no more");
    }

    [Fact]
    public async Task RecordListed_DropsAReplyThatIsNoLongerTheInstancesLiveSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        host.ProcessManager.Set(island, InstanceState.Running);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);
        var inFlight = Observation(host, island, ListedAt);

        // The server was restarted while the command was out, so the reply describes a process that has gone.
        host.ProcessManager.Set(island, InstanceState.Running, FakeProcessManager.DefaultStartTime.AddMinutes(5));
        Assert.False(await host.Tracker.RecordListedAsync(inFlight, ct));

        // And nothing at all is live after a stop.
        host.ProcessManager.Set(island, InstanceState.Stopped);
        Assert.False(await host.Tracker.RecordListedAsync(inFlight, ct));

        var row = await RowAsync(host, ct);
        Assert.True(row.IsOnline, "a dropped reply changes nothing");
        Assert.Equal(JoinedAt, row.LastSeenAt);
    }

    [Fact]
    public async Task RecordListed_LeavesRowsWhoseEvidenceIsNewerThanTheReply()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        host.ProcessManager.Set(island, InstanceState.Running);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);

        // Sent a minute before the join line was written, answered after the line had been applied.
        Assert.True(await host.Tracker.RecordListedAsync(Observation(host, island, JoinedAt.AddMinutes(-1)), ct));

        var row = await RowAsync(host, ct);
        Assert.True(row.IsOnline, "a delayed empty reply does not un-join a player");
        Assert.Equal(JoinedAt, row.LastSeenAt);
    }

    [Fact]
    public async Task RecordListed_FollowsATransfer_AndADelayedReplyDoesNotDragThePlayerBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        var center = await CreateAsync(host, "Center", 7779, 27021, ct);
        host.ProcessManager.Set(island, InstanceState.Running);
        host.ProcessManager.Set(center, InstanceState.Running);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);

        // The center lists them after the island join, so the player moves across.
        Assert.True(await host.Tracker.RecordListedAsync(Observation(host, center, ListedAt, ("Survivor42", Eos)), ct));

        var moved = await RowAsync(host, ct);
        Assert.Equal(("Center", ListedAt, true), (moved.LastInstance?.Name, moved.LastSeenAt, moved.IsOnline));

        // An island reply sent before the transfer may neither claim the player back nor mark them offline.
        Assert.True(await host.Tracker.RecordListedAsync(Observation(host, island, ListedAt.AddMinutes(-1), ("Survivor42", Eos)), ct));

        var after = await RowAsync(host, ct);
        Assert.Equal(("Center", ListedAt, true), (after.LastInstance?.Name, after.LastSeenAt, after.IsOnline));
    }

    [Fact]
    public async Task Started_AppliesTheHealthProbesReplies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        host.ProcessManager.Set(island, InstanceState.Running);
        await host.Tracker.StartAsync(ct);
        try
        {
            host.ProcessManager.Observe(Observation(host, island, ListedAt, ("Survivor42", Eos)));
            await WaitUntilAsync(host, rows => rows.Count == 1 && rows[0].IsOnline, ct);
        }
        finally
        {
            await host.Tracker.StopAsync(ct);
        }

        var row = await RowAsync(host, ct);
        Assert.Equal(("Survivor42", Eos, "Island", ListedAt), (row.Name, row.EosId, row.LastInstance?.Name, row.LastSeenAt));
    }

    [Fact]
    public async Task MarkOffline_ClearsOnlyThatInstancesPlayers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        var center = await CreateAsync(host, "Center", 7779, 27021, ct);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);
        await host.Tracker.ApplyAsync(center, PlayerLogLines.TryParse($"Elsewhere [UniqueNetId:{Other} Platform:None] joined this ARK!")!, CommandTestHost.Now, ct);

        Assert.Equal(1, await host.Tracker.MarkOfflineAsync(island, ct));
        Assert.Equal(0, await host.Tracker.MarkOfflineAsync(island, ct));

        var rows = await RowsAsync(host, ct);
        Assert.Equal([true, false], rows.Select(r => r.IsOnline));
        Assert.Equal(JoinedAt, rows[1].LastSeenAt);
        Assert.Null(rows[1].LastLeftAt);
    }

    [Fact]
    public async Task Started_FollowsInstanceConsoles_AndRuntimeChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        await host.Tracker.StartAsync(ct);
        try
        {
            // Neither the SteamCMD channel nor a manager note counts, whatever their text says.
            host.Console.Append(ConsoleChannels.SteamCmd, new ConsoleLine(CommandTestHost.Now, JoinLine));
            host.Console.Append(ConsoleChannels.Instance(island), new ConsoleLine(CommandTestHost.Now, JoinLine, ConsoleLineKind.Info));
            host.Console.Append(ConsoleChannels.Instance(island), new ConsoleLine(CommandTestHost.Now, JoinLine, ConsoleLineKind.Backfill));
            await WaitUntilAsync(host, rows => rows.Count == 1 && rows[0].IsOnline, ct);

            host.ProcessManager.Set(island, InstanceState.Stopped);
            await WaitUntilAsync(host, rows => rows.Count == 1 && !rows[0].IsOnline, ct);
        }
        finally
        {
            await host.Tracker.StopAsync(ct);
        }

        Assert.Equal("Island", (await RowAsync(host, ct)).LastInstance?.Name);
    }

    [Fact]
    public async Task DeletingTheInstanceRow_ClearsTheReference()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        var island = await CreateAsync(host, "Island", 7777, 27020, ct);
        await host.Tracker.ApplyAsync(island, Join, CommandTestHost.Now, ct);

        await using (var db = host.Db())
        {
            // The delete service removes the dependents first (and clears LastInstanceId itself); this checks the schema's own ON DELETE SET NULL.
            await db.IniDocuments.Where(d => d.InstanceId == island).ExecuteDeleteAsync(ct);
            await db.Instances.Where(i => i.Id == island).ExecuteDeleteAsync(ct);
        }

        var row = await RowAsync(host, ct);
        Assert.Null(row.LastInstanceId);
    }
}
