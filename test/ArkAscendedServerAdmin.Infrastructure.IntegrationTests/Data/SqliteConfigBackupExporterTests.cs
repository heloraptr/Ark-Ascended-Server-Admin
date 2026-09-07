using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Data;

public class SqliteConfigBackupExporterTests
{
    [Fact]
    public async Task Export_WritesAConsistentCopyWhileTheDatabaseIsOpen()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var store = new AppSettingsStore(root);
        await store.SaveAsync(new AppSettings { CurseForgeApiKey = "exported-key" }, ct);

        // Keep a live connection open with WAL traffic pending to prove the copy does not need exclusivity.
        await using var live = root.CreateDbContext();
        await live.Database.OpenConnectionAsync(ct);

        var destination = Path.Combine(root.Layout.Exports, "config.db");
        await new SqliteConfigBackupExporter(root).ExportAsync(destination, ct);

        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(destination + ".tmp"));

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={destination}").Options;
        await using (var copy = new AppDbContext(options))
        {
            var key = await copy.AppSettings.SingleAsync(s => s.Key == AppSettingsCodec.Keys.CurseForgeApiKey, ct);
            Assert.Equal("exported-key", key.Value);
            Assert.Empty(await copy.Database.GetPendingMigrationsAsync(ct));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Export_ReplacesAnExistingFile()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);
        var destination = Path.Combine(root.Layout.Exports, "config.db");
        await File.WriteAllTextAsync(destination, "not a database", ct);

        await new SqliteConfigBackupExporter(root).ExportAsync(destination, ct);

        await using var connection = new SqliteConnection($"Data Source={destination}");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Maps;";
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0);
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }
}
