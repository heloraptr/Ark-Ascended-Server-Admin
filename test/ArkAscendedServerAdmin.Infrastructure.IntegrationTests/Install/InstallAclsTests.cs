using System.Security.AccessControl;
using System.Security.Principal;
using ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Install;

/// <summary>
/// Runs the installer's <c>Set-InstallAcls</c> against a temp data root. Writing a DACL that leaves the current user
/// out needs an elevated process, so it is skipped otherwise.
/// </summary>
public class InstallAclsTests
{
    private static readonly SecurityIdentifier _users = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void SetInstallAcls_KeepsUsersOutOfInstancesAndClusters_ButLetsThemReadTheRoot()
    {
        Assert.SkipUnless(IsElevated(), "Applying installer ACLs needs an elevated process.");
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");
        var root = Path.Combine(Path.GetTempPath(), $"ArkAdminAclTests-{Guid.NewGuid():N}");
        var installDir = Path.Combine(root, "App");
        var dataRoot = Path.Combine(root, "Data");
        try
        {
            var (exitCode, _, error) = ChildPowerShell.Run($"Set-InstallAcls {ChildPowerShell.Quote(installDir)} {ChildPowerShell.Quote(dataRoot)}");

            Assert.True(exitCode == 0, error);
            Assert.True(HasUsersRule(dataRoot), "the data root should stay readable by Users");
            Assert.False(HasUsersRule(Path.Combine(dataRoot, "Instances")), "Instances must not be readable by Users");
            Assert.False(HasUsersRule(Path.Combine(dataRoot, "Clusters")), "Clusters must not be readable by Users");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool HasUsersRule(string directory) =>
        new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule => _users.Equals(rule.IdentityReference));

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
