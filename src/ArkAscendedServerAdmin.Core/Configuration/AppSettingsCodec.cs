using System.Globalization;

namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// Maps <see cref="AppSettings"/> to and from the key/value rows of the <c>AppSettings</c> table. Unknown
/// keys are ignored and missing or unparseable values fall back to the default, so a row set from any
/// schema version decodes to something usable.
/// </summary>
public static class AppSettingsCodec
{
    public static class Keys
    {
        public const string StaggerDelaySeconds = "StaggerDelaySeconds";
        public const string SteamCmdValidate = "SteamCmdValidate";
        public const string GamePortStart = "GamePortStart";
        public const string GamePortStep = "GamePortStep";
        public const string RconPortStart = "RconPortStart";
        public const string RconPortStep = "RconPortStep";
        public const string DefaultBackupIntervalMinutes = "DefaultBackupIntervalMinutes";
        public const string DefaultBackupRetention = "DefaultBackupRetention";
        public const string BackupQuiescenceSeconds = "BackupQuiescenceSeconds";
        public const string PreStopBroadcastMinutes = "PreStopBroadcastMinutes";
        public const string GracefulStopTimeoutSeconds = "GracefulStopTimeoutSeconds";
        public const string RconCommandTimeoutSeconds = "RconCommandTimeoutSeconds";
        public const string ConsoleBackfillLines = "ConsoleBackfillLines";
        public const string CurseForgeApiKey = "CurseForgeApiKey";
        public const string AdminWhitelist = "AdminWhitelist";

        public static readonly IReadOnlyList<string> All =
        [
            StaggerDelaySeconds, SteamCmdValidate, GamePortStart, GamePortStep, RconPortStart, RconPortStep,
            DefaultBackupIntervalMinutes, DefaultBackupRetention, BackupQuiescenceSeconds, PreStopBroadcastMinutes,
            GracefulStopTimeoutSeconds, RconCommandTimeoutSeconds, ConsoleBackfillLines, CurseForgeApiKey, AdminWhitelist,
        ];
    }

    public static IReadOnlyDictionary<string, string> Encode(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Keys.StaggerDelaySeconds] = Int(settings.StaggerDelaySeconds),
            [Keys.SteamCmdValidate] = settings.SteamCmdValidate ? "true" : "false",
            [Keys.GamePortStart] = Int(settings.GamePortStart),
            [Keys.GamePortStep] = Int(settings.GamePortStep),
            [Keys.RconPortStart] = Int(settings.RconPortStart),
            [Keys.RconPortStep] = Int(settings.RconPortStep),
            [Keys.DefaultBackupIntervalMinutes] = Int(settings.DefaultBackupIntervalMinutes),
            [Keys.DefaultBackupRetention] = Int(settings.DefaultBackupRetention),
            [Keys.BackupQuiescenceSeconds] = Int(settings.BackupQuiescenceSeconds),
            [Keys.PreStopBroadcastMinutes] = Int(settings.PreStopBroadcastMinutes),
            [Keys.GracefulStopTimeoutSeconds] = Int(settings.GracefulStopTimeoutSeconds),
            [Keys.RconCommandTimeoutSeconds] = Int(settings.RconCommandTimeoutSeconds),
            [Keys.ConsoleBackfillLines] = Int(settings.ConsoleBackfillLines),
            [Keys.CurseForgeApiKey] = settings.CurseForgeApiKey,
            [Keys.AdminWhitelist] = settings.AdminWhitelist,
        };
    }

    public static AppSettings Decode(IReadOnlyDictionary<string, string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var defaults = new AppSettings();

        return defaults with
        {
            StaggerDelaySeconds = GetInt(rows, Keys.StaggerDelaySeconds, defaults.StaggerDelaySeconds),
            SteamCmdValidate = GetBool(rows, Keys.SteamCmdValidate, defaults.SteamCmdValidate),
            GamePortStart = GetInt(rows, Keys.GamePortStart, defaults.GamePortStart),
            GamePortStep = GetInt(rows, Keys.GamePortStep, defaults.GamePortStep),
            RconPortStart = GetInt(rows, Keys.RconPortStart, defaults.RconPortStart),
            RconPortStep = GetInt(rows, Keys.RconPortStep, defaults.RconPortStep),
            DefaultBackupIntervalMinutes = GetInt(rows, Keys.DefaultBackupIntervalMinutes, defaults.DefaultBackupIntervalMinutes),
            DefaultBackupRetention = GetInt(rows, Keys.DefaultBackupRetention, defaults.DefaultBackupRetention),
            BackupQuiescenceSeconds = GetInt(rows, Keys.BackupQuiescenceSeconds, defaults.BackupQuiescenceSeconds),
            PreStopBroadcastMinutes = GetInt(rows, Keys.PreStopBroadcastMinutes, defaults.PreStopBroadcastMinutes),
            GracefulStopTimeoutSeconds = GetInt(rows, Keys.GracefulStopTimeoutSeconds, defaults.GracefulStopTimeoutSeconds),
            RconCommandTimeoutSeconds = GetInt(rows, Keys.RconCommandTimeoutSeconds, defaults.RconCommandTimeoutSeconds),
            ConsoleBackfillLines = GetInt(rows, Keys.ConsoleBackfillLines, defaults.ConsoleBackfillLines),
            CurseForgeApiKey = rows.TryGetValue(Keys.CurseForgeApiKey, out var key) ? key.Trim() : defaults.CurseForgeApiKey,
            AdminWhitelist = rows.TryGetValue(Keys.AdminWhitelist, out var whitelist) ? whitelist : defaults.AdminWhitelist,
        };
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int GetInt(IReadOnlyDictionary<string, string> rows, string key, int fallback) =>
        rows.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> rows, string key, bool fallback) =>
        rows.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;
}
