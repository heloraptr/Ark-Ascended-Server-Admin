using System.Globalization;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.Ini;

/// <summary>Everything the generator needs: the two source texts plus the instance-level values the manager owns.</summary>
/// <param name="GameIniSource">Source <c>Game.ini</c> text (cluster or instance level, resolved by the caller).</param>
/// <param name="GameUserSettingsSource">Source <c>GameUserSettings.ini</c> text.</param>
/// <param name="SessionName">Written to <c>[SessionSettings] SessionName</c>.</param>
/// <param name="GamePort">Written to <c>[SessionSettings] Port</c>; the effective game port is still the <c>-port=</c> argument.</param>
/// <param name="RconPort">Written to <c>[ServerSettings] RCONPort</c>.</param>
/// <param name="MaxPlayers">Written to <c>[/Script/Engine.GameSession] MaxPlayers</c>.</param>
/// <param name="Overrides">Per-instance <c>[Section] Key=Value</c> lines applied on top of the source text.</param>
/// <param name="ClusterAdminWhitelist">Cluster-level <c>AllowedCheaterAccountIDs</c> text, one EOS id per line (DESIGN §12).</param>
/// <param name="InstanceAdminWhitelist">Instance-level whitelist text in the same shape; the union is written to the instance.</param>
/// <param name="ManagerAdminWhitelist">The App Settings whitelist (every instance gets it); listed first in the union.</param>
public sealed record GenerationInput(
    string GameIniSource,
    string GameUserSettingsSource,
    string SessionName,
    int GamePort,
    int RconPort,
    int MaxPlayers,
    IReadOnlyList<IniOverrideSpec> Overrides,
    string ClusterAdminWhitelist,
    string InstanceAdminWhitelist,
    string ManagerAdminWhitelist = "");

