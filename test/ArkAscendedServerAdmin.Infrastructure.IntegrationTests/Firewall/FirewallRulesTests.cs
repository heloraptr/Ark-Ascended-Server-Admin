using System.Security.Principal;
using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Firewall;
using ArkAscendedServerAdmin.Infrastructure.Firewall;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsFirewallHelper;
using FirewallWASRule = WindowsFirewallHelper.FirewallRules.FirewallWASRule;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

/// <summary>Creates real firewall rules, so it only runs elevated; otherwise it is skipped with the reason.</summary>
public class FirewallRulesTests
{
    private const int InstanceId = 987654;

    // Two installations on one machine; nothing is created on disk, only the tag is derived from the paths.
    private static readonly string _rootA = Path.Combine(Path.GetTempPath(), "ArkAdminFirewallTests", "A");
    private static readonly string _rootB = Path.Combine(Path.GetTempPath(), "ArkAdminFirewallTests", "B");

    [Fact]
    public void EnsureThenChangePortThenRemove_ReconcilesTheRules()
    {
        SkipUnlessElevated();

        var rules = Create(_rootA);
        var name = rules.RuleName(InstanceId);
        try
        {
            rules.EnsureInstanceRules(InstanceId, 47777);
            Assert.Equal([47777, 47778], PortsOf(name));

            rules.EnsureInstanceRules(InstanceId, 47777);
            Assert.Equal([47777, 47778], PortsOf(name));

            rules.EnsureInstanceRules(InstanceId, 47790);
            Assert.Equal([47790, 47791], PortsOf(name));
            Assert.All(FirewallManager.Instance.Rules.Where(rule => rule.Name == name), rule =>
            {
                Assert.Equal(FirewallDirection.Inbound, rule.Direction);
                Assert.True(rule.Protocol.Equals(FirewallProtocol.UDP));
                Assert.Equal(FirewallAction.Allow, rule.Action);
                var port = rule.LocalPorts.Single();
                Assert.Equal(FirewallRuleNames.Description(InstanceId, port, new DataRootLayout(_rootA).Root), Assert.IsType<FirewallWASRule>(rule, exactMatch: false).Description);
            });
        }
        finally
        {
            rules.RemoveInstanceRules(InstanceId);
        }

        Assert.Empty(PortsOf(name));
    }

    [Fact]
    public void TwoInstallations_WithTheSameInstanceIdAndPort_KeepSeparateRules()
    {
        SkipUnlessElevated();

        var a = Create(_rootA);
        var b = Create(_rootB);
        Assert.NotEqual(a.RuleName(InstanceId), b.RuleName(InstanceId));
        try
        {
            a.EnsureInstanceRules(InstanceId, 47777);
            b.EnsureInstanceRules(InstanceId, 47777);
            Assert.Equal([47777, 47778], PortsOf(a.RuleName(InstanceId)));
            Assert.Equal([47777, 47778], PortsOf(b.RuleName(InstanceId)));

            a.RemoveInstanceRules(InstanceId);

            Assert.Empty(PortsOf(a.RuleName(InstanceId)));
            Assert.False(a.InstanceRulesExist(InstanceId));
            Assert.Equal([47777, 47778], PortsOf(b.RuleName(InstanceId)));
            Assert.True(b.InstanceRulesExist(InstanceId));
        }
        finally
        {
            a.RemoveInstanceRules(InstanceId);
            b.RemoveInstanceRules(InstanceId);
        }
    }

    /// <summary>
    /// The uninstall path end to end: the tag file the app writes, read by <c>Read-InstanceFirewallTag</c> and fed
    /// to <c>Remove-InstanceFirewallRules</c> in a child Windows PowerShell, removes this installation's rules only.
    /// </summary>
    [Fact]
    public async Task UninstallScript_RemovesOnlyTheRulesOfTheTagInTheFile()
    {
        SkipUnlessElevated();
        Assert.SkipWhen(ChildPowerShell.UnavailableReason is not null, ChildPowerShell.UnavailableReason ?? "");

        var a = Create(_rootA);
        var b = Create(_rootB);
        var installDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ArkAdminTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            a.EnsureInstanceRules(InstanceId, 47777);
            b.EnsureInstanceRules(InstanceId, 47777);
            await new FirewallTagFile(new DataRootLayout(_rootA), NullLogger<FirewallTagFile>.Instance)
                .WriteAsync(installDir, TestContext.Current.CancellationToken);

            var (exitCode, lines, error) = ChildPowerShell.Run(
                $"Remove-InstanceFirewallRules (Read-InstanceFirewallTag {ChildPowerShell.Quote(installDir)})");

            Assert.True(exitCode == 0, error + string.Join(Environment.NewLine, lines));
            Assert.Empty(PortsOf(a.RuleName(InstanceId)));
            Assert.Equal([47777, 47778], PortsOf(b.RuleName(InstanceId)));
        }
        finally
        {
            a.RemoveInstanceRules(InstanceId);
            b.RemoveInstanceRules(InstanceId);
            Directory.Delete(installDir, recursive: true);
        }
    }

    [Fact]
    public void RuleName_CarriesTheTagOfTheDataRootAndTheInstanceId()
    {
        var tag = FirewallRuleNames.InstallTag(_rootA);

        Assert.Equal($"ArkAscendedServerAdmin-{tag}-42", Create(_rootA).RuleName(42));
        Assert.Equal(Create(_rootA).RuleName(42), Create(_rootA.ToUpperInvariant() + Path.DirectorySeparatorChar).RuleName(42));
    }

    private static FirewallRules Create(string root) => new(new DataRootLayout(root), NullLogger<FirewallRules>.Instance);

    private static void SkipUnlessElevated()
    {
        Assert.SkipUnless(IsElevated(), "Firewall rule tests need an elevated process.");
        Assert.SkipUnless(FirewallManager.IsServiceRunning, "The Windows Firewall service is not running.");
    }

    private static List<int> PortsOf(string name) =>
        FirewallManager.Instance.Rules
            .Where(rule => rule.Name == name)
            .SelectMany(rule => rule.LocalPorts.Select(port => (int)port))
            .Order()
            .ToList();

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
