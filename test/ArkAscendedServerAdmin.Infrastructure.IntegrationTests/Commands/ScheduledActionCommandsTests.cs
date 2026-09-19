using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Commands;

/// <summary>
/// B3: the schedule editor's list, whole-list save, and runs list on both facades, the
/// <see cref="Instance.OverridesClusterSchedule"/> round trip, and the dashboard's next deadline.
/// </summary>
public class ScheduledActionCommandsTests
{
    private static async Task<(CommandTestHost Host, int MapId)> StartAsync(CancellationToken ct)
    {
        var host = new CommandTestHost();
        await host.InitializeAsync(ct);
        return (host, await host.MapIdAsync(ct));
    }

    private static InstanceDraft Draft(int mapId, string name, int gamePort, int rconPort, int? clusterId = null) =>
        new() { Name = name, MapId = mapId, ClusterId = clusterId, SessionName = $"{name} session", GamePort = gamePort, RconPort = rconPort };

    private static ScheduledActionEdit Restart(string cron, int id = 0, int warning = 10, bool enabled = true) =>
        new(id, cron, ScheduledActionKind.Restart, string.Empty, warning, enabled);

    private static ScheduledActionEdit Rcon(string cron, string command, int id = 0) =>
        new(id, cron, ScheduledActionKind.RconCommand, command, 10, true);

    private static InstanceEdit Edit(Instance instance, bool overridesClusterSchedule) =>
        new(instance.Name, instance.SessionName, instance.MaxPlayers, instance.GamePort, instance.RconPort, instance.AdminWhitelist, instance.BackupIntervalMinutes, instance.BackupRetention, overridesClusterSchedule);

    /// <summary>A cluster with two members (Alpha, Beta) and a standalone instance (Solo).</summary>
    private static async Task<(int Cluster, int Alpha, int Beta, int Solo)> SeedAsync(CommandTestHost host, int mapId, CancellationToken ct)
    {
        var cluster = await host.Clusters.CreateAsync("Cluster", ConfigSourceKind.Blank, null, ct);
        Assert.True(cluster.Succeeded, cluster.Error);
        var alpha = await host.Instances.CreateAsync(Draft(mapId, "Alpha", 7777, 27020, cluster.Value), ct);
        var beta = await host.Instances.CreateAsync(Draft(mapId, "Beta", 7787, 27030, cluster.Value), ct);
        var solo = await host.Instances.CreateAsync(Draft(mapId, "Solo", 7797, 27040), ct);
        Assert.True(alpha.Succeeded && beta.Succeeded && solo.Succeeded, string.Join(" ", alpha.Error, beta.Error, solo.Error));
        return (cluster.Value, alpha.Value, beta.Value, solo.Value);
    }

    private static async Task<IReadOnlyList<ScheduledActionView>> OwnRowsAsync(CommandTestHost host, int instanceId, CancellationToken ct)
    {
        var list = await host.Instances.ListScheduledActionsAsync(instanceId, ct);
        Assert.True(list.Succeeded, list.Error);
        return list.Value!.Where(r => !r.Inherited).ToList();
    }

