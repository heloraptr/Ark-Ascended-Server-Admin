using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Firewall;

/// <summary>
/// Records this installation's firewall tag in <c>firewall.tag</c> beside the app's own binaries, so
/// <c>uninstall.ps1</c> can remove exactly the instance rules this installation created. The uninstaller
/// cannot resolve DataRoot the way the service account did, so it reads this file instead of deriving the
/// tag; the installer carries the file across upgrades. Written on every start so it always names the tag
/// of the rules that exist. For a dev console run the folder is a <c>bin</c> folder nobody reads.
/// </summary>
public sealed class FirewallTagFile(DataRootLayout layout, ILogger<FirewallTagFile> logger)
{
    public const string FileName = "firewall.tag";

    /// <summary>Writes the file to <see cref="AppContext.BaseDirectory"/>; see <see cref="WriteAsync(string, CancellationToken)"/>.</summary>
    public Task WriteAsync(CancellationToken cancellationToken) => WriteAsync(AppContext.BaseDirectory, cancellationToken);

    /// <summary>
    /// Writes exactly the 8 lowercase hex characters of the tag and a newline. A failure is logged as a
    /// warning and swallowed: the rules still work, only a later uninstall leaves them in place.
    /// </summary>
    public async Task WriteAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            await AtomicFile.WriteAllTextAsync(path, FirewallRuleNames.InstallTag(layout.Root) + "\n", cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Path}; an uninstall will leave this installation's instance firewall rules in place.", path);
        }
    }
}
