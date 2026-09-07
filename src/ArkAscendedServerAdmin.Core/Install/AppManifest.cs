using System.Globalization;
using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Install;

/// <summary>
/// Minimal reader for Steam's <c>appmanifest_&lt;appid&gt;.acf</c> (a VDF/KeyValues file). The only fact
/// the manager needs is <c>StateFlags</c>: 4 means "fully installed"; anything else (2 = update required,
/// 1026 = update in progress, ...) means SteamCMD did not finish. A directory-exists check is never used.
/// </summary>
public static partial class AppManifest
{
    /// <summary>The <c>StateFlags</c> value Steam writes once an app is fully installed.</summary>
    public const int StateFullyInstalled = 4;

    public static bool IsFullyInstalled(string manifestText) =>
        TryReadStateFlags(manifestText, out var flags) && flags == StateFullyInstalled;

    public static bool TryReadStateFlags(string manifestText, out int stateFlags)
    {
        ArgumentNullException.ThrowIfNull(manifestText);
        var match = StateFlagsPattern().Match(manifestText);
        if (match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stateFlags))
        {
            return true;
        }

        stateFlags = 0;
        return false;
    }

    [GeneratedRegex("""^\s*"StateFlags"\s+"(?<value>\d+)"\s*$""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StateFlagsPattern();
}

/// <summary>Result of checking the on-disk install (plan step 11).</summary>
public sealed record GameInstallStatus(bool SteamCmdPresent, bool InstallComplete, string Detail)
{
    public bool IsComplete => SteamCmdPresent && InstallComplete;
}

public interface IGameInstallChecker
{
    GameInstallStatus Check();
}

/// <summary>Outcome of a SteamCMD install/update run.</summary>
public sealed record InstallResult(bool Succeeded, string? Error = null)
{
    public static readonly InstallResult Success = new(true);

    public static InstallResult Failure(string error) => new(false, error);
}

/// <summary>
/// Downloads SteamCMD if needed and installs/updates the server (plan step 20). Phase 4 supplies the
/// real runner; Phase 1 registers a placeholder that reports failure with an explanation.
/// </summary>
public interface IGameInstaller
{
    Task<InstallResult> InstallAsync(CancellationToken cancellationToken);
}
