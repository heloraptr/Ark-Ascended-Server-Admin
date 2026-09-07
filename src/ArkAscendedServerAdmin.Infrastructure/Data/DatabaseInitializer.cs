using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// Migrates the database, switches it to WAL, and seeds the official maps, every App Setting default, and
/// the singleton maintenance row. Every step is idempotent; it runs at each service start.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<AppDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            var names = string.Join(", ", pending);
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, names);
        }

        await db.Database.MigrateAsync(cancellationToken);

        // journal_mode is persistent for the database file; setting it every start is harmless.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        await SeedAsync(db, cancellationToken);
    }

    private async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var changed = false;

        var existingKeys = (await db.Maps.Select(m => m.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in OfficialMaps.All.Where(m => !existingKeys.Contains(m.Key)))
        {
            db.Maps.Add(new Map { Key = key, Name = name, IsOfficial = true });
            changed = true;
        }

        var existingSettings = (await db.AppSettings.Select(s => s.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in AppSettingsCodec.Encode(new AppSettings()).Where(kv => !existingSettings.Contains(kv.Key)))
        {
            db.AppSettings.Add(new AppSettingRow { Key = key, Value = value });
            changed = true;
        }

        if (!await db.MaintenanceStates.AnyAsync(cancellationToken))
        {
            db.MaintenanceStates.Add(new MaintenanceState { UpdatedAt = now });
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded missing maps, App Setting defaults, and maintenance state.");
        }
    }
}
