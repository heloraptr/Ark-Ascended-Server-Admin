using ArkAscendedServerAdmin.Backups;
using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.Infrastructure.Backups;

/// <summary>
/// The journal folder <c>Data\restore-journals\</c> (B2): one JSON file per unresolved restore, written with a temp
/// file and rename so a crash never leaves a half-written record. Every query reads the folder; there are at most
/// a handful of files and the enforcement points run once per launch, delete, or restore.
/// </summary>
public sealed class RestoreJournalStore(DataRootLayout layout) : IRestoreJournals
{
    private readonly Lock _sync = new();

    public IReadOnlyList<RestoreJournal> List()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(layout.RestoreJournals);
            var journals = new List<RestoreJournal>();
            foreach (var path in Directory.EnumerateFiles(layout.RestoreJournals, "*.json"))
            {
                if (RestoreJournal.FromJson(File.ReadAllText(path)) is { } journal)
                {
                    journals.Add(journal);
                }
            }

            return journals.OrderBy(j => j.CreatedAt).ThenBy(j => j.OperationId, StringComparer.Ordinal).ToList();
        }
    }

    public RestoreJournal? Find(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return List().FirstOrDefault(j => string.Equals(j.OperationId, operationId, StringComparison.Ordinal));
    }

    public RestoreJournal? FindForInstance(int instanceId) => List().FirstOrDefault(j => j.References(instanceId));

    public RestoreJournal? FindForCluster(int clusterId) => List().FirstOrDefault(j => j.ClusterId == clusterId);

    public void Write(RestoreJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var path = PathOf(journal.OperationId);
        lock (_sync)
        {
            Directory.CreateDirectory(layout.RestoreJournals);
            var temp = path + ".tmp";
            File.WriteAllText(temp, journal.ToJson());
            File.Move(temp, path, overwrite: true);
        }
    }

    public void Delete(string operationId)
    {
        var path = PathOf(operationId);
        lock (_sync)
        {
            File.Delete(path);
        }
    }

    private string PathOf(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!string.Equals(Path.GetFileName(operationId), operationId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{operationId}' is not a plain journal name.", nameof(operationId));
        }

        return Path.Combine(layout.RestoreJournals, operationId + ".json");
    }
}
