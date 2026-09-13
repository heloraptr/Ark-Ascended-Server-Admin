using System.Globalization;

namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Builds the dedicated-server command line (plan step 17). The result is an argument list for
/// <c>ProcessStartInfo.ArgumentList</c>; nothing here is shell-quoted.
/// <para>
/// Only what the game refuses to read from the INI goes on the command line: <c>-port=</c> is mandatory
/// because the INI <c>Port</c> is ignored, and <c>AltSaveDirectoryName</c> rides in the map string as a
/// <c>?</c>-token because it is the re-attach key (plan step 21). <c>SessionName</c>, <c>RCONPort</c>,
/// <c>RCONEnabled</c>, and <c>MaxPlayers</c> also reach the game through the generated INI (plan step 16);
/// <c>-WinLiveMaxPlayers</c> is the authoritative player cap: ASA ignores the INI <c>MaxPlayers</c> and
/// resets it to the default (ark.wiki.gg, Server configuration: "This currently replaces the MaxPlayers
/// setting from the GameUserSettings.ini option"), so the flag is emitted from the same field.
/// </para>
/// <para>
/// Output order: map string, <c>-port</c>, <c>-WinLiveMaxPlayers</c>, cluster options, <c>-mods</c>,
/// logging flags, <c>-NoBattlEye</c>, the remaining typed flags, then the free-text additional arguments
/// verbatim. No <c>-stdout</c> / <c>-FullStdOutLogOutput</c>: the <c>ShooterGame.log</c> tail is the
/// console's output source.
/// </para>
/// </summary>
public static class LaunchArgumentBuilder
{
    public const int MinGamePort = 1;
    public const int MaxGamePort = 65534;
    public const int MinPlayers = 1;
    public const int MaxPlayers = 500;

    /// <summary>Validates every field of <paramref name="request"/>, then emits the arguments.</summary>
    /// <exception cref="LaunchValidationException">One or more fields would produce an unsafe or invalid command line.</exception>
    public static LaunchArguments Build(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ClusterModIds);
        ArgumentNullException.ThrowIfNull(request.InstanceModIds);
        ArgumentNullException.ThrowIfNull(request.Flags);

        var problems = Validate(request);
        if (problems.Count > 0)
        {
            throw new LaunchValidationException(problems);
        }

        var flags = request.Flags;
        var arguments = new List<string>
        {
            string.Concat(request.MapKey, ReservedKeys.MapStringDelimiter, "listen", ReservedKeys.MapStringDelimiter, "AltSaveDirectoryName=", request.Slug),
            $"-port={request.GamePort.ToString(CultureInfo.InvariantCulture)}",
            $"-WinLiveMaxPlayers={request.MaxPlayers.ToString(CultureInfo.InvariantCulture)}",
        };

        if (request.ClusterKey is not null)
        {
            arguments.Add($"-clusterid={request.ClusterKey}");
            arguments.Add($"-ClusterDirOverride={request.ClusterDirectory}");
        }

        var modIds = request.ClusterModIds.Concat(request.InstanceModIds).Distinct().ToList();
        if (modIds.Count > 0)
        {
            arguments.Add("-mods=" + string.Join(',', modIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        }

        arguments.Add("-log");
        if (flags.ServerGameLog != false)
        {
            arguments.Add("-servergamelog");
        }

        AddIfTrue(arguments, flags.ServerGameLogIncludeTribeLogs, "-ServerGameLogIncludeTribeLogs");
        AddIfTrue(arguments, flags.ServerRconOutputTribeLogs, "-ServerRconOutputTribeLogs");

        if (flags.NoBattlEye != false)
        {
            arguments.Add("-NoBattlEye");
        }

        AddIfTrue(arguments, flags.ExclusiveJoin, "-exclusivejoin");
        AddIfTrue(arguments, flags.NoWildBabies, "-NoWildBabies");
        AddIfTrue(arguments, flags.PreventSpawnAnimations, "-PreventSpawnAnimations");
        AddIfTrue(arguments, flags.UseStore, "-UseStore");
        AddIfTrue(arguments, flags.ConvertToStore, "-ConvertToStore");
        AddIfSet(arguments, flags.ServerPlatform, "-ServerPlatform");
        AddIfSet(arguments, flags.ActiveEvent, "-ActiveEvent");

        arguments.AddRange(AdditionalArgs.Tokenize(flags.AdditionalArgs));

        return new LaunchArguments(arguments);
    }

    private static List<string> Validate(LaunchRequest request)
    {
        var problems = new List<string>();

        AddTypedValueProblems(problems, "Map key", request.MapKey);
        AddTypedValueProblems(problems, "Slug", request.Slug);

        if (request.GamePort is < MinGamePort or > MaxGamePort)
        {
            problems.Add($"Game port {request.GamePort} must be between {MinGamePort} and {MaxGamePort}.");
        }

        if (request.MaxPlayers is < MinPlayers or > MaxPlayers)
        {
            problems.Add($"Max players {request.MaxPlayers} must be between {MinPlayers} and {MaxPlayers}.");
        }

        if (request.ClusterKey is null != request.ClusterDirectory is null)
        {
            problems.Add("Cluster key and cluster directory must both be set (clustered) or both be empty (standalone).");
        }
        else if (request.ClusterKey is not null)
        {
            AddTypedValueProblems(problems, "Cluster key", request.ClusterKey);
            if (string.IsNullOrWhiteSpace(request.ClusterDirectory))
            {
                problems.Add("Cluster directory must not be empty.");
            }
        }

        foreach (var modId in request.ClusterModIds.Concat(request.InstanceModIds).Where(id => id <= 0).Distinct())
        {
            problems.Add($"Mod id {modId} is not a valid CurseForge project id.");
        }

        if (!string.IsNullOrWhiteSpace(request.Flags.ServerPlatform))
        {
            AddTypedValueProblems(problems, "Server platform", request.Flags.ServerPlatform);
        }

        if (!string.IsNullOrWhiteSpace(request.Flags.ActiveEvent))
        {
            AddTypedValueProblems(problems, "Active event", request.Flags.ActiveEvent);
        }

        problems.AddRange(AdditionalArgs.Validate(request.Flags.AdditionalArgs));

        return problems;
    }

    /// <summary>Applies <see cref="ReservedKeys.ValidateTypedValue"/> plus the no-whitespace rule for values that become single tokens.</summary>
    private static void AddTypedValueProblems(List<string> problems, string fieldName, string? value)
    {
        var problem = ReservedKeys.ValidateTypedValue(fieldName, value);
        if (problem is not null)
        {
            problems.Add(problem);
        }
        else if (value!.Any(char.IsWhiteSpace))
        {
            problems.Add($"{fieldName} must not contain whitespace.");
        }
    }

    private static void AddIfTrue(List<string> arguments, bool? flag, string option)
    {
        if (flag == true)
        {
            arguments.Add(option);
        }
    }

    private static void AddIfSet(List<string> arguments, string? value, string option)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add($"{option}={value}");
        }
    }
}
