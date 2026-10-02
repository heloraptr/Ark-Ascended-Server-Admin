using System.Text.RegularExpressions;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Firewall;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

/// <summary>The file the app writes at startup for <c>uninstall.ps1</c>.</summary>
public sealed partial class FirewallTagFileTests
{
    [Fact]
    public async Task Write_PutsExactlyTheTagAndANewlineInTheBaseDirectory()
    {
        using var root = new TempDataRoot();
        var file = new FirewallTagFile(root.Layout, new RecordingLogger<FirewallTagFile>());
        var path = Path.Combine(AppContext.BaseDirectory, FirewallTagFile.FileName);
        try
        {
            await file.WriteAsync(TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(FirewallRuleNames.InstallTag(root.Layout.Root) + "\n", text);
            Assert.Matches(UninstallerTagPattern(), text.Trim());
            Assert.Equal(9, new FileInfo(path).Length); // no byte-order mark, no carriage return
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_WhenTheFolderCannotBeWritten_LogsAWarningAndCarriesOn()
    {
        using var root = new TempDataRoot();
        var logger = new RecordingLogger<FirewallTagFile>();
        var blocker = Path.Combine(root.Layout.Root, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "", TestContext.Current.CancellationToken);

        // A file where the folder should be: creating the directory fails with an IOException.
        await new FirewallTagFile(root.Layout, logger).WriteAsync(blocker, TestContext.Current.CancellationToken);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(FirewallTagFile.FileName, warning.Message, StringComparison.Ordinal);
    }

    // The same check install\ArkInstall.Common.ps1 applies before it trusts the file.
    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex UninstallerTagPattern();
}
