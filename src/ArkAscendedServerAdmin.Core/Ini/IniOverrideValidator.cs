using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.Ini;

/// <summary>
/// Save-time validation of an <see cref="Domain.ExtraOverride"/> (plan step 16): the shape must survive
/// being written as <c>[Section]</c> / <c>Key=Value</c> lines, and the key must not be one the manager
/// owns (<see cref="ReservedKeys.IniKeys"/>). Used by the UI (plan step 27) and defensively by
/// <see cref="IniGenerator"/>.
/// </summary>
public static class IniOverrideValidator
{
    /// <summary>Returns every problem with the override; empty when it is acceptable.</summary>
    public static IReadOnlyList<string> Validate(string? section, string? key, string? value)
    {
        var problems = new List<string>(ValidateShape(section, key, value));

        if (!string.IsNullOrWhiteSpace(key) && ReservedKeys.IsReservedIniKey(key))
        {
            problems.Add($"Key '{key.Trim()}' is reserved: the manager writes it from the instance settings.");
        }

        return problems;
    }

    /// <summary>Shape checks only (no reserved-key policy), shared with <see cref="IniText.Set"/>.</summary>
    internal static IReadOnlyList<string> ValidateShape(string? section, string? key, string? value)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(section))
        {
            problems.Add("Section is required.");
        }
        else
        {
            if (section.Contains('[') || section.Contains(']'))
            {
                problems.Add("Section must not contain '[' or ']'.");
            }

            if (HasLineBreak(section))
            {
                problems.Add("Section must not contain line breaks.");
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            problems.Add("Key is required.");
        }
        else
        {
            if (key.Contains('='))
            {
                problems.Add("Key must not contain '='.");
            }

            if (key.Contains('[') || key.Contains(']'))
            {
                problems.Add("Key must not contain '[' or ']'.");
            }

            if (HasLineBreak(key))
            {
                problems.Add("Key must not contain line breaks.");
            }
            else if (IniText.NormalizeKey(key).Length == 0)
            {
                problems.Add("Key must contain more than an array prefix.");
            }
        }

        if (value is not null && HasLineBreak(value))
        {
            problems.Add("Value must not contain line breaks.");
        }

        return problems;
    }

    private static bool HasLineBreak(string text) => text.Contains('\r') || text.Contains('\n');
}
