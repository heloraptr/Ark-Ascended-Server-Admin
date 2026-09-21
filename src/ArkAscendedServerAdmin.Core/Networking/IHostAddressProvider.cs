namespace ArkAscendedServerAdmin.Networking;

/// <summary>
/// The addresses this box answers on (B9, the Connection card). One call reads the network interfaces once;
/// nothing is cached, and nothing on the network is contacted. Fakeable so the command facade tests can hand
/// the card fixed addresses.
/// </summary>
public interface IHostAddressProvider
{
    /// <summary>
    /// The non-loopback IPv4 addresses of interfaces that are up, in interface order, without duplicates.
    /// Link-local (169.254.x.x) addresses and adapters that look virtual are left out when they can be told
    /// apart; when that leaves nothing, every non-loopback address is returned so the card is never empty on a
    /// box whose only adapter is a virtual one. Empty when the box has no usable interface.
    /// </summary>
    IReadOnlyList<string> GetLanAddresses();
}
