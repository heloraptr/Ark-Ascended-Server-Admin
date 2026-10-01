using System.Text;

namespace ArkAscendedServerAdmin.Ini;

public enum LineEndingStyle
{
    /// <summary><c>\r\n</c>, what Windows tools and the game itself write.</summary>
    CrLf,
    /// <summary><c>\n</c>.</summary>
    Lf,
}

/// <summary>
/// Keeps a source file's own line endings across an edit. A browser text area always reports <c>\n</c>, so the
/// text it sends is put back into the style of the file on disk before it is written; otherwise every save of a
/// CRLF file would rewrite every line.
/// </summary>
public static class IniLineEndings
{
    /// <summary>
    /// The style <paramref name="text"/> mostly uses: LF only when bare <c>\n</c> breaks outnumber <c>\r\n</c>
    /// ones, CRLF on a tie and for text without any break, since these are configs of a Windows server.
    /// </summary>
    public static LineEndingStyle Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return lf > crlf ? LineEndingStyle.Lf : LineEndingStyle.CrLf;
    }

    /// <summary>
    /// <paramref name="text"/> with every line break (<c>\r\n</c>, <c>\n</c>, or a lone <c>\r</c>) written in
    /// <paramref name="style"/>. Nothing else changes: a trailing break stays, a missing one stays missing.
    /// </summary>
    public static string Apply(string text, LineEndingStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newline = style == LineEndingStyle.CrLf ? "\r\n" : "\n";
        var builder = new StringBuilder(text.Length + (text.Length / 16));
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r':
                    builder.Append(newline);
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n':
                    builder.Append(newline);
                    break;
                default:
                    builder.Append(text[i]);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>True when the two texts differ in nothing but their line endings.</summary>
    public static bool Equivalent(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return string.Equals(Apply(a, LineEndingStyle.Lf), Apply(b, LineEndingStyle.Lf), StringComparison.Ordinal);
    }
}
