using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ArkAscendedServerAdmin.Networking;

namespace ArkAscendedServerAdmin.Infrastructure.Networking;

/// <summary>
/// <see cref="IHostAddressProvider"/> over <see cref="NetworkInterface.GetAllNetworkInterfaces"/>. Adapters are
/// taken in the order the OS lists them; only those that are up count, loopback and tunnel adapters never do,
/// and adapters whose name or description marks them as a host-side virtual switch (Hyper-V's vEthernet, VMware
/// and VirtualBox host adapters, WSL, Npcap, TAP) are skipped when a physical adapter is present. The guest side
/// of a Hyper-V VM ("Microsoft Hyper-V Network Adapter") is the box's real adapter and is kept.
/// </summary>
public sealed class HostAddressProvider : IHostAddressProvider
{
    private static readonly string[] VirtualMarkers =
    [
        "vEthernet",
        "Hyper-V Virtual Ethernet",
        "VMware Virtual",
        "VirtualBox Host-Only",
        "WSL",
        "Npcap",
        "TAP-Windows",
        "Wintun",
    ];

    public IReadOnlyList<string> GetLanAddresses()
    {
        var physical = new List<string>();
        var virtualOnly = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var target = LooksVirtual(adapter) ? virtualOnly : physical;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                {
                    continue;
                }

                var text = address.ToString();
                if (!target.Contains(text))
                {
                    target.Add(text);
                }
            }
        }

        return physical.Count > 0 ? physical : virtualOnly;
    }

    private static bool LooksVirtual(NetworkInterface adapter) =>
        VirtualMarkers.Any(marker =>
            adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
