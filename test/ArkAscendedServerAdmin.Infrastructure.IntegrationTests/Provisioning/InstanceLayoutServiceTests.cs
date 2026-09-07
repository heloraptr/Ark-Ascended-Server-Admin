using ArkAscendedServerAdmin.Infrastructure.Provisioning;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Provisioning;

public class InstanceLayoutServiceTests
{
    private const string Slug = "alpha";
    private static readonly DateTimeOffset _now = new(2026, 9, 7, 14, 30, 5, TimeSpan.Zero);

    private static InstanceLayoutService CreateService(TempDataRoot root)
    {
        FakeServerTree.Create(root.Layout);
        return new InstanceLayoutService(root.Layout, new FixedTimeProvider(_now));
    }

    private static IEnumerable<string> JunctionLinks(TempDataRoot root) =>
        FakeServerTree.JunctionPaths.Select(relative => Path.Combine(root.Layout.InstanceDirectory(Slug), relative));

    [Fact]
    public async Task Ensure_CreatesJunctionsAndRealSaved_AndIsIdempotent()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);

        await service.EnsureAsync(Slug, ct);

        Assert.True(service.IsComplete(Slug));
        foreach (var link in JunctionLinks(root))
        {
            Assert.True(FakeServerTree.IsReparsePoint(link), link);
            Assert.True(File.Exists(Path.Combine(link, FakeServerTree.MarkerFileName)), $"marker not visible through {link}");
        }

        var saved = root.Layout.InstanceSavedDirectory(Slug);
        Assert.True(Directory.Exists(saved));
        Assert.False(FakeServerTree.IsReparsePoint(saved));
        Assert.True(Directory.Exists(root.Layout.InstanceGeneratedConfigDirectory(Slug)));

        // .NET's own view of the junction agrees with the reparse data the service wrote.
        var engineTarget = new DirectoryInfo(Path.Combine(root.Layout.InstanceDirectory(Slug), "Engine")).LinkTarget;
        Assert.Equal(Path.Combine(root.Layout.Server, "Engine"), engineTarget, ignoreCase: true);

        var before = JunctionLinks(root).Select(link => Directory.GetCreationTimeUtc(link)).ToList();
        await service.EnsureAsync(Slug, ct);

        Assert.True(service.IsComplete(Slug));
        Assert.Equal(before, JunctionLinks(root).Select(link => Directory.GetCreationTimeUtc(link)).ToList());
        Assert.True(FakeServerTree.TargetsIntact(root.Layout));
    }

    [Fact]
    public async Task Ensure_RepairsAJunctionWithTheWrongTarget()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        var wrongTarget = Path.Combine(root.Layout.Root, "Elsewhere");
        Directory.CreateDirectory(wrongTarget);
        File.WriteAllText(Path.Combine(wrongTarget, "wrong.txt"), "x");
        var contentLink = Path.Combine(root.Layout.InstanceDirectory(Slug), "ShooterGame", "Content");
        FakeServerTree.MklinkJunction(contentLink, wrongTarget);
        Directory.CreateDirectory(root.Layout.InstanceSavedDirectory(Slug));

        Assert.False(service.IsComplete(Slug));

        await service.EnsureAsync(Slug, ct);

        Assert.True(service.IsComplete(Slug));
        Assert.True(File.Exists(Path.Combine(contentLink, FakeServerTree.MarkerFileName)));
        Assert.False(File.Exists(Path.Combine(contentLink, "wrong.txt")));
        // Repair deleted the junction entry only; the old target and its content survived.
        Assert.True(File.Exists(Path.Combine(wrongTarget, "wrong.txt")));
    }

    [Fact]
    public async Task Ensure_RefusesToReplaceARealDirectory()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        var engineLink = Path.Combine(root.Layout.InstanceDirectory(Slug), "Engine");
        Directory.CreateDirectory(engineLink);
        File.WriteAllText(Path.Combine(engineLink, "keep.txt"), "x");

        await Assert.ThrowsAsync<IOException>(() => service.EnsureAsync(Slug, ct));

        Assert.True(File.Exists(Path.Combine(engineLink, "keep.txt")));
    }

    [Fact]
    public async Task IsComplete_FalseWhenAJunctionIsMissing()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        await service.EnsureAsync(Slug, ct);

        Directory.Delete(Path.Combine(root.Layout.InstanceDirectory(Slug), "ShooterGame", "Plugins"), recursive: false);

        Assert.False(service.IsComplete(Slug));
        Assert.True(FakeServerTree.TargetsIntact(root.Layout));
    }

    [Fact]
    public void IsComplete_FalseForAnUnknownSlug()
    {
        using var root = new TempDataRoot();
        var service = CreateService(root);

        Assert.False(service.IsComplete("never-created"));
    }

    [Fact]
    public async Task RemoveJunctions_LeavesTargetsIntact_AndKeepsSaved()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        await service.EnsureAsync(Slug, ct);
        var savedFile = Path.Combine(root.Layout.InstanceSavedDirectory(Slug), "world.ark");
        File.WriteAllText(savedFile, "world");
        Directory.CreateDirectory(root.Layout.InstanceConfigSourceDirectory(Slug));
        File.WriteAllText(Path.Combine(root.Layout.InstanceConfigSourceDirectory(Slug), "Game.ini"), "[x]");

        await service.RemoveJunctionsAsync(Slug, ct);

        Assert.All(JunctionLinks(root), link => Assert.False(Directory.Exists(link), link));
        Assert.True(FakeServerTree.TargetsIntact(root.Layout));
        Assert.True(File.Exists(savedFile));
        Assert.False(Directory.Exists(root.Layout.InstanceConfigSourceDirectory(Slug)));
        Assert.False(service.IsComplete(Slug));

        // Idempotent: a second removal is a no-op.
        await service.RemoveJunctionsAsync(Slug, ct);
        Assert.True(File.Exists(savedFile));
    }

    [Fact]
    public async Task Retire_KeepWorldData_MovesSavedToArchive_AndRemovesInstance()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        await service.EnsureAsync(Slug, ct);
        var worldDirectory = root.Layout.InstanceWorldDirectory(Slug, "TheIsland_WP");
        Directory.CreateDirectory(worldDirectory);
        File.WriteAllText(Path.Combine(worldDirectory, "TheIsland_WP.ark"), "world");

        var archivePath = await service.RetireAsync(Slug, keepWorldData: true, ct);

        Assert.Equal(root.Layout.ArchiveDirectory(Slug, _now), archivePath);
        Assert.True(File.Exists(Path.Combine(archivePath!, Slug, "TheIsland_WP", "TheIsland_WP.ark")));
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory(Slug)));
        Assert.True(FakeServerTree.TargetsIntact(root.Layout));
    }

    [Fact]
    public async Task Retire_DeleteWorldData_RemovesEverything()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);
        await service.EnsureAsync(Slug, ct);
        File.WriteAllText(Path.Combine(root.Layout.InstanceSavedDirectory(Slug), "world.ark"), "world");

        var archivePath = await service.RetireAsync(Slug, keepWorldData: false, ct);

        Assert.Null(archivePath);
        Assert.False(Directory.Exists(root.Layout.InstanceDirectory(Slug)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Layout.Archive));
        Assert.True(FakeServerTree.TargetsIntact(root.Layout));
    }

    [Fact]
    public async Task Retire_WithoutAnInstanceDirectory_IsANoOp()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var service = CreateService(root);

        Assert.Null(await service.RetireAsync(Slug, keepWorldData: true, ct));
    }
}
