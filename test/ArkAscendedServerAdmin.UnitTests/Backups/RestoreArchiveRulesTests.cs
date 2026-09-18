using ArkAscendedServerAdmin.Backups;

namespace ArkAscendedServerAdmin.UnitTests.Backups;

/// <summary>B2: the archive allowlist, the manifest bijection, and the identity checks a restore applies before it deletes anything.</summary>
public class RestoreArchiveRulesTests
{
    private const string MapKey = "TheIsland_WP";

    [Theory]
    [InlineData("World/TheIsland_WP.ark")]
    [InlineData("World/theisland_wp.ARK")]
    [InlineData("World/111.arkprofile")]
    [InlineData("World/222.arktribe")]
    [InlineData("Cluster/transfers/12345.dat")]
    [InlineData("Cluster/Config/Game.ini")]
    [InlineData("Cluster/a")]
    [InlineData("Cluster/a b/c-d_e.f")]
    [InlineData("Cluster/empty/")]
    public void CheckEntryName_AllowsPlainWorldFilesAndClusterPaths(string name) =>
        Assert.Null(RestoreArchiveRules.CheckEntryName(name, MapKey));

    [Theory]
    [InlineData("World/notes.txt")]
    [InlineData("World/Other_WP.ark")]
    [InlineData("World/TheIsland_WP_07.09.2026_12.00.00.arkrbf")]
    [InlineData("World/TheIsland_WP_AntiCorruptionBackup.bak")]
    [InlineData("Cluster/transfers/TheIsland_WP_AntiCorruptionBackup.bak")]
    [InlineData("World/sub/TheIsland_WP.ark")]
    [InlineData("World/")]
    [InlineData("World/empty/")]
    [InlineData("TheIsland_WP.ark")]
    [InlineData("Other/TheIsland_WP.ark")]
    [InlineData("../World/TheIsland_WP.ark")]
    [InlineData("World/../TheIsland_WP.ark")]
    [InlineData("Cluster/../x")]
    [InlineData("Cluster/./x")]
    [InlineData("World\\TheIsland_WP.ark")]
    [InlineData("Cluster/a\\b")]
    [InlineData("Cluster/a:b")]
    [InlineData("World/TheIsland_WP.ark:stream")]
    [InlineData("Cluster/.hidden")]
    [InlineData("Cluster/trailing.")]
    [InlineData("Cluster/trailing ")]
    [InlineData("Cluster/ leading")]
    [InlineData("Cluster/CON")]
    [InlineData("Cluster/con.dat")]
    [InlineData("Cluster/COM1.dat")]
    [InlineData("Cluster/lpt9")]
    [InlineData("Cluster/NUL/x")]
    [InlineData("Cluster/a?b")]
    [InlineData("Cluster/a*b")]
    [InlineData("Cluster/ä")]
    [InlineData("Cluster//x")]
    [InlineData("")]
    public void CheckEntryName_RefusesEverythingElse(string name) =>
        Assert.NotNull(RestoreArchiveRules.CheckEntryName(name, MapKey));

