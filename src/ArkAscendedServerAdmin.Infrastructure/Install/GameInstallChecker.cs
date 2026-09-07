using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Install;

namespace ArkAscendedServerAdmin.Infrastructure.Install;

/// <summary>
/// Decides whether the box has a usable install: <c>steamcmd.exe</c> present and the app manifest's
/// <c>StateFlags</c> equal to 4. Never a directory-exists check (plan step 11).
/// </summary>
public sealed class GameInstallChecker(DataRootLayout layout) : IGameInstallChecker
{
    public GameInstallStatus Check()
    {
        var steamCmdPresent = File.Exists(layout.SteamCmdExecutable);

        if (!File.Exists(layout.AppManifestPath))
        {
            return new GameInstallStatus(steamCmdPresent, false, $"Missing {layout.AppManifestPath}.");
        }

        string manifest;
        try
        {
            manifest = File.ReadAllText(layout.AppManifestPath);
        }
        catch (IOException ex)
        {
            return new GameInstallStatus(steamCmdPresent, false, $"Could not read the app manifest: {ex.Message}");
        }

        if (!AppManifest.TryReadStateFlags(manifest, out var flags))
        {
            return new GameInstallStatus(steamCmdPresent, false, "The app manifest has no StateFlags entry.");
        }

        return flags == AppManifest.StateFullyInstalled
            ? new GameInstallStatus(steamCmdPresent, true, steamCmdPresent ? "Install verified." : $"Install verified, but {layout.SteamCmdExecutable} is missing.")
            : new GameInstallStatus(steamCmdPresent, false, $"The app manifest reports StateFlags {flags}; expected {AppManifest.StateFullyInstalled} (fully installed).");
    }
}
