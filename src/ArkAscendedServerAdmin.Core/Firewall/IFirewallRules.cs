namespace ArkAscendedServerAdmin.Firewall;

/// <summary>
/// Port-based inbound firewall rules (plan step 26): UDP game port and port + 1 only, never RCON. Rules are
/// named <c>ArkAscendedServerAdmin-&lt;tag&gt;-&lt;instanceId&gt;</c>, where the tag identifies the installation
/// (<see cref="FirewallRuleNames.InstallTag"/>); reconciled on every Start, removed on delete.
/// </summary>
public interface IFirewallRules
{
    /// <summary>The name this installation gives the instance's rules; shown on the Connection card.</summary>
    string RuleName(int instanceId);

    /// <summary>Creates or updates the instance's rules so they match <paramref name="gamePort"/>; idempotent.</summary>
    void EnsureInstanceRules(int instanceId, int gamePort);

    void RemoveInstanceRules(int instanceId);

    /// <summary>
    /// Whether at least one rule named for the instance exists (B9, the Connection card). Ports are not
    /// checked; a stale rule counts. Throws <see cref="InvalidOperationException"/> when the firewall cannot
    /// be read, so callers can show "unknown" instead of "missing".
    /// </summary>
    bool InstanceRulesExist(int instanceId);
}
