namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// The single reserved-key policy (plan step 17): every command-line option and INI key the manager
/// owns. Enforced across all three input channels — free-text additional arguments are rejected when
/// they contain one, <c>ExtraOverrides</c> are rejected at save time, and occurrences in source INI text
/// are replaced by the generated values (with an editor warning). <c>ServerAdminPassword</c> is
/// deliberately <b>not</b> reserved: it is user-owned INI text the manager only reads.
/// </summary>
public static class ReservedKeys
{
    /// <summary>
    /// Command-line options the launch-argument builder emits itself. Compared case-insensitively against
    /// the part of a token before the first <c>=</c>. Typed <see cref="Domain.LaunchFlags"/> options are
    /// listed too so free text cannot contradict the typed field.
    /// </summary>
    public static readonly IReadOnlySet<string> CommandLineOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Manager-owned.
        "-port",
        "-clusterid",
        "-ClusterDirOverride",
        "-mods",
        "-WinLiveMaxPlayers",
        "-log",
        // Typed flags (use the typed field instead).
        "-NoBattlEye",
        "-ServerPlatform",
        "-exclusivejoin",
        "-NoWildBabies",
        "-PreventSpawnAnimations",
        "-UseStore",
        "-ConvertToStore",
        "-servergamelog",
        "-ServerGameLogIncludeTribeLogs",
        "-ServerRconOutputTribeLogs",
        "-ActiveEvent",
    };

    /// <summary>
    /// INI keys (and <c>?</c>-keys) the INI pipeline writes from instance fields. Compared case-insensitively
    /// against the key name with any Unreal array prefix (<c>+ - . !</c>) stripped, in every section of
    /// both files.
    /// </summary>
    public static readonly IReadOnlySet<string> IniKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AltSaveDirectoryName",
        "Port",
        "RCONPort",
        "RCONEnabled",
        "SessionName",
        "MaxPlayers",
    };

    /// <summary>The map-string delimiter; any free-text token containing it is rejected (delimiter injection).</summary>
    public const char MapStringDelimiter = '?';

    public static bool IsReservedCommandLineOption(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var separator = token.IndexOf('=');
        var name = separator < 0 ? token : token[..separator];
        return CommandLineOptions.Contains(name.Trim());
    }

    public static bool IsReservedIniKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = key.Trim().TrimStart('+', '-', '.', '!').Trim();
        return IniKeys.Contains(name);
    }

    /// <summary>
    /// Validates a typed value the manager writes verbatim into a command line or INI (session name, map
    /// key, cluster key, slug, ...). Returns the problem, or null when the value is safe. Typed values may
    /// never contain <c>?</c>, <c>=</c>, or line breaks (plan step 16).
    /// </summary>
    public static string? ValidateTypedValue(string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{fieldName} must not be empty.";
        }

        if (value.Contains(MapStringDelimiter))
        {
            return $"{fieldName} must not contain '{MapStringDelimiter}'.";
        }

        if (value.Contains('='))
        {
            return $"{fieldName} must not contain '='.";
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            return $"{fieldName} must not contain line breaks.";
        }

        return null;
    }
}