    [Fact]
    public void Check_AcceptsABijection_AndReturnsThePayloadAndDirectories()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10), ("World/111.arkprofile", 2), ("Cluster/transfers/1.dat", 3));
        var entries = Entries(("manifest.json", 99), ("World/TheIsland_WP.ark", 10), ("World/111.arkprofile", 2), ("Cluster/transfers/1.dat", 3), ("Cluster/empty/", 0));

        var result = RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey);

        Assert.Null(result.Problem);
        Assert.Equal(["World/TheIsland_WP.ark", "World/111.arkprofile", "Cluster/transfers/1.dat"], result.Payload.Select(p => p.Entry.Name));
        Assert.Equal(["empty"], result.Directories);
    }

    [Fact]
    public void Check_RefusesAnUnlistedEntry()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10), ("World/222.arktribe", 1));

        Assert.Contains("not in the manifest", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesAListedFileMissingFromTheArchive()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10), ("World/222.arktribe", 1));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10));

        Assert.Contains("not in the archive", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesALengthMismatch()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 11));

        Assert.Contains("11 bytes in the archive, 10 in the manifest", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesEntriesDifferingOnlyByCase()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10), ("World/111.arkprofile", 2));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10), ("World/111.arkprofile", 2), ("World/111.ARKPROFILE", 2));

        Assert.Contains("differing only by case", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesADuplicateManifestPath()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10), ("World/theisland_wp.ark", 10));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10));

        Assert.Contains("twice", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "no manifest.json")]
    [InlineData(2, "more than one manifest.json")]
    public void Check_RequiresExactlyOneManifest(int manifests, string expected)
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10));
        var list = new List<(string, long)> { ("World/TheIsland_WP.ark", 10) };
        for (var i = 0; i < manifests; i++)
        {
            list.Add(("manifest.json", 1));
        }

        Assert.Contains(expected, RestoreArchiveRules.Check(manifest, Entries([.. list]), "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RequiresTheWorldFileOfTheCurrentMap()
    {
        var manifest = Manifest(("World/111.arkprofile", 2));
        var entries = Entries(("manifest.json", 1), ("World/111.arkprofile", 2));

        Assert.Contains("does not contain World/TheIsland_WP.ark", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RequiresTheInstanceSlugAndMapKeyToMatch()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10));

        Assert.Contains("belongs to instance 'alpha', not 'beta'", RestoreArchiveRules.Check(manifest, entries, "beta", MapKey).Problem, StringComparison.Ordinal);
        Assert.Contains("now runs 'Aberration_P'", RestoreArchiveRules.Check(manifest, entries, "alpha", "Aberration_P").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesAnUnsafeNameEvenWhenListed()
    {
        var manifest = Manifest(("World/TheIsland_WP.ark", 10), ("Cluster/../evil.dat", 1));
        var entries = Entries(("manifest.json", 1), ("World/TheIsland_WP.ark", 10), ("Cluster/../evil.dat", 1));

        Assert.Contains("evil.dat", RestoreArchiveRules.Check(manifest, entries, "alpha", MapKey).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_WrittenBeforeClusterFields_DecodesWithNulls()
    {
        const string old = """{"instanceSlug":"alpha","mapKey":"TheIsland_WP","createdAt":"2026-09-07T12:00:00+00:00","files":[{"path":"World/TheIsland_WP.ark","length":1,"sha256":"00"}]}""";

        var manifest = BackupManifest.FromJson(old);

        Assert.NotNull(manifest);
        Assert.Null(manifest.ClusterSlug);
        Assert.False(manifest.ClusterCaptured);
        Assert.True(manifest.ContainsWorldFile);
    }

    [Fact]
    public void Manifest_RoundTripsTheClusterFields()
    {
        var manifest = new BackupManifest("alpha", MapKey, DateTimeOffset.UnixEpoch, [], "main", true);

        var back = BackupManifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.Equal("main", back.ClusterSlug);
        Assert.True(back.ClusterCaptured);
        Assert.Equal(manifest.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void Journal_RoundTrips_AndReferencesEveryAffectedInstance()
    {
        var journal = new RestoreJournal("alpha-20260918-120000-1", DateTimeOffset.UnixEpoch, 1, "alpha", MapKey, 5, "main", [1, 2, 3], @"C:\Ark\Backups\alpha\_restore-safety\20260918-120000-1", "20260918-110000-1.zip", RestorePhase.Replacing);

        var back = RestoreJournal.FromJson(journal.ToJson());

        Assert.NotNull(back);
        Assert.Equal(journal.OperationId, back.OperationId);
        Assert.Equal([1, 2, 3], back.AffectedInstanceIds);
        Assert.Equal(RestorePhase.Replacing, back.Phase);
        Assert.Contains("\"phase\": \"Replacing\"", journal.ToJson(), StringComparison.Ordinal);
        Assert.True(back.IncludesCluster);
        Assert.True(back.References(2));
        Assert.False(back.References(4));
        Assert.Contains("alpha-20260918-120000-1", back.RefusalReason("this instance"), StringComparison.Ordinal);
        Assert.Null(RestoreJournal.FromJson("not json"));
    }

    private static BackupManifest Manifest(params (string Path, long Length)[] files) =>
        new("alpha", MapKey, DateTimeOffset.UnixEpoch, files.Select(f => new BackupManifestEntry(f.Path, f.Length, "00")).ToList(), "main", true);

    private static List<RestoreArchiveRules.ArchiveEntry> Entries(params (string Name, long Length)[] entries) =>
        entries.Select(e => new RestoreArchiveRules.ArchiveEntry(e.Name, e.Length)).ToList();
}
