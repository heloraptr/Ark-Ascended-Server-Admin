using System.Linq.Expressions;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Processes;
using ArkAscendedServerAdmin.Launch;
using ArkAscendedServerAdmin.Mods;
using ArkAscendedServerAdmin.Naming;
using ArkAscendedServerAdmin.Ports;
using ArkAscendedServerAdmin.Rcon;
using ArkAscendedServerAdmin.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Validation and lookup helpers shared by the command facades.</summary>
internal static class CommandSupport
{
    public const int MaxNameLength = 100;

    public const int MaxSessionNameLength = 200;

    /// <summary>Every slug that a new instance or cluster must not reuse: live instances, clusters, and archived worlds.</summary>
    public static async Task<IReadOnlyList<string>> ReservedSlugsAsync(AppDbContext db, DataRootLayout layout, CancellationToken cancellationToken)
    {
        var reserved = new List<string>();
        reserved.AddRange(await db.Instances.AsNoTracking().Select(i => i.Slug).ToListAsync(cancellationToken));
        reserved.AddRange(await db.Clusters.AsNoTracking().Select(c => c.Slug).ToListAsync(cancellationToken));

        if (Directory.Exists(layout.Archive))
        {
            foreach (var directory in Directory.EnumerateDirectories(layout.Archive))
            {
                var name = Path.GetFileName(directory);
                reserved.Add(name);
                if (Slug.TryParseArchiveDirectoryName(name) is { } archivedSlug)
                {
                    reserved.Add(archivedSlug);
                }
            }
        }

        return reserved;
    }

