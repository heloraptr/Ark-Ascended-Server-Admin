using System.Text;

namespace ArkAscendedServerAdmin.Ini;

/// <summary>
/// Line-preserving model of an Unreal INI file (plan step 16). Unreal INI files are not dictionaries:
/// a section may repeat a key many times (<c>ConfigOverrideItemMaxQuantity=(...)</c>), keys carry array
/// operator prefixes (<c>+ - . !</c>), values contain <c>=</c>, parentheses and quotes, and comments,
/// blank lines and stray text all matter to the person editing the file. Every line is kept verbatim
/// unless it is the specific line an edit targets; <see cref="ToString"/> reproduces untouched lines
/// byte-for-byte with <c>\r\n</c> endings. Section and key comparisons are case-insensitive and key
/// comparisons ignore array prefixes.
/// </summary>
public sealed class IniText
{
    /// <summary>U+FEFF, which the game prepends to files it writes.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    private static readonly char[] _prefixChars = ['+', '-', '.', '!'];

    private readonly List<Line> _lines;

    private IniText(List<Line> lines)
    {
        _lines = lines;
    }

    /// <summary>Distinct section names in order of first appearance, as written in their headers.</summary>
    public IReadOnlyList<string> Sections
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var line in _lines)
            {
                if (line.Kind == LineKind.Header && seen.Add(line.Section))
                {
                    result.Add(line.Section);
                }
            }

            return result;
        }
    }

    /// <summary>Parses INI text. Accepts <c>\r\n</c> or <c>\n</c> line endings and strips a leading UTF-8 BOM.</summary>
    public static IniText Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            text = text[1..];
        }

        var lines = new List<Line>();
        if (text.Length == 0)
        {
            return new IniText(lines);
        }

        var segments = text.Split('\n');
        var count = segments.Length;

        // A trailing newline produces one empty trailing segment; it is the line terminator, not a line.
        if (count > 0 && segments[count - 1].Length == 0)
        {
            count--;
        }

        var section = string.Empty;
        for (var i = 0; i < count; i++)
        {
            var raw = segments[i];
            if (raw.EndsWith('\r'))
            {
                raw = raw[..^1];
            }

            var line = Classify(raw, section, i + 1);
            if (line.Kind == LineKind.Header)
            {
                section = line.Section;
            }

            lines.Add(line);
        }

        return new IniText(lines);
    }

    /// <summary>
    /// Returns the value of the first <c>Key=Value</c> line in the section (case-insensitive on both,
    /// array prefixes on the key ignored), trimmed, or null when absent.
    /// </summary>
    public string? Get(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);

        var sectionName = section.Trim();
        var keyName = NormalizeKey(key);

        foreach (var line in _lines)
        {
            if (line.Kind == LineKind.KeyValue && SectionEquals(line.Section, sectionName) && KeyEquals(line.Key, keyName))
            {
                return line.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes <c>Key=Value</c> into the section: the first existing occurrence of the key is replaced in
    /// place (keeping its position) and any further occurrences in that section are removed; when the key
    /// is absent the line is appended after the section's last non-blank line; when the section is absent
    /// it is created at the end of the file, preceded by a blank line. The key is written exactly as
    /// given, so pass an array prefix (<c>+Key</c>) only when you mean one.
    /// </summary>
    /// <exception cref="ArgumentException">The section or key cannot be written as a well-formed line.</exception>
    public void Set(string section, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var problems = IniOverrideValidator.ValidateShape(section, key, value);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }

        var sectionName = section.Trim();
        var keyText = key.Trim();
        var keyName = NormalizeKey(keyText);
        var raw = $"{keyText}={value}";

        var replaced = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Kind != LineKind.KeyValue || !SectionEquals(line.Section, sectionName) || !KeyEquals(line.Key, keyName))
            {
                continue;
            }

            if (replaced)
            {
                _lines.RemoveAt(i);
                i--;
                continue;
            }

            _lines[i] = new Line(raw, LineKind.KeyValue, line.Section, keyText, keyName, value.Trim(), line.OriginalLineNumber);
            replaced = true;
        }

        if (replaced)
        {
            return;
        }

        var headerIndex = _lines.FindIndex(l => l.Kind == LineKind.Header && SectionEquals(l.Section, sectionName));
        if (headerIndex < 0)
        {
            if (_lines.Count > 0 && !_lines[^1].IsBlank)
            {
                _lines.Add(new Line(string.Empty, LineKind.Other, sectionName, null, null, null, null));
            }

            _lines.Add(new Line($"[{sectionName}]", LineKind.Header, sectionName, null, null, null, null));
            _lines.Add(new Line(raw, LineKind.KeyValue, sectionName, keyText, keyName, value.Trim(), null));
            return;
        }

        // Insert after the last non-blank line of the section's first block; trailing blank lines stay after it.
        var insertAt = headerIndex + 1;
        for (var i = headerIndex + 1; i < _lines.Count && _lines[i].Kind != LineKind.Header; i++)
        {
            if (!_lines[i].IsBlank)
            {
                insertAt = i + 1;
            }
        }

        _lines.Insert(insertAt, new Line(raw, LineKind.KeyValue, _lines[headerIndex].Section, keyText, keyName, value.Trim(), null));
    }

    /// <summary>
    /// Removes every occurrence of the key (array prefixes ignored) in every section, returning what was
    /// removed in document order. Line numbers are those of the parsed text; a line added by
    /// <see cref="Set"/> reports its position at the time of removal.
    /// </summary>
    public IReadOnlyList<IniRemoval> RemoveKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var keyName = NormalizeKey(key);
        var removed = new List<IniRemoval>();

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Kind != LineKind.KeyValue || !KeyEquals(line.Key, keyName))
            {
                continue;
            }

            removed.Add(new IniRemoval(line.Section, line.KeyText!, line.Value!, line.OriginalLineNumber ?? i + 1));
            _lines.RemoveAt(i);
            i--;
        }

        return removed;
    }

    /// <summary>Renders the document with <c>\r\n</c> line endings and a trailing newline (empty text for an empty document).</summary>
    public override string ToString()
    {
        if (_lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            builder.Append(line.Raw).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>All <c>Key=Value</c> lines in document order, for scanners that need section, key and line number.</summary>
    internal IEnumerable<(int LineNumber, string Section, string KeyText, string Key, string Value)> EnumerateKeyValues()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Kind == LineKind.KeyValue)
            {
                yield return (line.OriginalLineNumber ?? i + 1, line.Section, line.KeyText!, line.Key!, line.Value!);
            }
        }
    }

    /// <summary>Strips whitespace and Unreal array-operator prefixes, mirroring <see cref="Launch.ReservedKeys.IsReservedIniKey"/>.</summary>
    internal static string NormalizeKey(string key) => key.Trim().TrimStart(_prefixChars).Trim();

    private static bool SectionEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool KeyEquals(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Line Classify(string raw, string currentSection, int lineNumber)
    {
        var trimmed = raw.Trim();

        if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#')
        {
            return new Line(raw, LineKind.Other, currentSection, null, null, null, lineNumber);
        }

        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            var name = trimmed[1..^1].Trim();
            return new Line(raw, LineKind.Header, name, null, null, null, lineNumber);
        }

        var separator = trimmed.IndexOf('=');
        if (separator > 0)
        {
            var keyText = trimmed[..separator].Trim();
            var keyName = NormalizeKey(keyText);
            if (keyName.Length > 0)
            {
                var value = trimmed[(separator + 1)..].Trim();
                return new Line(raw, LineKind.KeyValue, currentSection, keyText, keyName, value, lineNumber);
            }
        }

        return new Line(raw, LineKind.Other, currentSection, null, null, null, lineNumber);
    }

    private enum LineKind
    {
        /// <summary>Blank line, comment, or text that is neither a header nor <c>Key=Value</c>. Never touched.</summary>
        Other,
        Header,
        KeyValue,
    }

    /// <param name="Raw">The line exactly as it will be rendered.</param>
    /// <param name="Kind">What the line is.</param>
    /// <param name="Section">The header name for a header line; otherwise the section the line belongs to (empty before the first header).</param>
    /// <param name="KeyText">The key as written, including any array prefix.</param>
    /// <param name="Key">The key with prefix and whitespace stripped, used for matching.</param>
    /// <param name="Value">The trimmed text after the first <c>=</c>.</param>
    /// <param name="OriginalLineNumber">1-based line number in the parsed text; null for lines added by <see cref="Set"/>.</param>
    private sealed record Line(string Raw, LineKind Kind, string Section, string? KeyText, string? Key, string? Value, int? OriginalLineNumber)
    {
        public bool IsBlank => Raw.Trim().Length == 0;
    }
}

/// <summary>
/// One <c>Key=Value</c> line removed by <see cref="IniText.RemoveKey"/>. <paramref name="Key"/> is the key as
/// written, including any array prefix; <paramref name="Section"/> is empty for lines before the first header.
/// </summary>
public sealed record IniRemoval(string Section, string Key, string Value, int LineNumber);
