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

        var existing = (await db.Maps.ToListAsync(cancellationToken)).ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name, isStory, releaseDate) in OfficialMaps.All)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                db.Maps.Add(new Map { Key = key, Name = name, IsOfficial = true, IsStory = isStory, ReleaseDate = releaseDate });
                changed = true;
            }
            else if (!row.IsOfficial || row.IsStory != isStory || row.ReleaseDate != releaseDate)
            {
                // Facts about the map, not owner preferences: keep them current (the display name is the owner's).
                row.IsOfficial = true;
                row.IsStory = isStory;
                row.ReleaseDate = releaseDate;
                changed = true;
            }
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
