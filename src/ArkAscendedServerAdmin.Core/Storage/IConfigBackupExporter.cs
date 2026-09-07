namespace ArkAscendedServerAdmin.Storage;

/// <summary>Produces a consistent copy of the configuration database while the service is running.</summary>
public interface IConfigBackupExporter
{
    /// <summary>Writes the copy to <paramref name="destinationPath"/>, replacing any existing file.</summary>
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);
}
