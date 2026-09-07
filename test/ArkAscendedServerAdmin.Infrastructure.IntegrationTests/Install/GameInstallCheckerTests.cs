using ArkAscendedServerAdmin.Infrastructure.Install;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Install;

public class GameInstallCheckerTests
{
    [Fact]
    public void EmptyDataRoot_IsIncomplete()
    {
        using var root = new TempDataRoot();

        var status = new GameInstallChecker(root.Layout).Check();

        Assert.False(status.SteamCmdPresent);
        Assert.False(status.InstallComplete);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void ServerDirectoryAlone_IsNotEnough()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(root.Layout.ServerExecutable)!);
        File.WriteAllText(root.Layout.ServerExecutable, "stub");
        File.WriteAllText(root.Layout.SteamCmdExecutable, "stub");

        var status = new GameInstallChecker(root.Layout).Check();

        Assert.True(status.SteamCmdPresent);
        Assert.False(status.InstallComplete);
    }

    [Fact]
    public void ManifestWithStateFlags4_AndSteamCmd_IsComplete()
    {
        using var root = new TempDataRoot();
        WriteManifest(root, 4);
        File.WriteAllText(root.Layout.SteamCmdExecutable, "stub");

        var status = new GameInstallChecker(root.Layout).Check();

        Assert.True(status.IsComplete);
    }

    [Fact]
    public void ManifestWithUpdateInProgress_IsIncomplete()
    {
        using var root = new TempDataRoot();
        WriteManifest(root, 1026);
        File.WriteAllText(root.Layout.SteamCmdExecutable, "stub");

        var status = new GameInstallChecker(root.Layout).Check();

        Assert.False(status.IsComplete);
        Assert.Contains("1026", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSteamCmd_IsIncompleteEvenWithAVerifiedInstall()
    {
        using var root = new TempDataRoot();
        WriteManifest(root, 4);

        var status = new GameInstallChecker(root.Layout).Check();

        Assert.True(status.InstallComplete);
        Assert.False(status.IsComplete);
    }

    private static void WriteManifest(TempDataRoot root, int stateFlags)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root.Layout.AppManifestPath)!);
        File.WriteAllText(root.Layout.AppManifestPath, $"\"AppState\"\n{{\n\t\"appid\"\t\t\"2430930\"\n\t\"StateFlags\"\t\t\"{stateFlags}\"\n}}\n");
    }
}
