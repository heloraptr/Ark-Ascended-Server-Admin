using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Naming;

/// <summary>
/// Slug generation and validation (plan step 18). A slug is derived from an instance or cluster name
/// once and never changes: it names <c>Instances\&lt;slug&gt;</c> (or <c>Clusters\&lt;slug&gt;</c>), is
/// passed on the command line as <c>AltSaveDirectoryName</c>, and becomes a directory under
/// <c>Saved\</c>, so it must be safe as a path segment and as a <c>?</c>-token value. Slugs are
/// lower-case <c>[a-z0-9-]</c>, at most <see cref="MaxLength"/> characters, never start or end with a
/// hyphen, are never a Windows device name, and never look like an <c>Archive\</c> entry.
/// </summary>
/// <remarks>
/// Phase 4 builds the <c>reserved</c> set for <see cref="Generate"/> from the live slugs of the namespace
/// being allocated in (instances or clusters are separate namespaces; the caller decides what to pass)
/// plus <see cref="TryParseArchiveDirectoryName"/> over every directory name under <c>Archive\</c>, so
/// a deleted-with-keep instance's slug stays reserved while its archive exists (plan step 30).
/// </remarks>
public static partial class Slug
{
    public const int MaxLength = 32;

    /// <summary>What <see cref="Generate"/> falls back to when nothing of the name survives.</summary>
    public const string Fallback = "instance";

    private const string ArchiveTimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Length of the <c>-yyyyMMdd-HHmmss</c> suffix <c>DataRootLayout.ArchiveDirectory</c> appends.</summary>
    private const int ArchiveSuffixLength = 16;

    private static readonly HashSet<string> _deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Derives a slug from <paramref name="name"/>: NFKD-normalized with combining marks dropped,
    /// lower-cased, every run of characters outside <c>[a-z0-9]</c> collapsed to one hyphen, trimmed and
    /// cut to <see cref="MaxLength"/>; an empty result becomes <see cref="Fallback"/>. When the result is
    /// in <paramref name="reserved"/> (compared ignoring case), is a Windows device name, or parses as an
    /// archive directory name, <c>-2</c>, <c>-3</c>, ... is appended (shortening the base so the total
    /// stays within <see cref="MaxLength"/>). The result always satisfies <see cref="IsValid"/>.
    /// </summary>
    public static string Generate(string name, IEnumerable<string> reserved)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(reserved);

        var taken = new HashSet<string>(reserved.Where(r => !string.IsNullOrWhiteSpace(r)), StringComparer.OrdinalIgnoreCase);
        var baseSlug = Truncate(Sanitize(name), MaxLength);
        if (baseSlug.Length == 0)
        {
            baseSlug = Fallback;
        }

        if (!IsTaken(baseSlug, taken))
        {
            return baseSlug;
        }

        for (var n = 2; ; n++)
        {
            var suffix = $"-{n.ToString(CultureInfo.InvariantCulture)}";
            var candidate = Truncate(baseSlug, MaxLength - suffix.Length) + suffix;
            if (!IsTaken(candidate, taken))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="slug"/> is 1 to <see cref="MaxLength"/> characters of <c>[a-z0-9-]</c>
    /// with no leading or trailing hyphen, is not a Windows device name, and does not parse as an
    /// archive directory name.
    /// </summary>
    public static bool IsValid(string? slug) =>
        slug is not null
        && SlugPattern().IsMatch(slug)
        && !_deviceNames.Contains(slug)
        && TryParseArchiveDirectoryName(slug) is null;

    /// <summary>
    /// Inverts <c>DataRootLayout.ArchiveDirectory</c>: <c>&lt;slug&gt;-yyyyMMdd-HHmmss</c> yields
    /// <c>&lt;slug&gt;</c> when the suffix is a syntactically valid timestamp and the remainder is a valid
    /// slug; otherwise <see langword="null"/>. Callers feed the results into the <c>reserved</c> set of
    /// <see cref="Generate"/> for every directory under <c>Archive\</c>.
    /// </summary>
    public static string? TryParseArchiveDirectoryName(string directoryName)
    {
        ArgumentNullException.ThrowIfNull(directoryName);

        if (directoryName.Length <= ArchiveSuffixLength || directoryName[^ArchiveSuffixLength] != '-')
        {
            return null;
        }

        var timestamp = directoryName[^(ArchiveSuffixLength - 1)..];
        if (!IsTimestampShaped(timestamp)
            || !DateTime.TryParseExact(timestamp, ArchiveTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return null;
        }

        var slug = directoryName[..^ArchiveSuffixLength];
        return IsValid(slug) ? slug : null;
    }

    /// <summary>Eight ASCII digits, a hyphen, six ASCII digits: the shape <see cref="ArchiveTimestampFormat"/> produces.</summary>
    private static bool IsTimestampShaped(string timestamp) =>
        timestamp.Length == ArchiveSuffixLength - 1
        && timestamp[8] == '-'
        && timestamp.Select((c, i) => i == 8 || char.IsAsciiDigit(c)).All(ok => ok);

    private static bool IsTaken(string candidate, HashSet<string> reserved) =>
        reserved.Contains(candidate) || _deviceNames.Contains(candidate) || TryParseArchiveDirectoryName(candidate) is not null;

    /// <summary>Lower-case ASCII letters and digits with every other run collapsed to one hyphen, trimmed.</summary>
    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingHyphen = false;

        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (char.IsAsciiLetterLower(lower) || char.IsAsciiDigit(lower))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Cuts to <paramref name="maxLength"/> and drops any hyphen the cut left at the end.</summary>
    private static string Truncate(string slug, int maxLength) =>
        (slug.Length > maxLength ? slug[..maxLength] : slug).TrimEnd('-');

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}
