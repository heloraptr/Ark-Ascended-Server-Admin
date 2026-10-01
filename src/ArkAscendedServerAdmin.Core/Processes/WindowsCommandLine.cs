using System.Text;

namespace ArkAscendedServerAdmin.Processes;

/// <summary>
/// Joins an executable and its arguments into one <c>CreateProcess</c> command line, quoted so that
/// <c>CommandLineToArgvW</c> and the MSVC runtime split it back into the same arguments: an argument is
/// wrapped in quotes when it is empty or contains whitespace or a quote, embedded quotes are escaped,
/// and backslashes are doubled only where they precede a quote. This is what
/// <c>ProcessStartInfo.ArgumentList</c> does internally, for callers that start a process by hand.
/// </summary>
public static class WindowsCommandLine
{
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();

        // The program name is parsed by different rules (no escapes), so it is only ever wrapped in quotes.
        if (fileName.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("An executable path cannot contain a quote.", nameof(fileName));
        }

        builder.Append('"').Append(fileName).Append('"');
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are escapes: double them, then escape the quote itself.
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(c);
        }

        // Backslashes before the closing quote must be doubled so they do not escape it.
        builder.Append('\\', backslashes * 2).Append('"');
    }
}
