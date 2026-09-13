using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Everything <see cref="LaunchArgumentBuilder"/> needs, already resolved from the instance and its
/// cluster: <paramref name="Flags"/> comes from <see cref="LaunchFlagResolver"/>, the mod lists are in
/// cluster order then instance order with <paramref name="MapModId"/> (a custom map's own mod) ahead of
/// both, and <paramref name="ClusterDirectory"/> is the full path from
/// <see cref="Configuration.DataRootLayout.ClusterDirectory"/>. <paramref name="ClusterKey"/> and
/// <paramref name="ClusterDirectory"/> are both <see langword="null"/> for a standalone instance and both
/// set for a clustered one; the builder rejects any other combination.
/// </summary>
public sealed record LaunchRequest(
    string MapKey,
    string Slug,
    int GamePort,
    int MaxPlayers,
    string? ClusterKey,
    string? ClusterDirectory,
    IReadOnlyList<int> ClusterModIds,
    IReadOnlyList<int> InstanceModIds,
    LaunchFlags Flags,
    int? MapModId = null);
