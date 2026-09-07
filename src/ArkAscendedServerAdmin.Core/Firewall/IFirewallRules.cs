namespace ArkAscendedServerAdmin.Firewall;

/// <summary>
/// Port-based inbound firewall rules (plan step 26): UDP game port and port + 1 only, never RCON. Rules are
/// named <c>ArkAscendedServerAdmin-&lt;instanceId&gt;</c>, reconciled on every Start, removed on delete.
/// </summary>
public interface IFirewallRules
{
    /// <summary>Creates or updates the instance's rules so they match <paramref name="gamePort"/>; idempotent.</summary>
    void EnsureInstanceRules(int instanceId, int gamePort);

    void RemoveInstanceRules(int instanceId);
}
