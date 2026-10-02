using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Firewall;
using Microsoft.Extensions.Logging;
using WindowsFirewallHelper;
using FirewallWASRule = WindowsFirewallHelper.FirewallRules.FirewallWASRule;

namespace ArkAscendedServerAdmin.Infrastructure.Firewall;

/// <summary>
/// <see cref="IFirewallRules"/> over WindowsFirewallHelper (plan step 26). Each instance owns two inbound
/// UDP port rules — one for the game port, one for game port + 1 — that share the name
/// <c>ArkAscendedServerAdmin-&lt;tag&gt;-&lt;instanceId&gt;</c> across all profiles; the description names the
/// port and the DataRoot. The tag comes from this installation's DataRoot, so only rules this installation
/// created are ever matched, replaced or removed; rules of another install, and old untagged names, are
/// left alone. <see cref="EnsureInstanceRules"/> removes same-named rules whose port no longer matches and
/// creates the missing ones. Failures (no rights, firewall service stopped) are logged and rethrown as
/// <see cref="InvalidOperationException"/>; callers treat them as advisory and never fail a Start on them.
/// </summary>
public sealed class FirewallRules(DataRootLayout layout, ILogger<FirewallRules> logger) : IFirewallRules
{
    private const FirewallProfiles AllProfiles = FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public;

    private readonly string _tag = FirewallRuleNames.InstallTag(layout.Root);

    public string RuleName(int instanceId) => FirewallRuleNames.RuleName(_tag, instanceId);

    public void EnsureInstanceRules(int instanceId, int gamePort)
    {
        if (gamePort is < 1 or > 65534)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePort), gamePort, "The game port must leave room for port + 1.");
        }

        var name = RuleName(instanceId);
        var wantedPorts = new HashSet<ushort> { (ushort)gamePort, (ushort)(gamePort + 1) };
        try
        {
            var firewall = FirewallManager.Instance;
            var rules = firewall.Rules;
            foreach (var rule in rules.Where(rule => string.Equals(rule.Name, name, StringComparison.Ordinal)).ToList())
            {
                var port = SinglePort(rule);
                if (port is not null && wantedPorts.Contains(port.Value) && IsInboundUdp(rule))
                {
                    wantedPorts.Remove(port.Value);
                }
                else
                {
                    rules.Remove(rule);
                    logger.LogInformation("Removed stale firewall rule {Rule} ({Ports}).", name, DescribePorts(rule));
                }
            }

            foreach (var port in wantedPorts.Order())
            {
                var rule = firewall.CreatePortRule(AllProfiles, name, FirewallAction.Allow, port, FirewallProtocol.UDP);
                rule.Direction = FirewallDirection.Inbound;
                if (rule is FirewallWASRule withDescription)
                {
                    // Every Windows the app supports returns this type; the legacy API has no description.
                    withDescription.Description = FirewallRuleNames.Description(instanceId, port, layout.Root);
                }

                rules.Add(rule);
                logger.LogInformation("Created firewall rule {Rule} for UDP {Port}.", name, port);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reconcile firewall rules {Rule} for UDP {GamePort}-{GamePortPlusOne}.", name, gamePort, gamePort + 1);
            throw new InvalidOperationException($"Firewall rules '{name}' could not be reconciled: {ex.Message}", ex);
        }
    }

    public void RemoveInstanceRules(int instanceId)
    {
        var name = RuleName(instanceId);
        try
        {
            var rules = FirewallManager.Instance.Rules;
            foreach (var rule in rules.Where(rule => string.Equals(rule.Name, name, StringComparison.Ordinal)).ToList())
            {
                rules.Remove(rule);
                logger.LogInformation("Removed firewall rule {Rule} ({Ports}).", name, DescribePorts(rule));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove firewall rules {Rule}.", name);
            throw new InvalidOperationException($"Firewall rules '{name}' could not be removed: {ex.Message}", ex);
        }
    }

    public bool InstanceRulesExist(int instanceId)
    {
        var name = RuleName(instanceId);
        try
        {
            return FirewallManager.Instance.Rules.Any(rule => string.Equals(rule.Name, name, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read firewall rules {Rule}.", name);
            throw new InvalidOperationException($"Firewall rules '{name}' could not be read: {ex.Message}", ex);
        }
    }

    private static ushort? SinglePort(IFirewallRule rule)
    {
        var ports = rule.LocalPorts;
        return ports is { Length: 1 } ? ports[0] : null;
    }

    private static bool IsInboundUdp(IFirewallRule rule) =>
        rule.Direction == FirewallDirection.Inbound && rule.Protocol.Equals(FirewallProtocol.UDP);

    private static string DescribePorts(IFirewallRule rule)
    {
        var ports = rule.LocalPorts;
        return ports is { Length: > 0 } ? string.Join(",", ports) : "no port";
    }
}