    private static async Task<int> AddRunAsync(CommandTestHost host, int actionId, int instanceId, DateTimeOffset scheduledFor, DateTimeOffset startedAt, ScheduledActionOutcome outcome, string reason, CancellationToken ct)
    {
        await using var db = host.Db();
        var run = new ScheduledActionRun
        {
            ScheduledActionId = actionId,
            InstanceId = instanceId,
            ScheduledFor = scheduledFor,
            StartedAt = startedAt,
            CompletedAt = outcome == ScheduledActionOutcome.Started ? null : startedAt.AddSeconds(30),
            Outcome = outcome,
            Reason = reason,
        };
        db.ScheduledActionRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    // ---- save ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_InsertsNewRows_UpdatesKnownRowsInPlace_AndDeletesTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (_, _, _, solo) = await SeedAsync(host, mapId, ct);

            var first = await host.Instances.SaveScheduledActionsAsync(solo, [Restart("  0 4 * * *  "), Rcon("0 5 * * 1-5", "  saveworld  ")], ct);

            Assert.True(first.Succeeded, first.Error);
            var listed = await OwnRowsAsync(host, solo, ct);
            Assert.Equal(2, listed.Count);
            Assert.All(listed, r => Assert.Equal((solo, (int?)null), (r.OwnerInstanceId, r.OwnerClusterId)));
            Assert.Equal(("0 4 * * *", "At 04:00", ScheduledActionKind.Restart, string.Empty, 10, true), (listed[0].Cron, listed[0].Description, listed[0].Kind, listed[0].Command, listed[0].WarningMinutes, listed[0].Enabled));
            Assert.Equal(("0 5 * * 1-5", "At 05:00, Monday through Friday", ScheduledActionKind.RconCommand, "saveworld"), (listed[1].Cron, listed[1].Description, listed[1].Kind, listed[1].Command));
            Assert.All(listed, r => Assert.True(r.NextDeadline > CommandTestHost.Now, $"{r.NextDeadline:O} is not after now"));
            var restartId = listed[0].Id;
            var rconId = listed[1].Id;
            var runId = await AddRunAsync(host, restartId, solo, CommandTestHost.Now.AddDays(-1), CommandTestHost.Now.AddDays(-1), ScheduledActionOutcome.Succeeded, string.Empty, ct);

            // Edit the restart, drop the RCON row, and add a dino wipe whose stray command text is cleared.
            var second = await host.Instances.SaveScheduledActionsAsync(
                solo,
                [Restart("30 4 * * *", restartId, warning: 5, enabled: false), new ScheduledActionEdit(0, "0 6 * * *", ScheduledActionKind.DinoWipe, "ignored", 15, true)],
                ct);

            Assert.True(second.Succeeded, second.Error);
            await using var db = host.Db();
            var rows = await db.ScheduledActions.Where(a => a.InstanceId == solo).OrderBy(a => a.Id).ToListAsync(ct);
            Assert.Equal(2, rows.Count);
            Assert.Equal(restartId, rows[0].Id);
            Assert.Equal(("30 4 * * *", 5, false), (rows[0].Cron, rows[0].WarningMinutes, rows[0].Enabled));
            Assert.True(rows[1].Id > rconId);
            Assert.Equal(("0 6 * * *", ScheduledActionKind.DinoWipe, string.Empty, 15), (rows[1].Cron, rows[1].Kind, rows[1].Command, rows[1].WarningMinutes));
            Assert.DoesNotContain(rows, r => r.Id == rconId);
            Assert.Equal(restartId, (await db.ScheduledActionRuns.SingleAsync(r => r.Id == runId, ct)).ScheduledActionId);
        }
    }

    [Fact]
    public async Task Save_ReportsEveryFieldProblem_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (_, _, _, solo) = await SeedAsync(host, mapId, ct);
            var tooLong = string.Join(',', Enumerable.Range(0, 50)) + " * * * *"; // valid to Cronos, but past the column
            ScheduledActionEdit[] rows =
            [
                new(0, "bogus", ScheduledActionKind.Restart, string.Empty, 10, true),
                new(0, "0 0 30 2 *", ScheduledActionKind.Restart, string.Empty, 61, true),
                new(0, "*/5 * * * *", ScheduledActionKind.RconCommand, "   ", -1, true),
                new(0, "*/5 * * * *", ScheduledActionKind.RconCommand, new string('x', 513), 0, true),
                new(0, "0 3 * * *", (ScheduledActionKind)42, string.Empty, 0, true),
                new(0, "*/30 * * * *", ScheduledActionKind.Restart, string.Empty, 30, true),
                new(0, tooLong, ScheduledActionKind.Restart, string.Empty, 10, true),
                new(0, "*/30 * * * *", ScheduledActionKind.Restart, string.Empty, 29, true),
                new(0, "*/5 * * * *", ScheduledActionKind.RconCommand, new string('y', 512), 0, true),
            ];

            var result = await host.Instances.SaveScheduledActionsAsync(solo, rows, ct);

            Assert.False(result.Succeeded);
            Assert.Equal(
                [
                    "Row 1: the schedule is not a valid cron expression.",
                    "Row 2: the schedule never runs.",
                    "Row 2: the warning must be between 0 and 60 minutes.",
                    "Row 3: the warning must be between 0 and 60 minutes.",
                    "Row 3: an RCON command is required.",
                    "Row 4: the RCON command must be 512 characters or fewer.",
                    "Row 5: the action kind is not recognized.",
                    "Row 6: the schedule repeats faster than its warning countdown.",
                    "Row 7: the schedule must be 128 characters or fewer.",
                ],
                result.Errors);
            await using var db = host.Db();
            Assert.Empty(await db.ScheduledActions.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task Save_RefusesRepeatedIds_AnotherOwnersIds_UnknownIds_AndInheritedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (cluster, alpha, beta, _) = await SeedAsync(host, mapId, ct);
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart("0 2 * * *")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(beta, [Restart("0 3 * * *")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(alpha, [Restart("20 3 * * *")], ct)).Succeeded);
            var clusterRow = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!.Single().Id;
            var betaRow = (await OwnRowsAsync(host, beta, ct)).Single().Id;
            var alphaRow = (await OwnRowsAsync(host, alpha, ct)).Single().Id;

            var onAlpha = await host.Instances.SaveScheduledActionsAsync(
                alpha,
                [Restart("20 3 * * *", alphaRow), Restart("20 3 * * *", alphaRow), Restart("1 0 * * *", betaRow), Restart("1 0 * * *", 999), Restart("1 0 * * *", clusterRow)],
                ct);
            var onCluster = await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart("0 2 * * *", clusterRow), Restart("1 0 * * *", alphaRow), Restart("1 0 * * *", 999)], ct);

            Assert.Equal(
                [
                    $"Row 2 repeats scheduled action #{alphaRow}.",
                    $"Row 3: scheduled action #{betaRow} does not belong to this instance.",
                    "Row 4: scheduled action #999 does not belong to this instance.",
                    $"Row 5: scheduled action #{clusterRow} comes from the cluster; change it on the cluster page.",
                ],
                onAlpha.Errors);
            Assert.Equal(
                [
                    $"Row 2: scheduled action #{alphaRow} does not belong to this cluster.",
                    "Row 3: scheduled action #999 does not belong to this cluster.",
                ],
                onCluster.Errors);
            await using var db = host.Db();
            Assert.Equal("0 2 * * *", (await db.ScheduledActions.SingleAsync(a => a.Id == clusterRow, ct)).Cron);
            Assert.Equal("0 3 * * *", (await db.ScheduledActions.SingleAsync(a => a.Id == betaRow, ct)).Cron);
            Assert.Equal("20 3 * * *", (await db.ScheduledActions.SingleAsync(a => a.Id == alphaRow, ct)).Cron);
        }
    }

    // ---- list and inheritance -----------------------------------------------------------------------

    [Fact]
    public async Task List_ShowsInheritedClusterRowsFlagged_UntilTheInstanceOverridesTheClusterSchedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (cluster, alpha, beta, solo) = await SeedAsync(host, mapId, ct);
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart("0 2 * * *"), Rcon("10 2 * * *", "saveworld")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(alpha, [Restart("20 3 * * *")], ct)).Succeeded);

            var clusterList = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!;
            var alphaList = (await host.Instances.ListScheduledActionsAsync(alpha, ct)).Value!;
            var betaList = (await host.Instances.ListScheduledActionsAsync(beta, ct)).Value!;
            var soloList = (await host.Instances.ListScheduledActionsAsync(solo, ct)).Value!;

            Assert.Equal(2, clusterList.Count);
            Assert.All(clusterList, r => Assert.Equal(((int?)null, cluster, false), (r.OwnerInstanceId, r.OwnerClusterId, r.Inherited)));
            Assert.Equal([true, true, false], alphaList.Select(r => r.Inherited));
            Assert.Equal(["0 2 * * *", "10 2 * * *", "20 3 * * *"], alphaList.Select(r => r.Cron));
            Assert.Equal(["At 02:00", "At 02:10", "At 03:20"], alphaList.Select(r => r.Description));
            Assert.All(alphaList, r => Assert.NotNull(r.NextDeadline));
            Assert.Equal((cluster, (int?)null), (alphaList[0].OwnerClusterId, alphaList[0].OwnerInstanceId));
            Assert.Equal(((int?)null, alpha), (alphaList[2].OwnerClusterId, alphaList[2].OwnerInstanceId));
            Assert.Equal([true, true], betaList.Select(r => r.Inherited));
            Assert.Empty(soloList);

            var overriding = await host.Instances.SaveAsync(alpha, Edit(await host.InstanceAsync(alpha, ct), true), ct);

            Assert.True(overriding.Succeeded, overriding.Error);
            Assert.True((await host.InstanceAsync(alpha, ct)).OverridesClusterSchedule);
            var ownOnly = (await host.Instances.ListScheduledActionsAsync(alpha, ct)).Value!;
            Assert.Equal(["20 3 * * *"], ownOnly.Select(r => r.Cron));
            Assert.All(ownOnly, r => Assert.False(r.Inherited));

            var inheriting = await host.Instances.SaveAsync(alpha, Edit(await host.InstanceAsync(alpha, ct), false), ct);

            Assert.True(inheriting.Succeeded, inheriting.Error);
            Assert.False((await host.InstanceAsync(alpha, ct)).OverridesClusterSchedule);
            Assert.Equal(3, (await host.Instances.ListScheduledActionsAsync(alpha, ct)).Value!.Count);
        }
    }

    // ---- runs ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_ListNewestFirst_HonorTake_AndSpanTheClusterMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (cluster, alpha, beta, solo) = await SeedAsync(host, mapId, ct);
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart("0 2 * * *")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(solo, [Rcon("0 1 * * *", "saveworld")], ct)).Succeeded);
            var clusterRow = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!.Single().Id;
            var soloRow = (await OwnRowsAsync(host, solo, ct)).Single().Id;
            var now = CommandTestHost.Now;
            await AddRunAsync(host, clusterRow, alpha, now.AddDays(-2).AddMinutes(10), now.AddDays(-2), ScheduledActionOutcome.Succeeded, string.Empty, ct);
            await AddRunAsync(host, clusterRow, beta, now.AddDays(-2).AddMinutes(10), now.AddDays(-2).AddMinutes(1), ScheduledActionOutcome.Failed, "boom", ct);
            await AddRunAsync(host, clusterRow, alpha, now.AddDays(-1).AddMinutes(10), now.AddDays(-1), ScheduledActionOutcome.Skipped, "The server is not running.", ct);
            await AddRunAsync(host, soloRow, solo, now, now, ScheduledActionOutcome.Started, string.Empty, ct);

            var alphaRuns = (await host.Instances.ListScheduledActionRunsAsync(alpha, 10, ct)).Value!;
            var alphaLatest = (await host.Instances.ListScheduledActionRunsAsync(alpha, 1, ct)).Value!;
            var clusterRuns = (await host.Clusters.ListScheduledActionRunsAsync(cluster, 10, ct)).Value!;
            var clusterLatestTwo = (await host.Clusters.ListScheduledActionRunsAsync(cluster, 2, ct)).Value!;
            var soloRuns = (await host.Instances.ListScheduledActionRunsAsync(solo, 10, ct)).Value!;

            Assert.Equal([now.AddDays(-1).AddMinutes(10), now.AddDays(-2).AddMinutes(10)], alphaRuns.Select(r => r.ScheduledFor));
            Assert.Equal([ScheduledActionOutcome.Skipped, ScheduledActionOutcome.Succeeded], alphaRuns.Select(r => r.Outcome));
            Assert.All(alphaRuns, r => Assert.Equal((clusterRow, alpha, "Alpha", ScheduledActionKind.Restart), (r.ScheduledActionId, r.InstanceId, r.InstanceName, r.Kind)));
            Assert.Equal("The server is not running.", alphaRuns[0].Reason);
            Assert.Equal(now.AddDays(-1), alphaRuns[0].StartedAt);
            Assert.Equal(now.AddDays(-1).AddSeconds(30), alphaRuns[0].CompletedAt);
            Assert.Equal([alphaRuns[0].Id], alphaLatest.Select(r => r.Id));

            Assert.Equal(["Alpha", "Beta", "Alpha"], clusterRuns.Select(r => r.InstanceName));
            Assert.Equal([now.AddDays(-1), now.AddDays(-2).AddMinutes(1), now.AddDays(-2)], clusterRuns.Select(r => r.StartedAt));
            Assert.Equal(["Alpha", "Beta"], clusterLatestTwo.Select(r => r.InstanceName));

            var soloRun = Assert.Single(soloRuns);
            Assert.Equal((soloRow, ScheduledActionKind.RconCommand, ScheduledActionOutcome.Started, (DateTimeOffset?)null, now), (soloRun.ScheduledActionId, soloRun.Kind, soloRun.Outcome, soloRun.CompletedAt, soloRun.ScheduledFor));
        }
    }

    // ---- not found ----------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMethod_ReportsAMissingOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await StartAsync(ct);
        using (host)
        {
            Assert.Equal("The instance no longer exists.", (await host.Instances.ListScheduledActionsAsync(999, ct)).Error);
            Assert.Equal("The instance no longer exists.", (await host.Instances.SaveScheduledActionsAsync(999, [Restart("0 3 * * *")], ct)).Error);
            Assert.Equal("The instance no longer exists.", (await host.Instances.ListScheduledActionRunsAsync(999, 10, ct)).Error);
            Assert.Equal("The cluster no longer exists.", (await host.Clusters.ListScheduledActionsAsync(999, ct)).Error);
            Assert.Equal("The cluster no longer exists.", (await host.Clusters.SaveScheduledActionsAsync(999, [Restart("0 3 * * *")], ct)).Error);
            Assert.Equal("The cluster no longer exists.", (await host.Clusters.ListScheduledActionRunsAsync(999, 10, ct)).Error);
        }
    }

    // ---- dashboard ----------------------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_ShowsTheNextDeadline_OnlyWhereARowApplies()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, mapId) = await StartAsync(ct);
        using (host)
        {
            var (cluster, alpha, beta, solo) = await SeedAsync(host, mapId, ct);
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart("0 3 * * *")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(solo, [Restart("0 1 * * *", enabled: false)], ct)).Succeeded);
            Assert.True((await host.Instances.SaveAsync(beta, Edit(await host.InstanceAsync(beta, ct), true), ct)).Succeeded);

            var dashboard = await host.Instances.GetDashboardAsync(ct);
            var detail = await host.Clusters.GetAsync(cluster, ct);

            var alphaRow = dashboard.Instances.Single(i => i.Id == alpha);
            var deadline = Assert.NotNull(alphaRow.NextDeadline);
            Assert.True(deadline > CommandTestHost.Now, $"{deadline:O} is not after now");
            Assert.True(deadline <= CommandTestHost.Now.AddHours(48), $"{deadline:O} is more than two days out");
            Assert.Equal(TimeSpan.FromHours(3), TimeZoneInfo.ConvertTime(deadline, TimeZoneInfo.Local).TimeOfDay);
            Assert.Null(dashboard.Instances.Single(i => i.Id == beta).NextDeadline);
            Assert.Null(dashboard.Instances.Single(i => i.Id == solo).NextDeadline);
            Assert.Equal(deadline, detail!.Instances.Single(i => i.Id == alpha).NextDeadline);
            Assert.Null(detail.Instances.Single(i => i.Id == beta).NextDeadline);
        }
    }
}
