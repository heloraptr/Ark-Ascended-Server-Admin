using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArkAscendedServerAdmin.Firewall;

/// <summary>
/// Names and descriptions of the instance firewall rules. Instance ids start at 1 in every installation, so
/// the name also carries a short tag derived from the DataRoot: two installs on one machine (a dev root beside
/// the live service, or a second service) then never match, replace or delete each other's rules.
/// </summary>
public static class FirewallRuleNames
{
    /// <summary>Shared by every rule the app creates, so <c>Get-NetFirewallRule -DisplayName 'ArkAscendedServerAdmin*'</c> lists them all.</summary>
    public const string NamePrefix = "ArkAscendedServerAdmin-";

    /// <summary>
    /// The installation tag: the first 8 lowercase hex characters of SHA-256 over the UTF-8 of the full DataRoot
    /// path, without trailing separators and upper-cased, because NTFS paths are case-insensitive and
    /// <c>C:\Ark</c>, <c>c:\ark\</c> and <c>C:\ARK</c> are the same installation.
    /// </summary>
    public static string InstallTag(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalized = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash, 0, 4);
    }

    /// <summary><c>ArkAscendedServerAdmin-&lt;tag&gt;-&lt;instanceId&gt;</c>, shared by the instance's two UDP rules.</summary>
    public static string RuleName(string tag, int instanceId) =>
        string.Create(CultureInfo.InvariantCulture, $"{NamePrefix}{tag}-{instanceId}");

    /// <summary>Names the port and the DataRoot, so a rule found in the firewall console can be traced to its install.</summary>
    public static string Description(int instanceId, int port, string dataRoot) =>
        string.Create(CultureInfo.InvariantCulture, $"ArkAscendedServerAdmin: UDP {port} for instance {instanceId} ({dataRoot})");
}
