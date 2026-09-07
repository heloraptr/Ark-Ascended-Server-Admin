using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Infrastructure.Data;
using ArkAscendedServerAdmin.Infrastructure.Provisioning;
using ArkAscendedServerAdmin.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Provisioning;

public class IniSourceStoreTests
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Text = "[ServerSettings]\r\nServerAdminPassword=hunter2\r\n";

    private static IniSourceStore CreateStore(TempDataRoot root, IDbContextFactory<AppDbContext>? factory = null) =>
        new(root.Layout, factory ?? root, TimeProvider.System, NullLogger<IniSourceStore>.Instance);

    private static string ClusterFile(TempDataRoot root, IniFile file) =>
        Path.Combine(root.Layout.ClusterConfigSourceDirectory(Seed.ClusterSlug), file == IniFile.Game ? "Game.ini" : "GameUserSettings.ini");

    [Fact]
    public async Task Load_MissingFile_IsEmptyWithTheEmptyHashAndAStaleMirror()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);

        var document = await store.LoadAsync(IniOwner.ForCluster(ids.ClusterId), IniFile.Game, ct);

        Assert.Equal(string.Empty, document.Text);
        Assert.Equal(EmptyHash, document.Sha256);
        Assert.True(document.MirrorStale);
    }

    [Fact]
    public async Task Load_UnknownOwner_Throws()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(IniOwner.ForInstance(9999), IniFile.Game, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(new IniOwner(null, null), IniFile.Game, ct));
    }

    [Fact]
    public async Task Save_WritesTheFileAndTheMirror()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);
        var owner = IniOwner.ForCluster(ids.ClusterId);

        var result = await store.SaveAsync(owner, IniFile.GameUserSettings, Text, EmptyHash, ct);

        Assert.True(result.Succeeded);
        Assert.False(result.MirrorFailed);
        Assert.Equal(IniSourceStore.ComputeSha256(Text), result.NewSha256);

        var path = ClusterFile(root, IniFile.GameUserSettings);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        Assert.Equal(Text, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "a BOM was written");

        await using var db = root.CreateDbContext();
        var row = Assert.Single(await db.IniDocuments.ToListAsync(ct));
        Assert.Equal(ids.ClusterId, row.ClusterId);
        Assert.Null(row.InstanceId);
        Assert.Equal(IniFile.GameUserSettings, row.File);
        Assert.Equal(Text, row.Text);
        Assert.Equal(result.NewSha256, row.Sha256);

        var loaded = await store.LoadAsync(owner, IniFile.GameUserSettings, ct);
        Assert.Equal(Text, loaded.Text);
        Assert.Equal(result.NewSha256, loaded.Sha256);
        Assert.False(loaded.MirrorStale);
    }

    [Fact]
    public async Task Save_ForAnInstance_UsesTheInstanceColumnAndDirectory()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);

        var result = await store.SaveAsync(IniOwner.ForInstance(ids.StandaloneInstanceId), IniFile.Game, Text, EmptyHash, ct);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(root.Layout.InstanceConfigSourceDirectory(Seed.StandaloneSlug), "Game.ini")));
        await using var db = root.CreateDbContext();
        var row = Assert.Single(await db.IniDocuments.ToListAsync(ct));
        Assert.Equal(ids.StandaloneInstanceId, row.InstanceId);
        Assert.Null(row.ClusterId);
    }

    [Fact]
    public async Task Save_WithAStaleHash_IsRejectedAndLeavesTheFileUntouched()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);
        var owner = IniOwner.ForCluster(ids.ClusterId);
        await store.SaveAsync(owner, IniFile.Game, Text, EmptyHash, ct);

        var result = await store.SaveAsync(owner, IniFile.Game, "[Other]\r\n", EmptyHash, ct);

        Assert.False(result.Succeeded);
        Assert.Contains("changed since you opened it", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.NewSha256);
        Assert.Equal(Text, await File.ReadAllTextAsync(ClusterFile(root, IniFile.Game), ct));
    }

    [Fact]
    public async Task Save_IsAtomic_NoTempFileRemains()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);
        var owner = IniOwner.ForCluster(ids.ClusterId);

        var first = await store.SaveAsync(owner, IniFile.Game, Text, EmptyHash, ct);
        await store.SaveAsync(owner, IniFile.Game, Text + "X=1\r\n", first.NewSha256!, ct);

        var entries = Directory.GetFileSystemEntries(root.Layout.ClusterConfigSourceDirectory(Seed.ClusterSlug));
        Assert.Equal([ClusterFile(root, IniFile.Game)], entries);
    }

    [Fact]
    public async Task Save_WhenTheMirrorFails_ReportsIt_AndRetryMirrorFixesIt()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var factory = new ToggleFailureContextFactory(root);
        var store = CreateStore(root, factory);
        var owner = IniOwner.ForCluster(ids.ClusterId);

        // Resolve the slug while the database is healthy, then fail every later context.
        await store.LoadAsync(owner, IniFile.Game, ct);
        factory.Fail = true;
        var saved = await store.SaveAsync(owner, IniFile.Game, Text, EmptyHash, ct);

        Assert.True(saved.Succeeded);
        Assert.True(saved.MirrorFailed);
        Assert.Equal(Text, await File.ReadAllTextAsync(ClusterFile(root, IniFile.Game), ct));
        Assert.True((await store.LoadAsync(owner, IniFile.Game, ct)).MirrorStale);

        factory.Fail = false;
        var retried = await store.RetryMirrorAsync(owner, IniFile.Game, ct);

        Assert.True(retried.Succeeded);
        Assert.False(retried.MirrorFailed);
        Assert.Equal(saved.NewSha256, retried.NewSha256);
        Assert.False((await store.LoadAsync(owner, IniFile.Game, ct)).MirrorStale);
        await using var db = root.CreateDbContext();
        Assert.Equal(Text, (await db.IniDocuments.SingleAsync(ct)).Text);
    }

    [Fact]
    public async Task RestoreFromDatabase_RecreatesEveryFile()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);
        var cluster = IniOwner.ForCluster(ids.ClusterId);
        var instance = IniOwner.ForInstance(ids.StandaloneInstanceId);
        await store.SaveAsync(cluster, IniFile.Game, "[cluster-game]\r\n", EmptyHash, ct);
        await store.SaveAsync(cluster, IniFile.GameUserSettings, "[cluster-gus]\r\n", EmptyHash, ct);
        await store.SaveAsync(instance, IniFile.GameUserSettings, "[instance-gus]\r\n", EmptyHash, ct);
        Directory.Delete(root.Layout.ClusterDirectory(Seed.ClusterSlug), recursive: true);
        Directory.Delete(root.Layout.InstanceDirectory(Seed.StandaloneSlug), recursive: true);

        // A fresh store: nothing cached from the saves above.
        await CreateStore(root).RestoreFromDatabaseAsync(ct);

        Assert.Equal("[cluster-game]\r\n", await File.ReadAllTextAsync(ClusterFile(root, IniFile.Game), ct));
        Assert.Equal("[cluster-gus]\r\n", await File.ReadAllTextAsync(ClusterFile(root, IniFile.GameUserSettings), ct));
        Assert.Equal(
            "[instance-gus]\r\n",
            await File.ReadAllTextAsync(Path.Combine(root.Layout.InstanceConfigSourceDirectory(Seed.StandaloneSlug), "GameUserSettings.ini"), ct));
        Assert.False((await store.LoadAsync(cluster, IniFile.Game, ct)).MirrorStale);
    }

    [Fact]
    public async Task Load_ReportsStale_WhenTheFileWasEditedOutsideTheManager()
    {
        using var root = new TempDataRoot();
        var ct = TestContext.Current.CancellationToken;
        var ids = await Seed.CreateAsync(root, ct);
        var store = CreateStore(root);
        var owner = IniOwner.ForCluster(ids.ClusterId);
        await store.SaveAsync(owner, IniFile.Game, Text, EmptyHash, ct);

        await File.WriteAllTextAsync(ClusterFile(root, IniFile.Game), Text + "Edited=1\r\n", ct);

        var document = await store.LoadAsync(owner, IniFile.Game, ct);
        Assert.True(document.MirrorStale);
        Assert.Equal(Text + "Edited=1\r\n", document.Text);
    }
}
