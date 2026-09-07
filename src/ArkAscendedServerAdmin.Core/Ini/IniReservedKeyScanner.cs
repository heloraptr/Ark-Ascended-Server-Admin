using ArkAscendedServerAdmin.Launch;

namespace ArkAscendedServerAdmin.Ini;

/// <summary>
/// Finds manager-owned keys (<see cref="ReservedKeys.IniKeys"/>) in source INI text so the editor can warn
/// "these keys will be replaced by the manager" before the user saves (plan step 16).
/// </summary>
public static class IniReservedKeyScanner
{
    public static IReadOnlyList<IniReservedKeyHit> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var hits = new List<IniReservedKeyHit>();
        foreach (var (lineNumber, section, keyText, key, _) in IniText.Parse(text).EnumerateKeyValues())
        {
            if (ReservedKeys.IsReservedIniKey(key))
            {
                hits.Add(new IniReservedKeyHit(lineNumber, section, keyText));
            }
        }

        return hits;
    }
}

/// <summary>
/// A reserved key found in source text. <paramref name="Key"/> is the key as written, including any array
/// prefix; <paramref name="Section"/> is empty for lines before the first header.
/// </summary>
public sealed record IniReservedKeyHit(int LineNumber, string Section, string Key);
