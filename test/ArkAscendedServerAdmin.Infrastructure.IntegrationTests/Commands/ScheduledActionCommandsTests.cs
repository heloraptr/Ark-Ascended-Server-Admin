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

    private static ScheduledActionEdit Restart(int timeOfDay, int id = 0, int warning = 10, bool enabled = true) =>
        new(id, timeOfDay, ScheduledActionKind.Restart, string.Empty, warning, enabled);

    private static ScheduledActionEdit Rcon(int timeOfDay, string command, int id = 0) =>
        new(id, timeOfDay, ScheduledActionKind.RconCommand, command, 10, true);

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

    private static async Task<int> AddRunAsync(CommandTestHost host, int actionId, int instanceId, DateOnly localDate, DateTimeOffset startedAt, ScheduledActionOutcome outcome, string reason, CancellationToken ct)
    {
        await using var db = host.Db();
        var run = new ScheduledActionRun
        {
            ScheduledActionId = actionId,
            InstanceId = instanceId,
            LocalDate = localDate,
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

            var first = await host.Instances.SaveScheduledActionsAsync(solo, [Restart(240), Rcon(300, "  saveworld  ")], ct);

            Assert.True(first.Succeeded, first.Error);
            var listed = await OwnRowsAsync(host, solo, ct);
            Assert.Equal(2, listed.Count);
            Assert.All(listed, r => Assert.Equal((solo, (int?)null), (r.OwnerInstanceId, r.OwnerClusterId)));
            Assert.Equal((240, ScheduledActionKind.Restart, string.Empty, 10, true), (listed[0].TimeOfDay, listed[0].Kind, listed[0].Command, listed[0].WarningMinutes, listed[0].Enabled));
            Assert.Equal((300, ScheduledActionKind.RconCommand, "saveworld"), (listed[1].TimeOfDay, listed[1].Kind, listed[1].Command));
            var restartId = listed[0].Id;
            var rconId = listed[1].Id;
            var runId = await AddRunAsync(host, restartId, solo, new DateOnly(2026, 9, 11), CommandTestHost.Now.AddDays(-1), ScheduledActionOutcome.Succeeded, string.Empty, ct);

            // Edit the restart, drop the RCON row, and add a dino wipe whose stray command text is cleared.
            var second = await host.Instances.SaveScheduledActionsAsync(
                solo,
                [Restart(250, restartId, warning: 5, enabled: false), new ScheduledActionEdit(0, 360, ScheduledActionKind.DinoWipe, "ignored", 15, true)],
                ct);

            Assert.True(second.Succeeded, second.Error);
            await using var db = host.Db();
            var rows = await db.ScheduledActions.Where(a => a.InstanceId == solo).OrderBy(a => a.Id).ToListAsync(ct);
            Assert.Equal(2, rows.Count);
            Assert.Equal(restartId, rows[0].Id);
            Assert.Equal((250, 5, false), (rows[0].TimeOfDay, rows[0].WarningMinutes, rows[0].Enabled));
            Assert.True(rows[1].Id > rconId);
            Assert.Equal((360, ScheduledActionKind.DinoWipe, string.Empty, 15), (rows[1].TimeOfDay, rows[1].Kind, rows[1].Command, rows[1].WarningMinutes));
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
            ScheduledActionEdit[] rows =
            [
                new(0, -1, ScheduledActionKind.Restart, string.Empty, 10, true),
                new(0, 1440, ScheduledActionKind.Restart, string.Empty, 61, true),
                new(0, 60, ScheduledActionKind.RconCommand, "   ", -1, true),
                new(0, 60, ScheduledActionKind.RconCommand, new string('x', 513), 0, true),
                new(0, 60, (ScheduledActionKind)42, string.Empty, 0, true),
                new(0, 60, ScheduledActionKind.RconCommand, new string('y', 512), 0, true),
            ];

            var result = await host.Instances.SaveScheduledActionsAsync(solo, rows, ct);

            Assert.False(result.Succeeded);
            Assert.Equal(
                [
                    "Row 1: the time of day must be between 00:00 and 23:59.",
                    "Row 2: the time of day must be between 00:00 and 23:59.",
                    "Row 2: the warning must be between 0 and 60 minutes.",
                    "Row 3: the warning must be between 0 and 60 minutes.",
                    "Row 3: an RCON command is required.",
                    "Row 4: the RCON command must be 512 characters or fewer.",
                    "Row 5: the action kind is not recognized.",
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
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart(120)], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(beta, [Restart(180)], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(alpha, [Restart(200)], ct)).Succeeded);
            var clusterRow = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!.Single().Id;
            var betaRow = (await OwnRowsAsync(host, beta, ct)).Single().Id;
            var alphaRow = (await OwnRowsAsync(host, alpha, ct)).Single().Id;

            var onAlpha = await host.Instances.SaveScheduledActionsAsync(
                alpha,
                [Restart(200, alphaRow), Restart(200, alphaRow), Restart(1, betaRow), Restart(1, 999), Restart(1, clusterRow)],
                ct);
            var onCluster = await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart(120, clusterRow), Restart(1, alphaRow), Restart(1, 999)], ct);

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
            Assert.Equal(120, (await db.ScheduledActions.SingleAsync(a => a.Id == clusterRow, ct)).TimeOfDay);
            Assert.Equal(180, (await db.ScheduledActions.SingleAsync(a => a.Id == betaRow, ct)).TimeOfDay);
            Assert.Equal(200, (await db.ScheduledActions.SingleAsync(a => a.Id == alphaRow, ct)).TimeOfDay);
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
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart(120), Rcon(130, "saveworld")], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(alpha, [Restart(200)], ct)).Succeeded);

            var clusterList = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!;
            var alphaList = (await host.Instances.ListScheduledActionsAsync(alpha, ct)).Value!;
            var betaList = (await host.Instances.ListScheduledActionsAsync(beta, ct)).Value!;
            var soloList = (await host.Instances.ListScheduledActionsAsync(solo, ct)).Value!;

            Assert.Equal(2, clusterList.Count);
            Assert.All(clusterList, r => Assert.Equal(((int?)null, cluster, false), (r.OwnerInstanceId, r.OwnerClusterId, r.Inherited)));
            Assert.Equal([true, true, false], alphaList.Select(r => r.Inherited));
            Assert.Equal([120, 130, 200], alphaList.Select(r => r.TimeOfDay));
            Assert.Equal((cluster, (int?)null), (alphaList[0].OwnerClusterId, alphaList[0].OwnerInstanceId));
            Assert.Equal(((int?)null, alpha), (alphaList[2].OwnerClusterId, alphaList[2].OwnerInstanceId));
            Assert.Equal([true, true], betaList.Select(r => r.Inherited));
            Assert.Empty(soloList);

            var overriding = await host.Instances.SaveAsync(alpha, Edit(await host.InstanceAsync(alpha, ct), true), ct);

            Assert.True(overriding.Succeeded, overriding.Error);
            Assert.True((await host.InstanceAsync(alpha, ct)).OverridesClusterSchedule);
            var ownOnly = (await host.Instances.ListScheduledActionsAsync(alpha, ct)).Value!;
            Assert.Equal([200], ownOnly.Select(r => r.TimeOfDay));
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
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart(120)], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(solo, [Rcon(60, "saveworld")], ct)).Succeeded);
            var clusterRow = (await host.Clusters.ListScheduledActionsAsync(cluster, ct)).Value!.Single().Id;
            var soloRow = (await OwnRowsAsync(host, solo, ct)).Single().Id;
            var now = CommandTestHost.Now;
            await AddRunAsync(host, clusterRow, alpha, new DateOnly(2026, 9, 10), now.AddDays(-2), ScheduledActionOutcome.Succeeded, string.Empty, ct);
            await AddRunAsync(host, clusterRow, beta, new DateOnly(2026, 9, 10), now.AddDays(-2).AddMinutes(1), ScheduledActionOutcome.Failed, "boom", ct);
            await AddRunAsync(host, clusterRow, alpha, new DateOnly(2026, 9, 11), now.AddDays(-1), ScheduledActionOutcome.Skipped, "The server is not running.", ct);
            await AddRunAsync(host, soloRow, solo, new DateOnly(2026, 9, 12), now, ScheduledActionOutcome.Started, string.Empty, ct);

            var alphaRuns = (await host.Instances.ListScheduledActionRunsAsync(alpha, 10, ct)).Value!;
            var alphaLatest = (await host.Instances.ListScheduledActionRunsAsync(alpha, 1, ct)).Value!;
            var clusterRuns = (await host.Clusters.ListScheduledActionRunsAsync(cluster, 10, ct)).Value!;
            var clusterLatestTwo = (await host.Clusters.ListScheduledActionRunsAsync(cluster, 2, ct)).Value!;
            var soloRuns = (await host.Instances.ListScheduledActionRunsAsync(solo, 10, ct)).Value!;

            Assert.Equal([new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 10)], alphaRuns.Select(r => r.LocalDate));
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
            Assert.Equal((soloRow, ScheduledActionKind.RconCommand, ScheduledActionOutcome.Started, (DateTimeOffset?)null), (soloRun.ScheduledActionId, soloRun.Kind, soloRun.Outcome, soloRun.CompletedAt));
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
            Assert.Equal("The instance no longer exists.", (await host.Instances.SaveScheduledActionsAsync(999, [Restart(1)], ct)).Error);
            Assert.Equal("The instance no longer exists.", (await host.Instances.ListScheduledActionRunsAsync(999, 10, ct)).Error);
            Assert.Equal("The cluster no longer exists.", (await host.Clusters.ListScheduledActionsAsync(999, ct)).Error);
            Assert.Equal("The cluster no longer exists.", (await host.Clusters.SaveScheduledActionsAsync(999, [Restart(1)], ct)).Error);
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
            Assert.True((await host.Clusters.SaveScheduledActionsAsync(cluster, [Restart(180)], ct)).Succeeded);
            Assert.True((await host.Instances.SaveScheduledActionsAsync(solo, [Restart(60, enabled: false)], ct)).Succeeded);
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
