namespace ArkAscendedServerAdmin.Mods;

/// <summary>
/// What a start would load for one instance: when the manager last launched it and the ids of every mod
/// the launch passes (the map's own mod, the enabled cluster mods, the enabled instance mods).
/// </summary>
public sealed record InstanceModLoad(DateTimeOffset? LastLaunchedAt, IReadOnlyCollection<int> ModIds);

/// <summary>
/// The "changed since last launch" decision (B8), kept pure so the facades and the pages share one rule.
/// A mod counts as changed when its CurseForge <c>dateModified</c> is later than the launch the manager
/// last issued. An instance that has never been launched never counts as changed: there is nothing to
/// compare against, and launching is what acknowledges the change.
/// </summary>
public static class ModUpdateStatus
{
    /// <summary>Whether any of the mods an instance loads was modified after <paramref name="lastLaunchedAt"/>.</summary>
    /// <param name="lastLaunchedAt">When the manager last issued the launch; null when it never has.</param>
    /// <param name="loadedModDates">The <c>dateModified</c> of each loaded mod; a null date is unknown and never counts.</param>
    public static bool ChangedSinceLaunch(DateTimeOffset? lastLaunchedAt, IEnumerable<DateTimeOffset?> loadedModDates)
    {
        ArgumentNullException.ThrowIfNull(loadedModDates);
        return lastLaunchedAt is { } launched && loadedModDates.Any(date => date is { } modified && modified > launched);
    }

    /// <summary>The same decision for one instance against a mod id → <c>dateModified</c> lookup.</summary>
    public static bool ChangedSinceLaunch(InstanceModLoad load, IReadOnlyDictionary<int, DateTimeOffset?> dateModifiedByMod)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(dateModifiedByMod);
        return ChangedSinceLaunch(load.LastLaunchedAt, load.ModIds.Select(id => dateModifiedByMod.GetValueOrDefault(id)));
    }

    /// <summary>
    /// The mods that changed since the last launch of at least one instance that loads them, for the mod
    /// library list. A mod nothing loads, or one only loaded by instances that were never launched, is not
    /// in the result.
    /// </summary>
    public static IReadOnlySet<int> ChangedMods(IEnumerable<InstanceModLoad> loads, IReadOnlyDictionary<int, DateTimeOffset?> dateModifiedByMod)
    {
        ArgumentNullException.ThrowIfNull(loads);
        ArgumentNullException.ThrowIfNull(dateModifiedByMod);
        var changed = new HashSet<int>();
        foreach (var load in loads)
        {
            if (load.LastLaunchedAt is not { } launched)
            {
                continue;
            }

            foreach (var id in load.ModIds)
            {
                if (dateModifiedByMod.GetValueOrDefault(id) is { } modified && modified > launched)
                {
                    changed.Add(id);
                }
            }
        }

        return changed;
    }
}