/// <summary>The generated file texts, ready for Phase 4 to write under <c>Saved\Config\WindowsServer</c>.</summary>
/// <param name="GameIni">Generated <c>Game.ini</c> text.</param>
/// <param name="GameUserSettingsIni">Generated <c>GameUserSettings.ini</c> text.</param>
/// <param name="AdminWhitelist">Union of manager, cluster, and instance whitelist lines, deduplicated, order preserved.</param>
/// <param name="ServerAdminPassword">Read back from the generated GameUserSettings.ini; null when missing or empty (Start is refused, plan step 23).</param>
/// <param name="Warnings">Content problems that were handled rather than fatal: replaced reserved keys, skipped overrides.</param>
public sealed record GeneratedConfig(
    string GameIni,
    string GameUserSettingsIni,
    IReadOnlyList<string> AdminWhitelist,
    string? ServerAdminPassword,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The pure part of the INI pipeline (plan step 16): source text plus instance fields in, generated
/// <c>Game.ini</c> and <c>GameUserSettings.ini</c> text out. Manager-owned keys are authoritative: every
/// occurrence of a <see cref="ReservedKeys.IniKeys"/> entry in either source is removed (with a warning)
/// and the instance values are written in their canonical sections. Only invalid typed values throw;
/// every other content problem becomes a warning. File I/O, locking and the DB mirror are Phase 4.
/// </summary>
public static class IniGenerator
{
    public const string SessionSettingsSection = "SessionSettings";
    public const string ServerSettingsSection = "ServerSettings";
    public const string GameSessionSection = "/Script/Engine.GameSession";

    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int MinPlayers = 1;
    public const int MaxPlayersLimit = 500;

    /// <exception cref="ArgumentException">A typed value is invalid; the message lists every problem.</exception>
    public static GeneratedConfig Generate(GenerationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.GameIniSource);
        ArgumentNullException.ThrowIfNull(input.GameUserSettingsSource);
        ArgumentNullException.ThrowIfNull(input.Overrides);
        ArgumentNullException.ThrowIfNull(input.ClusterAdminWhitelist);
        ArgumentNullException.ThrowIfNull(input.InstanceAdminWhitelist);
        ArgumentNullException.ThrowIfNull(input.ManagerAdminWhitelist);

        // 1. Typed values.
        var problems = ValidateTypedValues(input);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(input));
        }

        // 2. Parse.
        var game = IniText.Parse(input.GameIniSource);
        var gameUserSettings = IniText.Parse(input.GameUserSettingsSource);
        var warnings = new List<string>();

        // 3. Overrides.
        foreach (var o in input.Overrides)
        {
            var fileName = FileName(o.File);
            var shapeProblems = IniOverrideValidator.ValidateShape(o.Section, o.Key, o.Value);
            if (shapeProblems.Count > 0)
            {
                warnings.Add($"Override [{o.Section}] {o.Key} for {fileName} was skipped: {string.Join(" ", shapeProblems)}");
                continue;
            }

            if (ReservedKeys.IsReservedIniKey(o.Key))
            {
                warnings.Add($"Override [{o.Section}] {o.Key} for {fileName} was skipped: the key is reserved and written by the manager.");
                continue;
            }

            Target(o.File, game, gameUserSettings).Set(o.Section, o.Key, o.Value);
        }

        // 4. Reserved keys in both files.
        foreach (var key in ReservedKeys.IniKeys)
        {
            foreach (var removal in gameUserSettings.RemoveKey(key))
            {
                warnings.Add($"Reserved key '{removal.Key}' in {Describe(removal.Section)} of GameUserSettings.ini (line {removal.LineNumber}) was replaced by the manager value.");
            }

            foreach (var removal in game.RemoveKey(key))
            {
                warnings.Add($"Reserved key '{removal.Key}' in {Describe(removal.Section)} of Game.ini (line {removal.LineNumber}) was removed; the manager writes it in GameUserSettings.ini.");
            }
        }

        // 5. Manager keys. Spike result: the game honors RCONPort / RCONEnabled from the INI but ignores
        // Port (the effective game port is the -port= argument). Port is written anyway so the file agrees
        // with the launch line.
        gameUserSettings.Set(SessionSettingsSection, "SessionName", input.SessionName.Trim());
        gameUserSettings.Set(SessionSettingsSection, "Port", Invariant(input.GamePort));
        gameUserSettings.Set(ServerSettingsSection, "RCONEnabled", "True");
        gameUserSettings.Set(ServerSettingsSection, "RCONPort", Invariant(input.RconPort));
        gameUserSettings.Set(GameSessionSection, "MaxPlayers", Invariant(input.MaxPlayers));

        // 6. ServerAdminPassword is user-owned; read it back from the text that will actually be written.
        var password = gameUserSettings.Get(ServerSettingsSection, "ServerAdminPassword")?.Trim();
        if (string.IsNullOrEmpty(password))
        {
            password = null;
        }

        // 7. Whitelist union.
        var whitelist = UnionWhitelist(input.ManagerAdminWhitelist, input.ClusterAdminWhitelist, input.InstanceAdminWhitelist);

        return new GeneratedConfig(game.ToString(), gameUserSettings.ToString(), whitelist, password, warnings);
    }

    /// <summary>
    /// The lists in the order given (manager, cluster, instance), trimmed, blanks and <c>;</c>/<c>#</c>
    /// comment lines skipped, deduplicated (ordinal) with first-seen order preserved.
    /// </summary>
    public static IReadOnlyList<string> UnionWhitelist(params string[] whitelists)
    {
        ArgumentNullException.ThrowIfNull(whitelists);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var text in whitelists)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }

                if (seen.Add(line))
                {
                    result.Add(line);
                }
            }
        }

        return result;
    }

    private static List<string> ValidateTypedValues(GenerationInput input)
    {
        var problems = new List<string>();

        if (ReservedKeys.ValidateTypedValue("SessionName", input.SessionName) is { } sessionProblem)
        {
            problems.Add(sessionProblem);
        }

        if (input.GamePort is < MinPort or > MaxPort)
        {
            problems.Add($"GamePort must be between {MinPort} and {MaxPort}.");
        }

        if (input.RconPort is < MinPort or > MaxPort)
        {
            problems.Add($"RconPort must be between {MinPort} and {MaxPort}.");
        }

        if (input.MaxPlayers is < MinPlayers or > MaxPlayersLimit)
        {
            problems.Add($"MaxPlayers must be between {MinPlayers} and {MaxPlayersLimit}.");
        }

        return problems;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static IniText Target(IniFile file, IniText game, IniText gameUserSettings) =>
        file == IniFile.Game ? game : gameUserSettings;

    private static string FileName(IniFile file) => file == IniFile.Game ? "Game.ini" : "GameUserSettings.ini";

    private static string Describe(string section) => section.Length == 0 ? "the preamble before the first section" : $"[{section}]";
}
