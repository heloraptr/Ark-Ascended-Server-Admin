using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.UnitTests.Configuration;

public class DataRootLayoutTests
{
    [Fact]
    public void DerivedPaths_LiveUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-layout-test");
        var layout = new DataRootLayout(root);

        Assert.Equal(Path.GetFullPath(root), layout.Root);
        Assert.All(layout.Directories, d => Assert.StartsWith(layout.Root, d, StringComparison.Ordinal));
        Assert.Equal(Path.Combine(layout.Root, "Server"), layout.Server);
        Assert.Equal(Path.Combine(layout.Root, DataRootLayout.DatabaseFileName), layout.DatabasePath);
        Assert.Equal(Path.Combine(layout.Root, "Server", "steamapps", "appmanifest_2430930.acf"), layout.AppManifestPath);
        Assert.Equal(Path.Combine(layout.Root, "SteamCMD", "steamcmd.exe"), layout.SteamCmdExecutable);
        Assert.Equal(Path.Combine(layout.Root, "Instances", "island"), layout.InstanceDirectory("island"));
        Assert.Equal(Path.Combine(layout.Root, "Clusters", "main"), layout.ClusterDirectory("main"));
    }

    [Fact]
    public void RelativeRoot_IsMadeAbsolute()
    {
        var layout = new DataRootLayout("relative-data");

        Assert.True(Path.IsPathRooted(layout.Root));
    }

    [Fact]
    public void BlankRoot_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new DataRootLayout(" "));
    }
}
