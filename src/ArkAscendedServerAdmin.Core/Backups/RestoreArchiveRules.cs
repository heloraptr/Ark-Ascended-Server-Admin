using System.Text.RegularExpressions;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>
/// What an archive must look like before a restore may delete anything (B2). Entry names are allowlisted, not
/// blocklisted: <c>World/&lt;file&gt;</c> or <c>Cluster/&lt;seg&gt;/.../&lt;file&gt;</c> where every segment is a
/// plain Windows-safe name (no leading or trailing dot or space, no colon so no alternate data stream, no
/// backslash, no <c>..</c>, no reserved device name), no two entries differ only by case, exactly one
/// <c>manifest.json</c> exempt from the listing rule, and the payload entries are in a bijection with the
/// manifest's files. Pure: the caller enumerates the archive and checks hashes.
/// </summary>
public static partial class RestoreArchiveRules
{
    private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>One archive entry as the caller enumerated it.</summary>
    /// <param name="Name">The entry's full name with <c>/</c> separators.</param>
    /// <param name="Length">Uncompressed length.</param>
    public sealed record ArchiveEntry(string Name, long Length)
    {
        /// <summary>A zip directory entry: a trailing slash and no content. Allowed under <c>Cluster/</c>, never payload.</summary>
        public bool IsDirectory => Name.EndsWith('/') && Length == 0;
    }

    /// <summary>The result of <see cref="Check"/>: the problem, or the payload entries keyed by manifest path.</summary>
    /// <param name="Problem">Null when the archive may be restored.</param>
    /// <param name="Payload">Every payload entry paired with its manifest entry; empty when there is a problem.</param>
    /// <param name="Directories">Directory entries to recreate, relative to the cluster directory, empty when none.</param>
    public sealed record CheckResult(string? Problem, IReadOnlyList<(ArchiveEntry Entry, BackupManifestEntry Manifest)> Payload, IReadOnlyList<string> Directories)
    {
        public static CheckResult Fail(string problem) => new(problem, [], []);
    }

    /// <summary>Null when the segment is acceptable, otherwise why not.</summary>
    public static string? CheckSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!SegmentPattern().IsMatch(segment))
        {
            return $"'{segment}' is not a plain file or folder name";
        }

        var stem = segment.Split('.', 2)[0];
        if (_reservedDeviceNames.Contains(stem))
        {
            return $"'{segment}' is a reserved device name";
        }

        return null;
    }

    /// <summary>
    /// Null when the entry name is an allowed <c>World/&lt;file&gt;</c> or <c>Cluster/...</c> path, otherwise why not.
    /// A directory entry (trailing slash) is allowed only under <c>Cluster/</c>.
    /// </summary>
    public static string? CheckEntryName(string name, string mapKey)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mapKey);
        if (name.Length == 0 || name.Contains('\\', StringComparison.Ordinal))
        {
            return $"'{name}' is not an allowed archive path";
        }

        var isDirectory = name.EndsWith('/');
        var segments = (isDirectory ? name[..^1] : name).Split('/');
        if (segments.Length < 2)
        {
            return $"'{name}' is not under World/ or Cluster/";
        }

        foreach (var segment in segments.Skip(1))
        {
            if (CheckSegment(segment) is { } problem)
            {
                return $"{name}: {problem}";
            }
        }

        if (string.Equals(segments[0], BackupInventory.WorldFolder, StringComparison.Ordinal))
        {
            if (isDirectory || segments.Length != 2)
            {
                return $"'{name}' is not a file directly under World/";
            }

            return BackupInventory.IsWorldFileSelected(segments[1], mapKey) ? null : $"'{name}' is not a file a backup selects";
        }

        if (string.Equals(segments[0], BackupInventory.ClusterFolder, StringComparison.Ordinal))
        {
            return BackupInventory.IsExcluded(segments[^1]) ? $"'{name}' is a rollback copy" : null;
        }

        return $"'{name}' is not under World/ or Cluster/";
    }

    /// <summary>
    /// Checks the whole archive layout against the manifest and the instance's identity: exactly one manifest,
    /// payload entries in a bijection with the manifest (same path, same length; hashes are the caller's), every
    /// name allowed, no case-only duplicates, exactly one <c>World/&lt;mapKey&gt;.ark</c>, and the manifest's
    /// slug and map key equal to the instance's.
    /// </summary>
    public static CheckResult Check(BackupManifest manifest, IReadOnlyList<ArchiveEntry> entries, string instanceSlug, string mapKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(instanceSlug);
        ArgumentNullException.ThrowIfNull(mapKey);

        if (!string.Equals(manifest.InstanceSlug, instanceSlug, StringComparison.OrdinalIgnoreCase))
        {
            return CheckResult.Fail($"the backup belongs to instance '{manifest.InstanceSlug}', not '{instanceSlug}'");
        }

        if (!string.Equals(manifest.MapKey, mapKey, StringComparison.OrdinalIgnoreCase))
        {
            return CheckResult.Fail($"the backup is of map '{manifest.MapKey}', but the instance now runs '{mapKey}'");
        }

        var manifests = entries.Count(e => string.Equals(e.Name, BackupManifest.FileName, StringComparison.OrdinalIgnoreCase));
        if (manifests != 1)
        {
            return CheckResult.Fail(manifests == 0 ? "the archive has no manifest.json" : "the archive has more than one manifest.json");
        }

        var listed = new Dictionary<string, BackupManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!listed.TryAdd(file.Path, file))
            {
                return CheckResult.Fail($"the manifest lists {file.Path} twice");
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payload = new List<(ArchiveEntry, BackupManifestEntry)>(entries.Count);
        var directories = new List<string>();
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Name, BackupManifest.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CheckEntryName(entry.Name, mapKey) is { } problem)
            {
                return CheckResult.Fail(problem);
            }

            if (!seen.Add(entry.Name.TrimEnd('/')))
            {
                return CheckResult.Fail($"the archive has two entries named {entry.Name} (differing only by case)");
            }

            if (entry.IsDirectory)
            {
                directories.Add(entry.Name[(BackupInventory.ClusterFolder.Length + 1)..^1]);
                continue;
            }

            if (!listed.Remove(entry.Name, out var manifestEntry))
            {
                return CheckResult.Fail($"{entry.Name} is in the archive but not in the manifest");
            }

            if (manifestEntry.Length != entry.Length)
            {
                return CheckResult.Fail($"{entry.Name} is {entry.Length} bytes in the archive, {manifestEntry.Length} in the manifest");
            }

            payload.Add((entry, manifestEntry));
        }

        if (listed.Count > 0)
        {
            return CheckResult.Fail($"{listed.Keys.First()} is in the manifest but not in the archive");
        }

        var worldFile = BackupInventory.WorldFilePath(mapKey);
        if (payload.Count(p => string.Equals(p.Item1.Name, worldFile, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return CheckResult.Fail($"the archive does not contain {worldFile}");
        }

        return new CheckResult(null, payload, directories);
    }

    [GeneratedRegex("^(?:[A-Za-z0-9]|[A-Za-z0-9][A-Za-z0-9 ._-]*[A-Za-z0-9_-])$")]
    private static partial Regex SegmentPattern();
}
