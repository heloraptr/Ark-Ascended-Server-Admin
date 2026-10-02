namespace ArkAscendedServerAdmin.Configuration;

/// <summary>
/// The one rule for every admin whitelist, whether the manager-wide list in <see cref="AppSettings"/> or a cluster's
/// or instance's own: one EOS id per line, each line trimmed, blank lines dropped, and a line that still holds
/// whitespace or a control character refused. Sharing it keeps the three editors from accepting different input.
/// </summary>
public static class AdminWhitelistText
{
    /// <summary>The refusal for a line with inner whitespace or a control character.</summary>
    public const string InvalidLineError = $"{nameof(AppSettings.AdminWhitelist)} must hold one id per line with no spaces.";

    /// <summary>The trimmed, non-empty lines of <paramref name="text"/>.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The lines joined with CRLF, the shape the whitelist is stored in.</summary>
    public static string Normalize(string? text) => string.Join("\r\n", Lines(text));

    /// <summary><see cref="InvalidLineError"/> when any line holds whitespace or a control character; otherwise null.</summary>
    public static string? Validate(string? text) =>
        Lines(text).Any(line => line.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) ? InvalidLineError : null;
}
