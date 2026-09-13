using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Data;

public class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_CreatesDatabaseInWalMode()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;

        await root.InitializeAsync(ct);

        Assert.True(File.Exists(root.Layout.DatabasePath));
        await using var db = root.CreateDbContext();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (await command.ExecuteScalarAsync(ct))?.ToString());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
    }

    [Fact]
    public async Task Initialize_SeedsMapsSettingsAndMaintenanceRow()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;

        await root.InitializeAsync(ct);

        await using var db = root.CreateDbContext();
        var maps = await db.Maps.ToListAsync(ct);
        Assert.Equal(OfficialMaps.All.Select(m => m.Key).Order(), maps.Select(m => m.Key).Order());
        Assert.All(maps, m => Assert.True(m.IsOfficial));
        Assert.All(maps, m => Assert.NotNull(m.ReleaseDate));
        var island = maps.Single(m => m.Key == "TheIsland_WP");
        var center = maps.Single(m => m.Key == "TheCenter_WP");
        Assert.Equal((true, new DateOnly(2023, 10, 25)), (island.IsStory, island.ReleaseDate));
        Assert.Equal((false, new DateOnly(2024, 6, 4)), (center.IsStory, center.ReleaseDate));

        var settings = await db.AppSettings.ToDictionaryAsync(r => r.Key, r => r.Value, ct);
        Assert.Equal(AppSettingsCodec.Keys.All.Order(), settings.Keys.Order());
        Assert.Equal(new AppSettings(), AppSettingsCodec.Decode(settings));

        var maintenance = await db.MaintenanceStates.SingleAsync(ct);
        Assert.Equal(MaintenanceState.SingletonId, maintenance.Id);
        Assert.Equal(MaintenancePhase.None, maintenance.Phase);
        Assert.Empty(maintenance.Entries);
    }

    [Fact]
    public async Task Initialize_IsIdempotentAndKeepsEdits()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);

        await using (var db = root.CreateDbContext())
        {
            var island = await db.Maps.SingleAsync(m => m.Key == "TheIsland_WP", ct);
            island.Name = "Home";
            island.IsStory = false;
            island.ReleaseDate = null;
            (await db.AppSettings.SingleAsync(s => s.Key == AppSettingsCodec.Keys.GamePortStart, ct)).Value = "8000";
            db.Maps.Remove(await db.Maps.SingleAsync(m => m.Key == "BobsMissions_WP", ct));
            var state = await db.MaintenanceStates.SingleAsync(ct);
            state.Phase = MaintenancePhase.Restarting;
            state.Entries = [new MaintenanceEntry(7, Done: true), new MaintenanceEntry(9, Error: "port conflict")];
            await db.SaveChangesAsync(ct);
        }

        await root.InitializeAsync(ct);

        await using (var db = root.CreateDbContext())
        {
            // The owner's name survives; the story flag and release date are facts and come back.
            var island = await db.Maps.SingleAsync(m => m.Key == "TheIsland_WP", ct);
            Assert.Equal(("Home", true, new DateOnly(2023, 10, 25)), (island.Name, island.IsStory, island.ReleaseDate));
            Assert.Equal("8000", (await db.AppSettings.SingleAsync(s => s.Key == AppSettingsCodec.Keys.GamePortStart, ct)).Value);
            // A deleted official map is re-seeded (the seed is keyed on Key); the row count stays stable.
            Assert.Equal(OfficialMaps.All.Count, await db.Maps.CountAsync(ct));
            Assert.Equal(AppSettingsCodec.Keys.All.Count, await db.AppSettings.CountAsync(ct));

            var state = await db.MaintenanceStates.SingleAsync(ct);
            Assert.Equal(MaintenancePhase.Restarting, state.Phase);
            Assert.Equal([new MaintenanceEntry(7, Done: true), new MaintenanceEntry(9, Error: "port conflict")], state.Entries);
        }
    }

    [Fact]
    public async Task IniDocument_RequiresExactlyOneOwner()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await root.InitializeAsync(ct);

        await using var db = root.CreateDbContext();
        db.IniDocuments.Add(new IniDocument { File = IniFile.Game, Text = string.Empty, Sha256 = new string('0', 64), UpdatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }
}
