using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests;

/// <summary>A throwaway DataRoot on the real filesystem with a context factory for its database.</summary>
public sealed class TempDataRoot : IDisposable, IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public TempDataRoot()
    {
        Layout = new DataRootLayout(Path.Combine(Path.GetTempPath(), "ArkAdminTests", Guid.NewGuid().ToString("N")));
        Layout.EnsureDirectories();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Layout.DatabasePath}")
            .Options;
    }

    public DataRootLayout Layout { get; }

    public AppDbContext CreateDbContext() => new(_options);

    public DatabaseInitializer CreateInitializer() =>
        new(this, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance);

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        await CreateInitializer().InitializeAsync(cancellationToken);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            // Not Directory.Delete(recursive: true): that follows junctions badly (see JunctionSafeDirectory).
            JunctionSafeDirectory.Delete(Layout.Root);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner takes the rest.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
