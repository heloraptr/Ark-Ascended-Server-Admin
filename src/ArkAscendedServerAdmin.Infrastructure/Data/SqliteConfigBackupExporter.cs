using ArkAscendedServerAdmin.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// "Export config backup": uses SQLite's online backup API through <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>,
/// which produces a consistent copy even with WAL traffic in flight. A raw copy of the .db file is only
/// safe with the service stopped.
/// </summary>
public sealed class SqliteConfigBackupExporter(IDbContextFactory<AppDbContext> contextFactory) : IConfigBackupExporter
{
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        var temp = destinationPath + ".tmp";
        File.Delete(temp);
        try
        {
            await using (var target = new SqliteConnection($"Data Source={temp}"))
            {
                await target.OpenAsync(cancellationToken);
                connection.BackupDatabase(target);
            }

            // Release the pooled handle so the temp file can be renamed on Windows.
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={temp}"));
            File.Move(temp, destinationPath, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
