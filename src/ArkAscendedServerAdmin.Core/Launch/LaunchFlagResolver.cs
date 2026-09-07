using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Resolves the cluster base + instance override model for <see cref="LaunchFlags"/> (DESIGN.md §6,
/// plan step 17): per property the instance's non-null value wins, otherwise the cluster's value is
/// inherited. Free-text additional arguments are concatenated (cluster first) rather than overridden.
/// </summary>
public static class LaunchFlagResolver
{
    /// <summary>
    /// Returns a new <see cref="LaunchFlags"/>; neither input is mutated. Pass <see langword="null"/> as
    /// <paramref name="clusterBase"/> for a standalone instance.
    /// </summary>
    public static LaunchFlags Resolve(LaunchFlags? clusterBase, LaunchFlags instanceOverride)
    {
        ArgumentNullException.ThrowIfNull(instanceOverride);

        return new LaunchFlags
        {
            NoBattlEye = instanceOverride.NoBattlEye ?? clusterBase?.NoBattlEye,
            ServerPlatform = instanceOverride.ServerPlatform ?? clusterBase?.ServerPlatform,
            ExclusiveJoin = instanceOverride.ExclusiveJoin ?? clusterBase?.ExclusiveJoin,
            NoWildBabies = instanceOverride.NoWildBabies ?? clusterBase?.NoWildBabies,
            PreventSpawnAnimations = instanceOverride.PreventSpawnAnimations ?? clusterBase?.PreventSpawnAnimations,
            UseStore = instanceOverride.UseStore ?? clusterBase?.UseStore,
            ConvertToStore = instanceOverride.ConvertToStore ?? clusterBase?.ConvertToStore,
            ServerGameLog = instanceOverride.ServerGameLog ?? clusterBase?.ServerGameLog,
            ServerGameLogIncludeTribeLogs = instanceOverride.ServerGameLogIncludeTribeLogs ?? clusterBase?.ServerGameLogIncludeTribeLogs,
            ServerRconOutputTribeLogs = instanceOverride.ServerRconOutputTribeLogs ?? clusterBase?.ServerRconOutputTribeLogs,
            ActiveEvent = instanceOverride.ActiveEvent ?? clusterBase?.ActiveEvent,
            AdditionalArgs = ConcatenateAdditionalArgs(clusterBase?.AdditionalArgs, instanceOverride.AdditionalArgs),
        };
    }

    /// <summary>Cluster text then instance text joined by one space; blank parts dropped; null when both are blank.</summary>
    private static string? ConcatenateAdditionalArgs(string? clusterText, string? instanceText)
    {
        var parts = new[] { clusterText, instanceText }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToList();

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
