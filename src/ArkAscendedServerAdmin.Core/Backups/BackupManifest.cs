using System.Text.Json;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>One archived file with the hash the verification step checks (plan step 28).</summary>
/// <param name="Path">Archive-relative path with <c>/</c> separators.</param>
/// <param name="Length">Byte length.</param>
/// <param name="Sha256">Lower-case hex SHA-256 computed while the file was copied.</param>
public sealed record BackupManifestEntry(string Path, long Length, string Sha256);

/// <summary>
/// <c>manifest.json</c> inside every archive (plan step 28): what the backup contains and how to verify it.
/// The world file must be listed (<see cref="ContainsWorldFile"/>) or the archive is not a restorable backup.
/// </summary>
/// <param name="InstanceSlug">The instance the archive belongs to.</param>
/// <param name="MapKey">The map whose <c>.ark</c> is the anchor file.</param>
/// <param name="CreatedAt">When the snapshot was taken.</param>
/// <param name="Files">Every archived file.</param>
public sealed record BackupManifest(string InstanceSlug, string MapKey, DateTimeOffset CreatedAt, IReadOnlyList<BackupManifestEntry> Files)
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool ContainsWorldFile =>
        Files.Any(f => string.Equals(f.Path, BackupInventory.WorldFilePath(MapKey), StringComparison.OrdinalIgnoreCase));

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    /// <summary>Parses manifest text; null when it is not a manifest.</summary>
    public static BackupManifest? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
