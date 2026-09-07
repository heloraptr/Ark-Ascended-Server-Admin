using System.Text;

namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Pure string work over a process command line as reported by WMI <c>Win32_Process.CommandLine</c>
/// (plan step 21). The manager re-attaches to a running server by matching the <b>exact</b>
/// <c>AltSaveDirectoryName=&lt;slug&gt;</c> token of the map string; a prefix or substring match would
/// confuse <c>alpha</c> with <c>alpha2</c>.
/// </summary>
public static class LaunchCommandLine
{
    private const string AltSaveDirectoryNameKey = "AltSaveDirectoryName";
    private const string ExecutableExtension = ".exe";

    /// <summary>
    /// Returns the <c>?</c>-tokens of the first argument after the executable, without the map name
    /// (<c>["listen", "AltSaveDirectoryName=alpha"]</c>). Empty when there is no map string. The executable
    /// may be quoted or unquoted; an unquoted path containing spaces is recognized by its <c>.exe</c> suffix.
    /// </summary>
    public static IReadOnlyList<string> ParseMapTokens(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var arguments = SplitArguments(commandLine);
        if (arguments.Count == 0)
        {
            return [];
        }

        var parts = arguments[0].Split(ReservedKeys.MapStringDelimiter);
        return parts.Length <= 1 ? [] : parts[1..];
    }

    /// <summary>
    /// The value of the <c>AltSaveDirectoryName=</c> map token (key compared case-insensitively, value
    /// returned verbatim), or <see langword="null"/> when the token is absent or has an empty value.
    /// </summary>
    public static string? TryGetAltSaveDirectoryName(string commandLine)
    {
        foreach (var token in ParseMapTokens(commandLine))
        {
            var separator = token.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            if (token.AsSpan(0, separator).Equals(AltSaveDirectoryNameKey, StringComparison.OrdinalIgnoreCase))
            {
                var value = token[(separator + 1)..];
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>True when the command line's <c>AltSaveDirectoryName</c> value equals <paramref name="slug"/> ordinally.</summary>
    public static bool MatchesSlug(string commandLine, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return string.Equals(TryGetAltSaveDirectoryName(commandLine), slug, StringComparison.Ordinal);
    }

    /// <summary>Splits the arguments that follow the executable; a double-quoted argument is one token with the quotes stripped.</summary>
    private static List<string> SplitArguments(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var inQuotes = false;

        for (var position = SkipExecutable(commandLine); position < commandLine.Length; position++)
        {
            var c = commandLine[position];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (inToken)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>Returns the index just past the executable path (quoted, <c>.exe</c>-terminated, or the first whitespace-delimited word).</summary>
    private static int SkipExecutable(string commandLine)
    {
        var start = 0;
        while (start < commandLine.Length && char.IsWhiteSpace(commandLine[start]))
        {
            start++;
        }

        if (start >= commandLine.Length)
        {
            return start;
        }

        if (commandLine[start] == '"')
        {
            var closing = commandLine.IndexOf('"', start + 1);
            return closing < 0 ? commandLine.Length : closing + 1;
        }

        var exeIndex = commandLine.IndexOf(ExecutableExtension, start, StringComparison.OrdinalIgnoreCase);
        while (exeIndex >= 0)
        {
            var end = exeIndex + ExecutableExtension.Length;
            if (end >= commandLine.Length || char.IsWhiteSpace(commandLine[end]))
            {
                return end;
            }

            exeIndex = commandLine.IndexOf(ExecutableExtension, end, StringComparison.OrdinalIgnoreCase);
        }

        var space = start;
        while (space < commandLine.Length && !char.IsWhiteSpace(commandLine[space]))
        {
            space++;
        }

        return space;
    }
}
