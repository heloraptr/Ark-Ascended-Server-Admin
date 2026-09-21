namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// App Settings: the runtime-editable values stored in the database (one row per key, see
/// <see cref="AppSettingsCodec"/>). Every property has the MVP default from the plan so a fresh database
/// is usable without touching the Settings page.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Delay between consecutive game-server launches from the global launch queue.</summary>
    public int StaggerDelaySeconds { get; init; } = 30;

    /// <summary>Pass <c>validate</c> to SteamCMD on install/update.</summary>
    public bool SteamCmdValidate { get; init; }

    public int GamePortStart { get; init; } = 7777;

    /// <summary>ASA uses the game port and port+1, so consecutive instances step by two.</summary>
    public int GamePortStep { get; init; } = 2;

    public int RconPortStart { get; init; } = 27020;

    public int RconPortStep { get; init; } = 1;

    public int DefaultBackupIntervalMinutes { get; init; } = 30;

    public int DefaultBackupRetention { get; init; } = 10;

    /// <summary>How long a backup waits for the world files to settle after <c>saveworld</c>.</summary>
    public int BackupQuiescenceSeconds { get; init; } = 10;

    /// <summary>Broadcast countdown before a stop; zero skips the countdown.</summary>
    public int PreStopBroadcastMinutes { get; init; } = 1;

    /// <summary>How long to wait for the process to exit after <c>doexit</c> before killing it.</summary>
    public int GracefulStopTimeoutSeconds { get; init; } = 60;

    public int RconCommandTimeoutSeconds { get; init; } = 10;

    /// <summary>Lines read from <c>ShooterGame.log</c> into the console when re-attaching.</summary>
    public int ConsoleBackfillLines { get; init; } = 200;

    /// <summary>Stored in plain text by design (read-only key on the owner's box).</summary>
    public string CurseForgeApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The manager-wide admin whitelist, one EOS id per line. Unioned into every instance.s
    /// <c>AllowedCheaterAccountIDs.txt</c> ahead of the cluster and instance lists, so the owner enters
    /// themselves once; the per-instance editors show these ids locked.
    /// </summary>
    public string AdminWhitelist { get; init; } = string.Empty;

    /// <summary>
    /// The host name or IP address players outside the LAN connect to (B9): what they type after
    /// <c>open</c>, so a DNS name or the router's public address. Empty on a LAN-only box; the instance page
    /// then shows only the box's own addresses. Free text, never resolved or contacted by the app.
    /// </summary>
    public string PublicAddress { get; init; } = string.Empty;

    /// <summary>The longest host name DNS allows; caps <see cref="PublicAddress"/>.</summary>
    public const int PublicAddressMaxLength = 253;

    /// <summary>
    /// How many times the row set has been written (B0). A loaded snapshot carries it and <c>SaveAsync</c> refuses a
    /// snapshot whose version is behind the store's, so two editors cannot overwrite each other; <c>UpdateAsync</c>
    /// mutations bump it on every actual write. Not user-editable and never validated.
    /// </summary>
    public long Version { get; init; }

    /// <summary>Returns the validation problems, or an empty list when the settings are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        RequireRange(errors, nameof(StaggerDelaySeconds), StaggerDelaySeconds, 0, 3600);
        RequireRange(errors, nameof(GamePortStart), GamePortStart, 1, 65535);
        RequireRange(errors, nameof(GamePortStep), GamePortStep, 1, 1000);
        RequireRange(errors, nameof(RconPortStart), RconPortStart, 1, 65535);
        RequireRange(errors, nameof(RconPortStep), RconPortStep, 1, 1000);
        RequireRange(errors, nameof(DefaultBackupIntervalMinutes), DefaultBackupIntervalMinutes, 1, 10080);
        RequireRange(errors, nameof(DefaultBackupRetention), DefaultBackupRetention, 1, 1000);
        RequireRange(errors, nameof(BackupQuiescenceSeconds), BackupQuiescenceSeconds, 1, 600);
        RequireRange(errors, nameof(PreStopBroadcastMinutes), PreStopBroadcastMinutes, 0, 60);
        RequireRange(errors, nameof(GracefulStopTimeoutSeconds), GracefulStopTimeoutSeconds, 5, 3600);
        RequireRange(errors, nameof(RconCommandTimeoutSeconds), RconCommandTimeoutSeconds, 1, 300);
        RequireRange(errors, nameof(ConsoleBackfillLines), ConsoleBackfillLines, 0, 5000);

        if (CurseForgeApiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            errors.Add($"{nameof(CurseForgeApiKey)} must not contain whitespace or control characters.");
        }

        if (AdminWhitelist.Split('\n').Select(l => l.Trim()).Any(l => l.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
        {
            errors.Add($"{nameof(AdminWhitelist)} must hold one id per line with no spaces.");
        }

        if (PublicAddress.Length > PublicAddressMaxLength)
        {
            errors.Add($"{nameof(PublicAddress)} must be at most {PublicAddressMaxLength} characters (was {PublicAddress.Length}).");
        }
        else if (PublicAddress.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            errors.Add($"{nameof(PublicAddress)} must be one host name or IP address with no spaces.");
        }
        else if (PublicAddress.Contains("://", StringComparison.Ordinal))
        {
            errors.Add($"{nameof(PublicAddress)} must not include a scheme such as steam:// or https://.");
        }

        return errors;
    }

    private static void RequireRange(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name} must be between {min} and {max} (was {value}).");
        }
    }
}
