using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Ini;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Provisioning;

public class GeneratedConfigWriterTests
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string GameUserSettingsSource =
        "[ServerSettings]\r\n" +
        "ServerAdminPassword=hunter2\r\n" +
        "DifficultyOffset=1.0\r\n";

    private const string GameSource =
        "[/Script/ShooterGame.ShooterGameMode]\r\n" +
        "bDisableStructurePlacementCollision=True\r\n";

    private static (IniSourceStore Store, GeneratedConfigWriter Writer) Create(TempDataRoot root)
    {
        var store = new IniSourceStore(root.Layout, root, TimeProvider.System, NullLogger<IniSourceStore>.Instance);
        return (store, new GeneratedConfigWriter(root.Layout, root, store, new AppSettingsStore(root), NullLogger<GeneratedConfigWriter>.Instance));
    }

    private static async Task SeedSourcesAsync(IniSourceStore store, IniOwner owner, CancellationToken ct)
    {
        await store.SaveAsync(owner, IniFile.Game, GameSource, EmptyHash, ct);
        await store.SaveAsync(owner, IniFile.GameUserSettings, GameUserSettingsSource, EmptyHash, ct);
    }

    [Fact]
    public async Task Write_ClusteredInstance_UsesClusterSourceAndWritesEverything()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var (store, writer) = Create(root);
        await SeedSourcesAsync(store, IniOwner.ForCluster(ids.ClusterId), ct);

        var generated = await writer.WriteAsync(ids.ClusteredInstanceId, ct);

        var configDirectory = root.Layout.InstanceGeneratedConfigDirectory(Seed.ClusteredSlug);
        var gusText = await File.ReadAllTextAsync(Path.Combine(configDirectory, "GameUserSettings.ini"), ct);
        var gameText = await File.ReadAllTextAsync(Path.Combine(configDirectory, "Game.ini"), ct);
        Assert.Equal(generated.GameUserSettingsIni, gusText);
        Assert.Equal(generated.GameIni, gameText);

        var gus = IniText.Parse(gusText);
        Assert.Equal("Alpha Island", gus.Get("SessionSettings", "SessionName"));
        Assert.Equal("7777", gus.Get("SessionSettings", "Port"));
        Assert.Equal("True", gus.Get("ServerSettings", "RCONEnabled"));
        Assert.Equal("27020", gus.Get("ServerSettings", "RCONPort"));
        Assert.Equal("20", gus.Get("/Script/Engine.GameSession", "MaxPlayers"));
        Assert.Equal("2.0", gus.Get("ServerSettings", "XPMultiplier"));
        Assert.Equal("1.0", gus.Get("ServerSettings", "DifficultyOffset"));
        Assert.Equal("hunter2", generated.ServerAdminPassword);

        var game = IniText.Parse(gameText);
        Assert.Equal("3", game.Get("/Script/ShooterGame.ShooterGameMode", "TamingSpeedMultiplier"));
        Assert.Equal("True", game.Get("/Script/ShooterGame.ShooterGameMode", "bDisableStructurePlacementCollision"));

        var whitelistPath = Path.Combine(root.Layout.InstanceSavedDirectory(Seed.ClusteredSlug), GeneratedConfigWriter.AdminWhitelistFileName);
        Assert.Equal("0002abc\r\n0002def\r\n0002ghi\r\n", await File.ReadAllTextAsync(whitelistPath, ct));
        Assert.Equal(["0002abc", "0002def", "0002ghi"], generated.AdminWhitelist);
        Assert.Empty(generated.Warnings);

        Assert.Empty(Directory.GetFiles(configDirectory, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(configDirectory, "GameUserSettings.ini.bak")));
    }

    [Fact]
    public async Task Write_SecondTime_KeepsExactlyOneBackupOfThePreviousFile()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var (store, writer) = Create(root);
        var owner = IniOwner.ForCluster(ids.ClusterId);
        await SeedSourcesAsync(store, owner, ct);
        var first = await writer.WriteAsync(ids.ClusteredInstanceId, ct);

        // Simulate the game's shutdown rewrite of the generated file, then change the source and regenerate.
        var configDirectory = root.Layout.InstanceGeneratedConfigDirectory(Seed.ClusteredSlug);
        var gusPath = Path.Combine(configDirectory, "GameUserSettings.ini");
        await File.WriteAllTextAsync(gusPath, first.GameUserSettingsIni + "[/Script/Engine.Rewritten]\r\nX=1\r\n", ct);
        var source = await store.LoadAsync(owner, IniFile.GameUserSettings, ct);
        await store.SaveAsync(owner, IniFile.GameUserSettings, source.Text + "ServerPVE=True\r\n", source.Sha256, ct);

        var second = await writer.WriteAsync(ids.ClusteredInstanceId, ct);

        Assert.Equal(second.GameUserSettingsIni, await File.ReadAllTextAsync(gusPath, ct));
        Assert.Equal(first.GameUserSettingsIni + "[/Script/Engine.Rewritten]\r\nX=1\r\n", await File.ReadAllTextAsync(gusPath + ".bak", ct));
        Assert.Equal(first.GameIni, await File.ReadAllTextAsync(Path.Combine(configDirectory, "Game.ini.bak"), ct));
        // The game's rewrite never leaked into the generated output (generated files are not source).
        Assert.DoesNotContain("Rewritten", second.GameUserSettingsIni, StringComparison.Ordinal);
        Assert.Contains("ServerPVE=True", second.GameUserSettingsIni, StringComparison.Ordinal);

        var third = await writer.WriteAsync(ids.ClusteredInstanceId, ct);
        Assert.Equal(second.GameUserSettingsIni, await File.ReadAllTextAsync(gusPath + ".bak", ct));
        Assert.Equal(third.GameUserSettingsIni, await File.ReadAllTextAsync(gusPath, ct));
        Assert.Equal(
            ["Game.ini", "Game.ini.bak", "GameUserSettings.ini", "GameUserSettings.ini.bak"],
            Directory.GetFiles(configDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Write_StandaloneInstance_UsesItsOwnSourceAndWhitelist()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var (store, writer) = Create(root);
        await SeedSourcesAsync(store, IniOwner.ForInstance(ids.StandaloneInstanceId), ct);

        var generated = await writer.WriteAsync(ids.StandaloneInstanceId, ct);

        var gus = IniText.Parse(generated.GameUserSettingsIni);
        Assert.Equal("Solo Island", gus.Get("SessionSettings", "SessionName"));
        Assert.Equal("7787", gus.Get("SessionSettings", "Port"));
        Assert.Equal("10", gus.Get("/Script/Engine.GameSession", "MaxPlayers"));
        Assert.Equal(["0002zzz"], generated.AdminWhitelist);
        Assert.True(File.Exists(Path.Combine(root.Layout.InstanceGeneratedConfigDirectory(Seed.StandaloneSlug), "Game.ini")));
    }

    [Fact]
    public async Task Write_WithoutSourceFiles_StillGeneratesManagerKeys_AndNoPassword()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var (_, writer) = Create(root);

        var generated = await writer.WriteAsync(ids.StandaloneInstanceId, ct);

        Assert.Null(generated.ServerAdminPassword);
        Assert.Equal("27030", IniText.Parse(generated.GameUserSettingsIni).Get("ServerSettings", "RCONPort"));
    }

    [Fact]
    public async Task Write_UnknownInstance_Throws()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await Seed.CreateAsync(root, ct);
        var (_, writer) = Create(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(12345, ct));
    }

    [Fact]
    public async Task ReadGeneratedGameUserSettings_RoundTrips_AndIsNullWhenMissing()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var (store, writer) = Create(root);
        await SeedSourcesAsync(store, IniOwner.ForCluster(ids.ClusterId), ct);

        Assert.Null(await writer.ReadGeneratedGameUserSettingsAsync(Seed.ClusteredSlug, ct));

        var generated = await writer.WriteAsync(ids.ClusteredInstanceId, ct);

        Assert.Equal(generated.GameUserSettingsIni, await writer.ReadGeneratedGameUserSettingsAsync(Seed.ClusteredSlug, ct));
    }
}
