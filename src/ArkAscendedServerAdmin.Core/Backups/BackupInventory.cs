namespace ArkAscendedServerAdmin.Backups;

/// <summary>One file as seen by an inventory pass (plan step 28).</summary>
/// <param name="RelativePath">Archive-relative path with <c>/</c> separators (<c>World/…</c> or <c>Cluster/…</c>).</param>
/// <param name="Length">File length in bytes.</param>
/// <param name="LastWriteUtc">Last write time.</param>
public sealed record BackupFileEntry(string RelativePath, long Length, DateTimeOffset LastWriteUtc);

/// <summary>What changed between two inventories; any non-empty list means the snapshot is not trustworthy.</summary>
/// <param name="Added">Paths present only in the later inventory.</param>
/// <param name="Removed">Paths present only in the earlier inventory.</param>
/// <param name="Changed">Paths whose length or last-write time differ.</param>
public sealed record InventoryDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    public override string ToString()
    {
        var parts = new List<string>(3);
        if (Added.Count > 0)
        {
            parts.Add($"added {string.Join(", ", Added)}");
        }

        if (Removed.Count > 0)
        {
            parts.Add($"removed {string.Join(", ", Removed)}");
        }

        if (Changed.Count > 0)
        {
            parts.Add($"changed {string.Join(", ", Changed)}");
        }

        return string.Join("; ", parts);
    }
}

/// <summary>
/// The explicit file selection of plan step 28: from the world directory (<c>Saved\&lt;slug&gt;\&lt;Map&gt;\</c>)
/// only <c>&lt;Map&gt;.ark</c>, <c>*.arkprofile</c>, and <c>*.arktribe</c>; from the cluster directory everything;
/// never the rolling <c>*.arkrbf</c> copies or <c>*_AntiCorruptionBackup.bak</c>. Pure: the caller enumerates.
/// </summary>
public static class BackupInventory
{
    /// <summary>Archive folder holding the world files.</summary>
    public const string WorldFolder = "World";

    /// <summary>Archive folder holding the cluster directory contents.</summary>
    public const string ClusterFolder = "Cluster";

    /// <summary>Archive-relative path of the world file for a map.</summary>
    public static string WorldFilePath(string mapKey) => $"{WorldFolder}/{mapKey}.ark";

    /// <summary>True for the files the game keeps as rollback copies, which are never archived.</summary>
    public static bool IsExcluded(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.EndsWith(".arkrbf", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_AntiCorruptionBackup.bak", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a world-directory file that step 28 archives.</summary>
    public static bool IsWorldFileSelected(string fileName, string mapKey)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mapKey);
        if (IsExcluded(fileName))
        {
            return false;
        }

        return string.Equals(fileName, $"{mapKey}.ark", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".arkprofile", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".arktribe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies the selection and prefixes the archive folders. World entries are the world directory's
    /// immediate files (paths relative to it; nested paths are ignored); cluster entries are relative to the
    /// cluster directory and may be nested. The result is sorted by path so two inventories compare positionally.
    /// </summary>
    public static IReadOnlyList<BackupFileEntry> Select(string mapKey, IEnumerable<BackupFileEntry> worldFiles, IEnumerable<BackupFileEntry> clusterFiles)
    {
        ArgumentNullException.ThrowIfNull(mapKey);
        ArgumentNullException.ThrowIfNull(worldFiles);
        ArgumentNullException.ThrowIfNull(clusterFiles);

        var selected = new List<BackupFileEntry>();
        foreach (var file in worldFiles)
        {
            var name = Normalize(file.RelativePath);
            if (!name.Contains('/', StringComparison.Ordinal) && IsWorldFileSelected(name, mapKey))
            {
                selected.Add(file with { RelativePath = $"{WorldFolder}/{name}" });
            }
        }

        foreach (var file in clusterFiles)
        {
            var name = Normalize(file.RelativePath);
            if (!IsExcluded(Path.GetFileName(name)))
            {
                selected.Add(file with { RelativePath = $"{ClusterFolder}/{name}" });
            }
        }

        return selected.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Compares two inventories by path (case-insensitive), length, and last-write time.</summary>
    public static InventoryDifference Compare(IReadOnlyList<BackupFileEntry> before, IReadOnlyList<BackupFileEntry> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeByPath = before.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var afterByPath = after.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        var added = afterByPath.Keys.Where(p => !beforeByPath.ContainsKey(p)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = beforeByPath.Keys.Where(p => !afterByPath.ContainsKey(p)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = beforeByPath
            .Where(kv => afterByPath.TryGetValue(kv.Key, out var later) && (later.Length != kv.Value.Length || later.LastWriteUtc != kv.Value.LastWriteUtc))
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventoryDifference(added, removed, changed);
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');
}
