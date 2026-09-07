using System.Text;

namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Tokenizer and save-time validator for the free-text additional arguments (plan step 17). Tokens are
/// whitespace separated; double quotes group a token and are stripped (<c>-foo="a b"</c> becomes the
/// single token <c>-foo=a b</c>). There is no escape sequence: a literal double quote cannot be passed.
/// Every token is checked against <see cref="ReservedKeys"/> so free text can neither contradict a
/// manager-owned option nor inject a <c>?</c> map parameter.
/// </summary>
public static class AdditionalArgs
{
    /// <summary>
    /// The subset of <see cref="ReservedKeys.CommandLineOptions"/> that has a typed field on
    /// <see cref="Domain.LaunchFlags"/>; the rejection message points the owner at the typed option.
    /// </summary>
    public static readonly IReadOnlySet<string> TypedFlagOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
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

    /// <summary>Splits <paramref name="text"/> into tokens; null or blank text yields no tokens.</summary>
    /// <exception cref="FormatException">The text contains an unbalanced double quote.</exception>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var inQuotes = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
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

        if (inQuotes)
        {
            throw new FormatException("Additional arguments contain an unbalanced double quote.");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Returns one sentence per problem (empty when the text is acceptable). Save-time callers show these
    /// verbatim, so each names the offending token.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? text)
    {
        IReadOnlyList<string> tokens;
        try
        {
            tokens = Tokenize(text);
        }
        catch (FormatException ex)
        {
            return [ex.Message];
        }

        var problems = new List<string>();
        foreach (var token in tokens)
        {
            var problem = ValidateToken(token);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    private static string? ValidateToken(string token)
    {
        if (token.Length == 0 || token == "-")
        {
            return $"'{token}' is not a valid argument.";
        }

        if (ReservedKeys.IsReservedCommandLineOption(token))
        {
            return IsTypedFlag(token)
                ? $"'{token}' has a typed launch option; use the typed option instead of additional arguments."
                : $"'{token}' is managed by the manager; set it in the instance instead.";
        }

        if (token.Contains(ReservedKeys.MapStringDelimiter))
        {
            return $"'{token}' contains '{ReservedKeys.MapStringDelimiter}'; map parameters belong in the INI, not in additional arguments.";
        }

        return null;
    }

    private static bool IsTypedFlag(string token)
    {
        var separator = token.IndexOf('=');
        var name = separator < 0 ? token : token[..separator];
        return TypedFlagOptions.Contains(name.Trim());
    }
}
