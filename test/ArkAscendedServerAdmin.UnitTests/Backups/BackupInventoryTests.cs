using ArkAscendedServerAdmin.Backups;

namespace ArkAscendedServerAdmin.UnitTests.Backups;

public class BackupInventoryTests
{
    private static readonly DateTimeOffset _written = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("TheIsland_WP.ark", true)]
    [InlineData("theisland_wp.ARK", true)]
    [InlineData("12345.arkprofile", true)]
    [InlineData("67890.arktribe", true)]
    [InlineData("TheIsland_WP_07.09.2026_12.00.00.arkrbf", false)]
    [InlineData("TheIsland_WP_AntiCorruptionBackup.bak", false)]
    [InlineData("ScorchedEarth_WP.ark", false)]
    [InlineData("readme.txt", false)]
    [InlineData("TheIsland_WP.ark.tmp", false)]
    public void IsWorldFileSelected_FollowsStep28(string fileName, bool expected)
    {
        Assert.Equal(expected, BackupInventory.IsWorldFileSelected(fileName, "TheIsland_WP"));
    }

    [Theory]
    [InlineData("x.arkrbf", true)]
    [InlineData("TheIsland_WP_AntiCorruptionBackup.bak", true)]
    [InlineData("theisland_wp_anticorruptionbackup.BAK", true)]
    [InlineData("TheIsland_WP.ark", false)]
    [InlineData("other.bak", false)]
    public void IsExcluded_MatchesRollbackCopies(string fileName, bool expected)
    {
        Assert.Equal(expected, BackupInventory.IsExcluded(fileName));
    }

    [Fact]
    public void Select_PicksWorldFilesAndEverythingInTheClusterDirectory_Sorted()
    {
        var world = new[]
        {
            Entry("TheIsland_WP.ark", 10),
            Entry("TheIsland_WP_07.09.2026_12.00.00.arkrbf", 10),
            Entry("TheIsland_WP_AntiCorruptionBackup.bak", 10),
            Entry("111.arkprofile", 1),
            Entry("222.arktribe", 2),
            Entry("notes.txt", 3),
            Entry("nested/333.arkprofile", 4),
        };
        var cluster = new[]
        {
            Entry("transfer.dat", 5),
            Entry(@"Config\Game.ini", 6),
            Entry("stale.arkrbf", 7),
        };

        var selected = BackupInventory.Select("TheIsland_WP", world, cluster);

        Assert.Equal(
            ["Cluster/Config/Game.ini", "Cluster/transfer.dat", "World/111.arkprofile", "World/222.arktribe", "World/TheIsland_WP.ark"],
            selected.Select(e => e.RelativePath));
        Assert.Equal(6, selected.Single(e => e.RelativePath == "Cluster/Config/Game.ini").Length);
    }

    [Fact]
    public void Select_WithoutClusterFiles_IsStandaloneOnly()
    {
        var selected = BackupInventory.Select("TheIsland_WP", [Entry("TheIsland_WP.ark", 10)], []);

        Assert.Equal([BackupInventory.WorldFilePath("TheIsland_WP")], selected.Select(e => e.RelativePath));
    }

    [Fact]
    public void Compare_IdenticalInventories_IsEmpty()
    {
        var before = new[] { Entry("World/a.ark", 1), Entry("Cluster/b", 2) };
        var after = new[] { Entry("World/a.ark", 1), Entry("Cluster/b", 2) };

        var difference = BackupInventory.Compare(before, after);

        Assert.True(difference.IsEmpty);
        Assert.Equal(string.Empty, difference.ToString());
    }

    [Fact]
    public void Compare_ReportsAddedRemovedAndChanged()
    {
        var before = new[] { Entry("World/a.ark", 1), Entry("World/gone.arktribe", 2), Entry("World/size.arkprofile", 3), Entry("World/time.arkprofile", 4) };
        var after = new[]
        {
            Entry("World/a.ark", 1),
            Entry("World/new.arktribe", 2),
            Entry("World/size.arkprofile", 30),
            Entry("World/time.arkprofile", 4, _written.AddSeconds(1)),
        };

        var difference = BackupInventory.Compare(before, after);

        Assert.Equal(["World/new.arktribe"], difference.Added);
        Assert.Equal(["World/gone.arktribe"], difference.Removed);
        Assert.Equal(["World/size.arkprofile", "World/time.arkprofile"], difference.Changed);
        Assert.False(difference.IsEmpty);
        Assert.Contains("added World/new.arktribe", difference.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_IsCaseInsensitiveOnPaths()
    {
        var difference = BackupInventory.Compare([Entry("World/A.ark", 1)], [Entry("world/a.ARK", 1)]);

        Assert.True(difference.IsEmpty);
    }

    [Fact]
    public void Manifest_RoundTripsThroughJson_AndKnowsTheWorldFile()
    {
        var manifest = new BackupManifest("alpha", "TheIsland_WP", _written, [new BackupManifestEntry("World/TheIsland_WP.ark", 10, "ab")]);

        var parsed = BackupManifest.FromJson(manifest.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(manifest.InstanceSlug, parsed.InstanceSlug);
        Assert.Equal(manifest.MapKey, parsed.MapKey);
        Assert.Equal(manifest.CreatedAt, parsed.CreatedAt);
        Assert.Equal(manifest.Files, parsed.Files);
        Assert.True(parsed.ContainsWorldFile);
        Assert.False((manifest with { MapKey = "Other_WP" }).ContainsWorldFile);
        Assert.Null(BackupManifest.FromJson("not json"));
    }

    private static BackupFileEntry Entry(string path, long length, DateTimeOffset? written = null) => new(path, length, written ?? _written);
}
