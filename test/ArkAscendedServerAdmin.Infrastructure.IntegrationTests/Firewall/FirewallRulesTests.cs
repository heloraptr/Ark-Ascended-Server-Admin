using System.Security.Principal;
using ArkAscendedServerAdmin.Infrastructure.Firewall;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsFirewallHelper;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Firewall;

/// <summary>Creates real firewall rules, so it only runs elevated; otherwise it is skipped with the reason.</summary>
public class FirewallRulesTests
{
    private const int InstanceId = 987654;

    [Fact]
    public void EnsureThenChangePortThenRemove_ReconcilesTheRules()
    {
        Assert.SkipUnless(IsElevated(), "Firewall rule tests need an elevated process.");
        Assert.SkipUnless(FirewallManager.IsServiceRunning, "The Windows Firewall service is not running.");

        var rules = new FirewallRules(NullLogger<FirewallRules>.Instance);
        var name = FirewallRules.RuleName(InstanceId);
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
            });
        }
        finally
        {
            rules.RemoveInstanceRules(InstanceId);
        }

        Assert.Empty(PortsOf(name));
    }

    [Fact]
    public void RuleName_UsesTheInstanceId() => Assert.Equal("ArkAscendedServerAdmin-42", FirewallRules.RuleName(42));

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
