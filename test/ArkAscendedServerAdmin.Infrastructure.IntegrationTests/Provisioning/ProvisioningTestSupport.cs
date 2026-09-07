using System.Diagnostics;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Provisioning;

/// <summary>A stand-in for the SteamCMD install: the four junction targets, each holding a marker file.</summary>
internal static class FakeServerTree
{
    public static readonly string[] JunctionPaths =
    [
        "Engine",
        Path.Combine("ShooterGame", "Binaries"),
        Path.Combine("ShooterGame", "Content"),
        Path.Combine("ShooterGame", "Plugins"),
    ];

    public const string MarkerFileName = "marker.txt";

    public static void Create(DataRootLayout layout)
    {
        foreach (var relative in JunctionPaths)
        {
            var directory = Path.Combine(layout.Server, relative);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, MarkerFileName), relative);
        }
    }

    public static bool TargetsIntact(DataRootLayout layout) =>
        JunctionPaths.All(relative => File.Exists(Path.Combine(layout.Server, relative, MarkerFileName)));

    public static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Creates a junction the way the spike did, to prove the service interoperates with <c>mklink /J</c> output.</summary>
    public static void MklinkJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Delegates to the real factory until <see cref="Fail"/> is set, then throws on every context request.</summary>
internal sealed class ToggleFailureContextFactory(IDbContextFactory<AppDbContext> inner) : IDbContextFactory<AppDbContext>
{
    public bool Fail { get; set; }

    public AppDbContext CreateDbContext() =>
        Fail ? throw new InvalidOperationException("Simulated storage failure.") : inner.CreateDbContext();
}

/// <param name="MapId">The seeded map.</param>
/// <param name="ClusterId">The seeded cluster (slug <c>alpha-cluster</c>).</param>
/// <param name="ClusteredInstanceId">An instance in the cluster (slug <c>alpha</c>).</param>
/// <param name="StandaloneInstanceId">An instance without a cluster (slug <c>solo</c>).</param>
internal sealed record SeededIds(int MapId, int ClusterId, int ClusteredInstanceId, int StandaloneInstanceId);

internal static class Seed
{
    public const string ClusterSlug = "alpha-cluster";
    public const string ClusteredSlug = "alpha";
    public const string StandaloneSlug = "solo";

    public static async Task<SeededIds> CreateAsync(TempDataRoot root, CancellationToken cancellationToken)
    {
        await root.InitializeAsync(cancellationToken);
        await using var db = root.CreateDbContext();

        // The initializer seeds the official maps; reuse one rather than fight the unique index.
        var map = await db.Maps.SingleAsync(m => m.Key == "TheIsland_WP", cancellationToken);

        var cluster = new Cluster
        {
            Name = "Alpha Cluster",
            Slug = ClusterSlug,
            ClusterKey = ClusterSlug,
            AdminWhitelist = "0002abc\r\n0002def\r\n",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clusters.Add(cluster);

        var clustered = new Instance
        {
            Name = "Alpha",
            Slug = ClusteredSlug,
            Cluster = cluster,
            Map = map,
            SessionName = "Alpha Island",
            GamePort = 7777,
            RconPort = 27020,
            MaxPlayers = 20,
            AdminWhitelist = "0002def\r\n0002ghi\r\n",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        clustered.ExtraOverrides.Add(new ExtraOverride { File = IniFile.GameUserSettings, Section = "ServerSettings", Key = "XPMultiplier", Value = "2.0" });
        clustered.ExtraOverrides.Add(new ExtraOverride { File = IniFile.Game, Section = "/Script/ShooterGame.ShooterGameMode", Key = "TamingSpeedMultiplier", Value = "3" });
        db.Instances.Add(clustered);

        var standalone = new Instance
        {
            Name = "Solo",
            Slug = StandaloneSlug,
            Map = map,
            SessionName = "Solo Island",
            GamePort = 7787,
            RconPort = 27030,
            MaxPlayers = 10,
            AdminWhitelist = "0002zzz\r\n",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Instances.Add(standalone);

        await db.SaveChangesAsync(cancellationToken);
        return new SeededIds(map.Id, cluster.Id, clustered.Id, standalone.Id);
    }
}
