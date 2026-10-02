using Microsoft.Data.Sqlite;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// Builds SQLite connection strings through <see cref="SqliteConnectionStringBuilder"/> rather than by
/// interpolation, so a data root whose path contains <c>;</c> or <c>=</c> is quoted instead of being read
/// as extra keywords.
/// </summary>
internal static class SqliteConnectionStrings
{
    public static string ForFile(string path) => new SqliteConnectionStringBuilder { DataSource = path }.ToString();
}
