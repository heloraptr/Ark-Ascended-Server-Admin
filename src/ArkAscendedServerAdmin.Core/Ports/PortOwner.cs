namespace ArkAscendedServerAdmin.Ports;

/// <summary>
/// A defined instance as the port allocator sees it (plan step 15). The game port occupies
/// <see cref="GamePort"/> and <c>GamePort + 1</c> (both UDP); the RCON port is TCP. Inside the manager
/// every port is treated as one protocol-agnostic namespace, so a game range may not overlap another
/// instance's RCON port and vice versa.
/// </summary>
public sealed record PortOwner(string Name, int GamePort, int RconPort);
