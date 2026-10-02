using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Firewall;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Launch;
using ArkAscendedServerAdmin.Maintenance;
using ArkAscendedServerAdmin.Mods;
using ArkAscendedServerAdmin.Naming;
using ArkAscendedServerAdmin.Networking;
using ArkAscendedServerAdmin.Ports;
using ArkAscendedServerAdmin.Processes;
using ArkAscendedServerAdmin.Provisioning;
using ArkAscendedServerAdmin.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Server.Commands;

/// <summary>Guarded facade for the dashboard, the instance detail page, and the instance wizard.</summary>
public sealed class InstanceCommands(
    IAuthorizationGuard guard,
    IDbContextFactory<AppDbContext> contextFactory,
    DataRootLayout layout,
    HostConfiguration host,
    IAppSettingsStore settings,
    IProcessManager processManager,
    IInstanceLocks locks,
    IRestoreJournals restoreJournals,
    IBackupService backups,
    IInstanceDeleteService deleteService,
    IInstanceLayoutService layoutService,
    IIniSourceStore iniStore,
    IRconOperations rconOperations,
    IFirewallRules firewall,
    IHostAddressProvider hostAddresses,
    TimeProvider timeProvider,
    ILogger<InstanceCommands> logger) : IInstanceCommands
{
    public async Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var clusters = await db.Clusters.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ClusterSummary(c.Id, c.Name, c.Slug, c.ClusterKey))
            .ToListAsync(cancellationToken);

        var instances = await db.Instances.AsNoTracking()
            .Include(i => i.Map)
            .Include(i => i.Cluster).ThenInclude(c => c!.Mods)
            .Include(i => i.Cluster).ThenInclude(c => c!.ScheduledActions)
            .Include(i => i.Mods)
            .Include(i => i.ScheduledActions)
            .OrderBy(i => i.Name)
            .ToListAsync(cancellationToken);

        var modDates = await CommandSupport.ModDatesAsync(db, cancellationToken);
        var summaries = new List<InstanceSummary>(instances.Count);
        foreach (var instance in instances)
        {
            summaries.Add(CommandSupport.ToSummary(
                instance,
                instance.Cluster?.Mods,
                await CommandSupport.LastBackupAsync(db, instance.Id, cancellationToken),
                CommandSupport.NextDeadline(instance, instance.Cluster?.ScheduledActions, timeProvider),
                modDates));
        }

        return new DashboardData(clusters, summaries);
    }

    public async Task<InstanceDetail?> GetAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.AsNoTracking()
            .Include(i => i.Cluster)
            .Include(i => i.Map)
            .Include(i => i.Mods).ThenInclude(m => m.Mod)
            .Include(i => i.ExtraOverrides)
            .SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var clusterMods = instance.ClusterId is { } clusterId
            ? await db.ClusterMods.AsNoTracking()
                .Where(m => m.ClusterId == clusterId)
                .OrderBy(m => m.Order)
                .Select(m => new ModListItem(m.Mod!, m.Enabled))
                .ToListAsync(cancellationToken)
            : [];

        var instanceMods = instance.Mods.OrderBy(m => m.Order).Select(m => new ModListItem(m.Mod!, m.Enabled)).ToList();
        ModLibraryEntry? mapMod = null;
        if (instance.Map?.ModId is { } mapModId)
        {
            mapMod = await db.ModLibrary.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mapModId, cancellationToken)
                ?? new ModLibraryEntry { Id = mapModId, Name = $"Map mod {mapModId}" };
        }

        var loadedModDates = clusterMods.Concat(instanceMods)
            .Where(m => m.Enabled)
            .Select(m => m.Mod.DateModified)
            .Concat(mapMod is null ? [] : [mapMod.DateModified]);

        return new InstanceDetail(
            instance,
            clusterMods,
            instanceMods,
            mapMod,
            ModUpdateStatus.ChangedSinceLaunch(instance.LastLaunchedAt, loadedModDates));
    }

    public async Task<ConnectionView?> GetConnectionAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ports = await db.Instances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Select(i => new { i.GamePort, i.RconPort })
            .SingleOrDefaultAsync(cancellationToken);
        if (ports is null)
        {
            return null;
        }

        // The firewall is advisory here exactly as it is at start: a box whose firewall service is off, or an
        // app run without the rights to read the rules, shows "unknown" rather than losing the whole card.
        bool? ruleExists;
        try
        {
            ruleExists = firewall.InstanceRulesExist(instanceId);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not read the firewall rules for instance {InstanceId}.", instanceId);
            ruleExists = null;
        }

        return new ConnectionView(
            ports.GamePort,
            ports.RconPort,
            hostAddresses.GetLanAddresses(),
            (await settings.GetAsync(cancellationToken)).PublicAddress.Trim(),
            FirewallRules.RuleName(instanceId),
            ruleExists);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetBackupsAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.BackupRecords.AsNoTracking()
            .Where(b => b.InstanceId == instanceId)
            .OrderByDescending(b => b.Id)
            .ToListAsync(cancellationToken);
    }

    // ---- lifecycle -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RestoreRecord>> GetRestoresAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RestoreRecords.AsNoTracking()
            .Where(r => r.InstanceId == instanceId)
            .OrderByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<RestoreJournal?> GetRestoreJournalAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return restoreJournals.FindForInstance(instanceId);
    }

    public async Task<RestoreInspection> InspectRestoreAsync(int instanceId, string fileName, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await backups.InspectRestoreAsync(instanceId, fileName, cancellationToken);
    }

    public async Task<OperationOutcome> RestoreAsync(int instanceId, string fileName, bool includeCluster, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await backups.RestoreAsync(instanceId, fileName, includeCluster, cancellationToken);
    }

    public async Task<OperationOutcome> RecoverRestoreAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await backups.RecoverAsync(operationId, cancellationToken);
    }

    public async Task<OperationOutcome> DiscardRestoreJournalAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await backups.DiscardJournalAsync(operationId, cancellationToken);
    }

    public async Task<OperationOutcome> StartAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await processManager.StartAsync(instanceId, LaunchKind.User, cancellationToken);
    }

    public async Task<OperationOutcome> StopAsync(int instanceId, bool skipCountdown, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await processManager.StopAsync(instanceId, new StopOptions(SkipCountdown: skipCountdown), cancellationToken);
    }

    public async Task<OperationOutcome> RestartAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await processManager.RestartAsync(instanceId, cancellationToken);
    }

    public async Task<bool> SkipCountdownAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return processManager.TrySkipCountdown(instanceId);
    }

    public async Task<OperationOutcome> RetryPersistIdentityAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await processManager.RetryPersistIdentityAsync(instanceId, cancellationToken);
    }

    public Task<IReadOnlyList<BulkOutcome>> StartManyAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken = default) =>
        RunManyAsync(instanceIds, id => processManager.StartAsync(id, LaunchKind.User, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<BulkOutcome>> StopManyAsync(IReadOnlyList<int> instanceIds, CancellationToken cancellationToken = default) =>
        RunManyAsync(instanceIds, id => processManager.StopAsync(id, new StopOptions(), cancellationToken), cancellationToken);

    /// <summary>All requests are submitted at once (the launch queue staggers them) and the outcomes are collected together.</summary>
    private async Task<IReadOnlyList<BulkOutcome>> RunManyAsync(IReadOnlyList<int> instanceIds, Func<int, Task<OperationOutcome>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        await guard.EnsureAuthorizedAsync(cancellationToken);

        Dictionary<int, string> names;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            names = await db.Instances.AsNoTracking()
                .Where(i => instanceIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);
        }

        var tasks = instanceIds.Select(async id =>
        {
            var name = names.GetValueOrDefault(id, $"#{id}");
            try
            {
                return new BulkOutcome(id, name, await operation(id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Bulk operation on instance {InstanceId} threw.", id);
                return new BulkOutcome(id, name, OperationOutcome.Rejected(ex.Message));
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<CommandResult<BackupRecord>> BackupNowAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        try
        {
            return CommandResult<BackupRecord>.Ok(await backups.BackupNowAsync(instanceId, isManual: true, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult<BackupRecord>.Fail(ex.Message);
        }
    }

    public async Task<OperationOutcome> DeleteAsync(int instanceId, InstanceDeleteOptions options, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        return await deleteService.DeleteAsync(instanceId, options, cancellationToken);
    }

    // ---- console -------------------------------------------------------------------------------------

    public async Task<CommandResult<string>> SendRconAsync(int instanceId, string command, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        // The send itself (live-process check, credentials, console echo) is the shared path the scheduled-action runner uses too (B3).
        return await rconOperations.ExecuteAsync(instanceId, command, cancellationToken);
    }

    // ---- ports ---------------------------------------------------------------------------------------

    public async Task<PortSuggestion> SuggestPortsAsync(CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await CommandSupport.PortOwnersAsync(db, null, cancellationToken);
        var allocator = new PortAllocator(await settings.GetAsync(cancellationToken));
        var hostPort = CommandSupport.HostPort(host);
        return new PortSuggestion(allocator.NextGamePort(owners, hostPort), allocator.NextRconPort(owners, hostPort));
    }

    public async Task<IReadOnlyList<PortConflict>> CheckPortsAsync(string instanceName, int gamePort, int rconPort, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await FindPortConflictsAsync(db, instanceName, gamePort, rconPort, null, cancellationToken);
    }

    private async Task<IReadOnlyList<PortConflict>> FindPortConflictsAsync(AppDbContext db, string instanceName, int gamePort, int rconPort, int? excludeInstanceId, CancellationToken cancellationToken)
    {
        var owners = await CommandSupport.PortOwnersAsync(db, excludeInstanceId, cancellationToken);
        var allocator = new PortAllocator(await settings.GetAsync(cancellationToken));
        return allocator.FindConflicts(new PortOwner(instanceName, gamePort, rconPort), owners, CommandSupport.HostPort(host));
    }

    // ---- create / edit -------------------------------------------------------------------------------

    public async Task<CommandResult<int>> CreateAsync(InstanceDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var name = draft.Name.Trim();
        var problems = new List<string>();
        problems.AddRange(CommandSupport.ValidateName(draft.Name, "Instance"));
        if (problems.Count == 0 && await db.Instances.AnyAsync(i => i.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            problems.Add($"An instance named '{name}' already exists.");
        }

        problems.AddRange(CommandSupport.ValidateSessionName(draft.SessionName));
        problems.AddRange(CommandSupport.ValidatePlayers(draft.MaxPlayers));
        problems.AddRange(CommandSupport.ValidateLaunchFlags(draft.LaunchFlags));
        problems.AddRange(CommandSupport.ValidateBackupSettings(draft.BackupIntervalMinutes, draft.BackupRetention));
        var adminPassword = CommandSupport.Trimmed(draft.AdminPassword);
        if (adminPassword is not null)
        {
            problems.AddRange(IniOverrideValidator.Validate(IniGenerator.ServerSettingsSection, "ServerAdminPassword", adminPassword)
                .Select(p => p.Replace("Value", "Admin password", StringComparison.Ordinal)));
        }

        var map = await db.Maps.AsNoTracking().SingleOrDefaultAsync(m => m.Id == draft.MapId, cancellationToken);
        if (map is null)
        {
            problems.Add("Choose a map.");
        }

        Cluster? cluster = null;
        if (draft.ClusterId is { } clusterId)
        {
            cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clusterId, cancellationToken);
            if (cluster is null)
            {
                problems.Add("The chosen cluster no longer exists.");
            }
            else if (locks.IsClusterReserved(clusterId))
            {
                problems.Add("The cluster is reserved by a restore; try again when it finishes.");
            }
            else if (restoreJournals.FindForCluster(clusterId) is { } journal)
            {
                problems.Add(journal.RefusalReason("the cluster"));
            }
        }

        var mods = draft.Mods.DistinctBy(m => m.ModId).ToList();
        var modIds = mods.Select(m => m.ModId).ToList();
        var knownMods = await db.ModLibrary.AsNoTracking().Where(m => modIds.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken);
        if (knownMods.Count != modIds.Count)
        {
            problems.Add("One of the chosen mods is no longer in the library.");
        }

        if (await MapModGuard.FindProblemAsync(db, modIds, cancellationToken) is { } mapModProblem)
        {
            problems.Add(mapModProblem);
        }

        problems.AddRange((await FindPortConflictsAsync(db, name, draft.GamePort, draft.RconPort, null, cancellationToken)).Select(c => c.Reason));
        if (problems.Count > 0)
        {
            return CommandResult<int>.Fail(problems);
        }

        var slug = Slug.Generate(name, await CommandSupport.ReservedSlugsAsync(db, layout, cancellationToken));
        var instance = new Instance
        {
            Name = name,
            Slug = slug,
            ClusterId = cluster?.Id,
            MapId = map!.Id,
            SessionName = draft.SessionName.Trim(),
            MaxPlayers = draft.MaxPlayers,
            GamePort = draft.GamePort,
            RconPort = draft.RconPort,
            AdminWhitelist = CommandSupport.NormalizeWhitelist(draft.AdminWhitelist),
            BackupIntervalMinutes = draft.BackupIntervalMinutes,
            BackupRetention = draft.BackupRetention,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        CommandSupport.CopyLaunchFlags(draft.LaunchFlags, instance.LaunchFlags);
        for (var order = 0; order < modIds.Count; order++)
        {
            instance.Mods.Add(new InstanceMod { ModId = mods[order].ModId, Enabled = mods[order].Enabled, Order = order });
        }

        db.Instances.Add(instance);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await layoutService.EnsureAsync(slug, cancellationToken);
            if (cluster is null)
            {
                await SeedIniAsync(IniOwner.ForInstance(instance.Id), draft.ConfigSource, draft.ConfigSourceId, cancellationToken, adminPassword);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Provisioning instance {Slug} failed; removing the row again.", slug);
            db.Instances.Remove(instance);
            await db.SaveChangesAsync(cancellationToken);
            return CommandResult<int>.Fail($"The instance folder could not be prepared: {ex.Message}");
        }

        logger.LogInformation("Created instance {Name} ({Slug}) on {Map}.", name, slug, map.Key);
        return CommandResult<int>.Ok(instance.Id);
    }

    /// <summary>
    /// Writes both source files for a new owner from the chosen starting point (plan step 16, DESIGN §5).
    /// A non-null <paramref name="adminPassword"/> is set as <c>ServerAdminPassword</c> in the seeded
    /// <c>GameUserSettings.ini</c>, replacing whatever the source had.
    /// </summary>
    internal async Task SeedIniAsync(IniOwner owner, ConfigSourceKind source, int? sourceId, CancellationToken cancellationToken, string? adminPassword = null)
    {
        foreach (var file in new[] { IniFile.Game, IniFile.GameUserSettings })
        {
            var text = source switch
            {
                ConfigSourceKind.Blank => string.Empty,
                ConfigSourceKind.CopyFromInstance when sourceId is { } id => (await iniStore.LoadAsync(IniOwner.ForInstance(id), file, cancellationToken)).Text,
                ConfigSourceKind.CopyFromCluster when sourceId is { } id => (await iniStore.LoadAsync(IniOwner.ForCluster(id), file, cancellationToken)).Text,
                _ => file == IniFile.Game ? IniTemplates.DefaultGameIni : IniTemplates.DefaultGameUserSettings,
            };

            if (file == IniFile.GameUserSettings && adminPassword is not null)
            {
                var ini = IniText.Parse(text);
                ini.Set(IniGenerator.ServerSettingsSection, "ServerAdminPassword", adminPassword);
                text = ini.ToString();
            }

            var current = await iniStore.LoadAsync(owner, file, cancellationToken);
            var result = await iniStore.SaveAsync(owner, file, text, current.Sha256, cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error ?? $"Writing {file} failed.");
            }
        }
    }

    public async Task<CommandResult> SaveAsync(int instanceId, InstanceEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult.Fail("The instance no longer exists.");
        }

        var name = edit.Name.Trim();
        var problems = new List<string>();
        problems.AddRange(CommandSupport.ValidateName(edit.Name, "Instance"));
        if (problems.Count == 0 && await db.Instances.AnyAsync(i => i.Id != instanceId && i.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            problems.Add($"An instance named '{name}' already exists.");
        }

        problems.AddRange(CommandSupport.ValidateSessionName(edit.SessionName));
        problems.AddRange(CommandSupport.ValidatePlayers(edit.MaxPlayers));
        problems.AddRange(CommandSupport.ValidateBackupSettings(edit.BackupIntervalMinutes, edit.BackupRetention));
        problems.AddRange((await FindPortConflictsAsync(db, name, edit.GamePort, edit.RconPort, instanceId, cancellationToken)).Select(c => c.Reason));
        if (problems.Count > 0)
        {
            return CommandResult.Fail(problems);
        }

        instance.Name = name;
        instance.SessionName = edit.SessionName.Trim();
        instance.MaxPlayers = edit.MaxPlayers;
        instance.GamePort = edit.GamePort;
        instance.RconPort = edit.RconPort;
        instance.AdminWhitelist = CommandSupport.NormalizeWhitelist(edit.AdminWhitelist);
        instance.BackupIntervalMinutes = edit.BackupIntervalMinutes;
        instance.BackupRetention = edit.BackupRetention;
        instance.OverridesClusterSchedule = edit.OverridesClusterSchedule;
        instance.AutoRestart = edit.AutoRestart;
        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> SaveLaunchFlagsAsync(int instanceId, LaunchFlags flags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        await guard.EnsureAuthorizedAsync(cancellationToken);

        var problems = CommandSupport.ValidateLaunchFlags(flags);
        if (problems.Count > 0)
        {
            return CommandResult.Fail(problems);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var instance = await db.Instances.SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult.Fail("The instance no longer exists.");
        }

        CommandSupport.CopyLaunchFlags(flags, instance.LaunchFlags);
        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> SetModsAsync(int instanceId, IReadOnlyList<ModSelection> orderedMods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedMods);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.Include(i => i.Mods).SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult.Fail("The instance no longer exists.");
        }

        var mods = orderedMods.DistinctBy(m => m.ModId).ToList();
        var ids = mods.Select(m => m.ModId).ToList();
        var known = await db.ModLibrary.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken);
        if (known.Count != ids.Count)
        {
            return CommandResult.Fail("One of the chosen mods is no longer in the library.");
        }

        if (await MapModGuard.FindProblemAsync(db, ids, cancellationToken) is { } mapModProblem)
        {
            return CommandResult.Fail(mapModProblem);
        }

        // Rows are updated in place rather than replaced: EF folds a delete and an insert of the same key into one
        // update and loses the enabled flag on the way (seen in the integration test), and the rows never need to move.
        var existing = instance.Mods.ToDictionary(m => m.ModId);
        db.InstanceMods.RemoveRange(instance.Mods.Where(m => !ids.Contains(m.ModId)));
        for (var order = 0; order < mods.Count; order++)
        {
            if (existing.TryGetValue(mods[order].ModId, out var row))
            {
                row.Order = order;
                row.Enabled = mods[order].Enabled;
            }
            else
            {
                instance.Mods.Add(new InstanceMod { InstanceId = instanceId, ModId = mods[order].ModId, Enabled = mods[order].Enabled, Order = order });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult<LaunchPreview>> PreviewLaunchAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.AsNoTracking()
            .Include(i => i.Cluster).ThenInclude(c => c!.Mods)
            .Include(i => i.Map)
            .Include(i => i.Mods)
            .Include(i => i.ExtraOverrides)
            .SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult<LaunchPreview>.Fail("The instance no longer exists.");
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        var commandLine = string.Empty;

        var request = new LaunchRequest(
            instance.Map!.Key,
            instance.Slug,
            instance.GamePort,
            instance.MaxPlayers,
            instance.Cluster?.ClusterKey,
            instance.Cluster is { } c ? layout.ClusterDirectory(c.Slug) : null,
            instance.Cluster?.Mods.Where(m => m.Enabled).OrderBy(m => m.Order).Select(m => m.ModId).ToList() ?? [],
            instance.Mods.Where(m => m.Enabled).OrderBy(m => m.Order).Select(m => m.ModId).ToList(),
            LaunchFlagResolver.Resolve(instance.Cluster?.LaunchFlags, instance.LaunchFlags),
            instance.Map.ModId);
        try
        {
            commandLine = LaunchArgumentBuilder.Build(request).ToDisplayString();
        }
        catch (LaunchValidationException ex)
        {
            problems.AddRange(ex.Problems);
        }

        var owner = instance.ClusterId is { } clusterId ? IniOwner.ForCluster(clusterId) : IniOwner.ForInstance(instance.Id);
        var game = await iniStore.LoadAsync(owner, IniFile.Game, cancellationToken);
        var gameUserSettings = await iniStore.LoadAsync(owner, IniFile.GameUserSettings, cancellationToken);
        try
        {
            var generated = IniGenerator.Generate(new GenerationInput(
                game.Text,
                gameUserSettings.Text,
                instance.SessionName,
                instance.GamePort,
                instance.RconPort,
                instance.MaxPlayers,
                instance.ExtraOverrides.OrderBy(o => o.Id).Select(IniOverrideSpec.From).ToList(),
                instance.Cluster?.AdminWhitelist ?? string.Empty,
                instance.AdminWhitelist,
                (await settings.GetAsync(cancellationToken)).AdminWhitelist));
            warnings.AddRange(generated.Warnings);
            if (string.IsNullOrWhiteSpace(generated.ServerAdminPassword))
            {
                problems.Add("ServerAdminPassword under [ServerSettings] in GameUserSettings.ini is empty. Start is refused until it is set; RCON is the only way the manager saves and stops the server.");
            }
        }
        catch (ArgumentException ex)
        {
            problems.Add(ex.Message);
        }

        return CommandResult<LaunchPreview>.Ok(new LaunchPreview(commandLine, warnings, problems.Count == 0 ? null : string.Join(" ", problems)));
    }

    // ---- scheduled actions (B3) ---------------------------------------------------------------------

    public async Task<CommandResult<IReadOnlyList<ScheduledActionView>>> ListScheduledActionsAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult<IReadOnlyList<ScheduledActionView>>.Fail("The instance no longer exists.");
        }

        // Inherited rows come first, the way the whitelist editor lists the cluster's locked ids ahead of the instance's own.
        var rows = new List<ScheduledActionView>();
        if (!instance.OverridesClusterSchedule && instance.ClusterId is { } clusterId)
        {
            rows.AddRange(await CommandSupport.ScheduledActionsAsync(db, a => a.ClusterId == clusterId, inherited: true, timeProvider, cancellationToken));
        }

        rows.AddRange(await CommandSupport.ScheduledActionsAsync(db, a => a.InstanceId == instanceId, inherited: false, timeProvider, cancellationToken));
        return CommandResult<IReadOnlyList<ScheduledActionView>>.Ok(rows);
    }

    public async Task<CommandResult> SaveScheduledActionsAsync(int instanceId, IReadOnlyList<ScheduledActionEdit> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return CommandResult.Fail("The instance no longer exists.");
        }

        var own = await db.ScheduledActions.Where(a => a.InstanceId == instanceId).ToListAsync(cancellationToken);
        HashSet<int> inherited = [];
        if (instance.ClusterId is { } clusterId)
        {
            inherited = await db.ScheduledActions.Where(a => a.ClusterId == clusterId).Select(a => a.Id).ToHashSetAsync(cancellationToken);
        }

        var problems = new List<string>();
        problems.AddRange(CommandSupport.ValidateScheduledActions(rows, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone));
        problems.AddRange(CommandSupport.ValidateScheduledActionOwnership(rows, own, "instance", inherited));
        if (problems.Count > 0)
        {
            return CommandResult.Fail(problems);
        }

        CommandSupport.ApplyScheduledActions(db, own, rows, a => a.InstanceId = instanceId);
        await db.SaveChangesAsync(cancellationToken);
        return CommandResult.Ok;
    }

    public async Task<CommandResult<IReadOnlyList<ScheduledActionRunView>>> ListScheduledActionRunsAsync(int instanceId, int take = 10, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await guard.EnsureAuthorizedAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
        {
            return CommandResult<IReadOnlyList<ScheduledActionRunView>>.Fail("The instance no longer exists.");
        }

        var runs = await CommandSupport.ScheduledActionRunsAsync(db, r => r.InstanceId == instanceId, take, cancellationToken);
        return CommandResult<IReadOnlyList<ScheduledActionRunView>>.Ok(runs);
    }
}
