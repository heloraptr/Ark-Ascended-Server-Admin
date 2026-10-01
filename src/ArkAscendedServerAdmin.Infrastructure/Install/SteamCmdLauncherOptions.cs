namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// How SteamCMD is started, bound from the <c>ArkAdmin</c> section of <c>appsettings.json</c> (host
/// setting; a change needs a service restart).
/// </summary>
public sealed class SteamCmdLauncherOptions
{
    /// <summary>
    /// <c>ArkAdmin:SteamCmdLiveOutput</c>. True runs SteamCMD under a pseudo console so its output
    /// streams as it happens (<see cref="PseudoConsoleSteamCmdLauncher"/>); false forces the redirected
    /// pipes of <see cref="ProcessSteamCmdLauncher"/>, where every line arrives when SteamCMD exits. An
    /// escape hatch in case the pseudo console misbehaves on a host.
    /// </summary>
    public bool SteamCmdLiveOutput { get; set; } = true;
}
