using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkAscendedServerAdmin.Backups;

/// <summary>Where an interrupted restore got to (B2).</summary>
public enum RestorePhase
{
    /// <summary>The safety copy is complete and the current files are being deleted and replaced.</summary>
    Replacing,
    /// <summary>The replacement failed and the safety copy is being copied back.</summary>
    RollingBack,
    /// <summary>Copying the safety copy back failed too; the directories are in an unknown state.</summary>
    RollbackFailed,
}

/// <summary>
/// The durable record of a restore whose destructive phase has begun and not finished (B2). Written to
/// <c>Data\restore-journals\&lt;OperationId&gt;.json</c>, outside every directory a backup archives or a restore
/// replaces, before the first delete; removed on success or on a completed rollback. While it exists, launching,
/// restoring, or deleting any affected instance, deleting the cluster, and adding a member are refused; only
/// "Recover from safety copy" and "Discard journal" are allowed.
/// </summary>
/// <param name="OperationId">Unique per attempt; also the file name.</param>
/// <param name="CreatedAt">When the destructive phase began.</param>
/// <param name="InstanceId">The instance the restore targets.</param>
/// <param name="InstanceSlug">Its slug, so a log line is readable without the database.</param>
/// <param name="MapKey">The map whose world directory is being replaced.</param>
/// <param name="ClusterId">The cluster whose directory is being replaced; null when cluster data was not included.</param>
/// <param name="ClusterSlug">Its slug; null when cluster data was not included.</param>
/// <param name="AffectedInstanceIds">Every instance whose files or shared cluster directory the restore touches; the target alone for world-only restores.</param>
/// <param name="SafetyCopyPath">The directory holding the files as they were before the first delete.</param>
/// <param name="SourceFileName">The archive being restored.</param>
/// <param name="Phase">How far the operation got.</param>
public sealed record RestoreJournal(
    string OperationId,
    DateTimeOffset CreatedAt,
    int InstanceId,
    string InstanceSlug,
    string MapKey,
    int? ClusterId,
    string? ClusterSlug,
    IReadOnlyList<int> AffectedInstanceIds,
    string SafetyCopyPath,
    string SourceFileName,
    RestorePhase Phase)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public bool IncludesCluster => ClusterId is not null;

    public bool References(int instanceId) => AffectedInstanceIds.Contains(instanceId);

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    /// <summary>Parses journal text; null when it is not a journal.</summary>
    public static RestoreJournal? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<RestoreJournal>(json, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The refusal every enforcement point reports while this journal exists.</summary>
    public string RefusalReason(string subject) =>
        $"An incomplete restore ({OperationId}) references {subject}; recover from the safety copy or discard the journal first.";
}

/// <summary>The journal folder (B2). Implementations are safe to call from any thread; every call reads the folder.</summary>
public interface IRestoreJournals
{
    IReadOnlyList<RestoreJournal> List();

    RestoreJournal? Find(string operationId);

    /// <summary>The journal whose affected set includes the instance, if any.</summary>
    RestoreJournal? FindForInstance(int instanceId);

    /// <summary>The journal that replaces the cluster's directory, if any.</summary>
    RestoreJournal? FindForCluster(int clusterId);

    /// <summary>Writes or replaces the journal durably (temp file and rename).</summary>
    void Write(RestoreJournal journal);

    void Delete(string operationId);
}