    public static IReadOnlyList<string> ValidateName(string? name, string what)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what} name is required.");
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            problems.Add($"{what} name must be {MaxNameLength} characters or fewer.");
        }

        return problems;
    }

    public static IReadOnlyList<string> ValidateSessionName(string? sessionName)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            problems.Add("Session name is required; it is what players see in the server browser.");
            return problems;
        }

        if (sessionName.Trim().Length > MaxSessionNameLength)
        {
            problems.Add($"Session name must be {MaxSessionNameLength} characters or fewer.");
        }

        if (ReservedKeys.ValidateTypedValue("SessionName", sessionName) is { } problem)
        {
            problems.Add(problem);
        }

        return problems;
    }

    public static IReadOnlyList<string> ValidateLaunchFlags(LaunchFlags flags)
    {
        // Empty typed values mean "not passed"; only a set value has to be usable on the command line.
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(flags.ServerPlatform) && ReservedKeys.ValidateTypedValue("ServerPlatform", flags.ServerPlatform) is { } platformProblem)
        {
            problems.Add(platformProblem);
        }

        if (!string.IsNullOrWhiteSpace(flags.ActiveEvent) && ReservedKeys.ValidateTypedValue("ActiveEvent", flags.ActiveEvent) is { } eventProblem)
        {
            problems.Add(eventProblem);
        }

        problems.AddRange(AdditionalArgs.Validate(flags.AdditionalArgs));
        return problems;
    }

    public static IReadOnlyList<string> ValidatePlayers(int maxPlayers) =>
        maxPlayers is < LaunchArgumentBuilder.MinPlayers or > LaunchArgumentBuilder.MaxPlayers
            ? [$"Max players must be between {LaunchArgumentBuilder.MinPlayers} and {LaunchArgumentBuilder.MaxPlayers}."]
            : [];

    public static IReadOnlyList<string> ValidateBackupSettings(int? intervalMinutes, int? retention)
    {
        var problems = new List<string>();
        if (intervalMinutes is { } interval && interval is < 1 or > 10080)
        {
            problems.Add("Backup interval must be between 1 and 10080 minutes, or left blank to use the default.");
        }

        if (retention is { } keep && keep is < 1 or > 1000)
        {
            problems.Add("Backups to keep must be between 1 and 1000, or left blank to use the default.");
        }

        return problems;
    }

    /// <summary>One id per line; blank lines are dropped, everything else is kept verbatim after trimming.</summary>
    public static string NormalizeWhitelist(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join("\r\n", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Copies the typed flags into a fresh owned entity so EF tracks the change as a whole.</summary>
    public static void CopyLaunchFlags(LaunchFlags source, LaunchFlags target)
    {
        target.NoBattlEye = source.NoBattlEye;
        target.ServerPlatform = Trimmed(source.ServerPlatform);
        target.ExclusiveJoin = source.ExclusiveJoin;
        target.NoWildBabies = source.NoWildBabies;
        target.UseStore = source.UseStore;
        target.ConvertToStore = source.ConvertToStore;
        target.ServerGameLogIncludeTribeLogs = source.ServerGameLogIncludeTribeLogs;
        target.ServerRconOutputTribeLogs = source.ServerRconOutputTribeLogs;
        target.ActiveEvent = Trimmed(source.ActiveEvent);
        target.AdditionalArgs = Trimmed(source.AdditionalArgs);
    }

    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The web UI's first bound port, which the port allocator treats as occupied.</summary>
    public static int HostPort(HostConfiguration host)
    {
        var ports = KestrelPorts.Parse(host.BindUrls);
        return ports.Count > 0 ? ports[0] : 0;
    }

    public static async Task<IReadOnlyList<PortOwner>> PortOwnersAsync(AppDbContext db, int? excludeInstanceId, CancellationToken cancellationToken) =>
        await db.Instances.AsNoTracking()
            .Where(i => excludeInstanceId == null || i.Id != excludeInstanceId)
            .Select(i => new PortOwner(i.Name, i.GamePort, i.RconPort))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The dashboard row. <paramref name="clusterMods"/> is the cluster's list for a member; the caller loads
    /// it because not every query includes the cluster. <paramref name="modDates"/> is mod id to
    /// <c>dateModified</c> for the whole library, which is what decides the "changed since last launch" flag (B8).
    /// </summary>
    public static InstanceSummary ToSummary(
        Instance instance,
        IEnumerable<ClusterMod>? clusterMods,
        BackupRecord? lastBackup,
        DateTimeOffset? nextDeadline,
        IReadOnlyDictionary<int, DateTimeOffset?> modDates)
    {
        var modIds = ActiveModIds(instance, clusterMods);
        return new InstanceSummary(
            instance.Id,
            instance.Name,
            instance.Slug,
            instance.ClusterId,
            instance.Map?.Key ?? string.Empty,
            instance.Map?.Name ?? string.Empty,
            instance.SessionName,
            instance.GamePort,
            instance.RconPort,
            instance.MaxPlayers,
            modIds.Count,
            lastBackup,
            nextDeadline,
            ModUpdateStatus.ChangedSinceLaunch(new InstanceModLoad(instance.LastLaunchedAt, modIds), modDates));
    }

    /// <summary>
    /// The dashboard's next deadline for <paramref name="instance"/> (B3): the earliest upcoming occurrence over
    /// the rows that apply to it (its own, loaded on the entity, plus <paramref name="clusterActions"/> unless it
    /// overrides them) in the host's local zone; null when nothing is scheduled.
    /// </summary>
    public static DateTimeOffset? NextDeadline(Instance instance, IEnumerable<ScheduledAction>? clusterActions, TimeProvider time) =>
        ScheduleOccurrences.NextDeadline(
            ScheduleOccurrences.Applicable(instance, instance.ScheduledActions.Concat(clusterActions ?? [])),
            time.GetUtcNow(),
            time.LocalTimeZone);

    /// <summary>
    /// The ids a start would put in <c>-mods</c>: the map's own mod, the enabled cluster mods, and the
    /// enabled instance mods, without duplicates (the same union <see cref="Launch.LaunchArgumentBuilder"/> emits).
    /// </summary>
    public static IReadOnlyCollection<int> ActiveModIds(Instance instance, IEnumerable<ClusterMod>? clusterMods)
    {
        IEnumerable<int> mapMod = instance.Map?.ModId is { } mapModId ? [mapModId] : [];
        return mapMod
            .Concat((clusterMods ?? []).Where(m => m.Enabled).Select(m => m.ModId))
            .Concat(instance.Mods.Where(m => m.Enabled).Select(m => m.ModId))
            .Distinct()
            .ToList();
    }

    /// <summary>Mod id to <c>dateModified</c> for every library entry, the lookup the badge decision reads (B8).</summary>
    public static async Task<IReadOnlyDictionary<int, DateTimeOffset?>> ModDatesAsync(AppDbContext db, CancellationToken cancellationToken) =>
        await db.ModLibrary.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.DateModified, cancellationToken);

    /// <summary>What every instance would load, with the launch time the badge compares against (B8).</summary>
    public static async Task<IReadOnlyList<InstanceModLoad>> ModLoadsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var instances = await db.Instances.AsNoTracking()
            .Include(i => i.Map)
            .Include(i => i.Mods)
            .Include(i => i.Cluster).ThenInclude(c => c!.Mods)
            .ToListAsync(cancellationToken);
        return instances.Select(i => new InstanceModLoad(i.LastLaunchedAt, ActiveModIds(i, i.Cluster?.Mods))).ToList();
    }

    public static Task<BackupRecord?> LastBackupAsync(AppDbContext db, int instanceId, CancellationToken cancellationToken) =>
        db.BackupRecords.AsNoTracking()
            .Where(b => b.InstanceId == instanceId)
            .OrderByDescending(b => b.Id) // SQLite cannot order by a DateTimeOffset column; ids are monotonic
            .FirstOrDefaultAsync(cancellationToken);

    // ---- scheduled actions (B3) ---------------------------------------------------------------------

    public const int MaxRconCommandLength = RconCommands.MaxCommandLength;

    public const int MaxCronLength = 128;

    public const int MaxWarningMinutes = 60;

    /// <summary>How many upcoming occurrences a save looks at to catch a schedule that repeats inside its own countdown.</summary>
    private const int UpcomingOccurrencesChecked = 50;

    /// <summary>
    /// The owner's rows in id order, each flagged <paramref name="inherited"/>, with the friendly text and the
    /// next deadline after now in the host's local zone computed in memory; <paramref name="owned"/> picks the
    /// owner.
    /// </summary>
    public static async Task<IReadOnlyList<ScheduledActionView>> ScheduledActionsAsync(
        AppDbContext db,
        Expression<Func<ScheduledAction, bool>> owned,
        bool inherited,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        var rows = await db.ScheduledActions.AsNoTracking()
            .Where(owned)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(a => new ScheduledActionView(
                a.Id,
                a.InstanceId,
                a.ClusterId,
                a.Cron,
                ScheduleDescriptions.Describe(a.Cron),
                ScheduleOccurrences.NextDeadline(a, now, zone),
                a.Kind,
                a.Command,
                a.WarningMinutes,
                a.Enabled,
                inherited))
            .ToList();
    }

    /// <summary>
    /// The newest <paramref name="take"/> runs matching <paramref name="scope"/>, newest first. Ids are handed out
    /// when a run starts, so ordering by id is ordering by <see cref="ScheduledActionRun.StartedAt"/>, which SQLite
    /// cannot sort by; the page is sorted by the timestamp once loaded.
    /// </summary>
    public static async Task<IReadOnlyList<ScheduledActionRunView>> ScheduledActionRunsAsync(
        AppDbContext db,
        Expression<Func<ScheduledActionRun, bool>> scope,
        int take,
        CancellationToken cancellationToken)
    {
        var page = await db.ScheduledActionRuns.AsNoTracking()
            .Where(scope)
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(r => new ScheduledActionRunView(
                r.Id,
                r.ScheduledActionId,
                r.InstanceId,
                r.Instance!.Name,
                r.ScheduledFor,
                r.StartedAt,
                r.CompletedAt,
                r.Outcome,
                r.Reason,
                r.ScheduledAction!.Kind))
            .ToListAsync(cancellationToken);

        return page.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).ToList();
    }

    /// <summary>
    /// The field problems in a whole-list schedule save, each naming the row by its position (1-based).
    /// <paramref name="now"/> and <paramref name="zone"/> anchor the look-ahead that catches a schedule that
    /// never fires or repeats inside its own warning countdown.
    /// </summary>
    public static IReadOnlyList<string> ValidateScheduledActions(IReadOnlyList<ScheduledActionEdit> rows, DateTimeOffset now, TimeZoneInfo zone)
    {
        var problems = new List<string>();
        var seen = new HashSet<int>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var label = $"Row {index + 1}";
            if (row.Id != 0 && !seen.Add(row.Id))
            {
                problems.Add($"{label} repeats scheduled action #{row.Id}.");
            }

            var cron = (row.Cron ?? string.Empty).Trim();
            IReadOnlyList<DateTimeOffset> upcoming = [];
            if (cron.Length > MaxCronLength)
            {
                problems.Add($"{label}: the schedule must be {MaxCronLength} characters or fewer.");
            }
            else if (!ScheduleOccurrences.TryParse(cron, out var expression))
            {
                problems.Add($"{label}: the schedule is not a valid cron expression.");
            }
            else
            {
                upcoming = ScheduleOccurrences.NextDeadlines(expression, now, zone, UpcomingOccurrencesChecked);
                if (upcoming.Count == 0)
                {
                    problems.Add($"{label}: the schedule never runs.");
                }
            }

            if (!Enum.IsDefined(row.Kind))
            {
                problems.Add($"{label}: the action kind is not recognized.");
            }

            if (row.WarningMinutes is < 0 or > MaxWarningMinutes)
            {
                problems.Add($"{label}: the warning must be between 0 and {MaxWarningMinutes} minutes.");
            }

            if (row.Kind == ScheduledActionKind.RconCommand)
            {
                if (string.IsNullOrWhiteSpace(row.Command))
                {
                    problems.Add($"{label}: an RCON command is required.");
                }
                else if (row.Command.Trim().Length > MaxRconCommandLength)
                {
                    problems.Add($"{label}: the RCON command must be {MaxRconCommandLength} characters or fewer.");
                }
            }

            // Two occurrences closer than the countdown plus the runner's minute would make the second one
            // land while the first is still counting down, and it would only ever be skipped.
            if (upcoming.Count > 1 && row.WarningMinutes is >= 0 and <= MaxWarningMinutes)
            {
                var effectiveWarning = row.Kind == ScheduledActionKind.RconCommand ? 0 : row.WarningMinutes;
                var minimumGap = TimeSpan.FromMinutes(effectiveWarning + 1);
                for (var i = 1; i < upcoming.Count; i++)
                {
                    if (upcoming[i] - upcoming[i - 1] < minimumGap)
                    {
                        problems.Add($"{label}: the schedule repeats faster than its warning countdown.");
                        break;
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The problems for rows whose id is not one of the owner's <paramref name="own"/> rows: unknown, another
    /// owner's, or (on an instance, when <paramref name="inheritedIds"/> has it) the cluster's, which only the
    /// cluster page edits. <paramref name="owner"/> is "instance" or "cluster" for the message.
    /// </summary>
    public static IReadOnlyList<string> ValidateScheduledActionOwnership(
        IReadOnlyList<ScheduledActionEdit> rows,
        IEnumerable<ScheduledAction> own,
        string owner,
        IReadOnlySet<int>? inheritedIds = null)
    {
        var ownIds = own.Select(a => a.Id).ToHashSet();
        var problems = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var id = rows[index].Id;
            if (id == 0 || ownIds.Contains(id))
            {
                continue;
            }

            problems.Add(inheritedIds?.Contains(id) == true
                ? $"Row {index + 1}: scheduled action #{id} comes from the cluster; change it on the cluster page."
                : $"Row {index + 1}: scheduled action #{id} does not belong to this {owner}.");
        }

        return problems;
    }

    /// <summary>
    /// Applies a validated whole-list save to <paramref name="existing"/>, the owner's tracked rows: a known id is
    /// updated in place (never deleted and re-added, which would drop its runs and trip EF over the reused key),
    /// id zero is added with <paramref name="setOwner"/> applied, and rows left out of <paramref name="rows"/> are
    /// removed. Every non-zero id has already been checked against <paramref name="existing"/>.
    /// </summary>
    public static void ApplyScheduledActions(AppDbContext db, IReadOnlyList<ScheduledAction> existing, IReadOnlyList<ScheduledActionEdit> rows, Action<ScheduledAction> setOwner)
    {
        var kept = new HashSet<int>();
        foreach (var row in rows)
        {
            ScheduledAction target;
            if (row.Id == 0)
            {
                target = new ScheduledAction();
                setOwner(target);
                db.ScheduledActions.Add(target);
            }
            else
            {
                target = existing.Single(a => a.Id == row.Id);
                kept.Add(row.Id);
            }

            target.Cron = row.Cron.Trim();
            target.Kind = row.Kind;
            target.Command = row.Kind == ScheduledActionKind.RconCommand ? row.Command.Trim() : string.Empty;
            target.WarningMinutes = row.WarningMinutes;
            target.Enabled = row.Enabled;
        }

        db.ScheduledActions.RemoveRange(existing.Where(a => !kept.Contains(a.Id)));
    }
}
