using ArkAscendedServerAdmin.Configuration;

namespace ArkAscendedServerAdmin.Ports;

/// <summary>
/// Port allocation and collision detection (plan step 15, DESIGN §7). The wizard pre-fills the next free
/// game/RCON ports from the App Settings start/step; a start is refused when the ports collide with
/// another defined instance, the web UI, or an OS listener. The collision refusal is load-bearing: the
/// game does not report a failed bind, it runs "healthy" without a game port.
/// </summary>
/// <remarks>
/// Pure: the caller supplies the defined instances, the Kestrel port, and (before launch, Phase 4) the
/// OS listener sets from <c>GetActiveUdpListeners</c> / <c>GetActiveTcpListeners</c>. Ports are compared
/// as one protocol-agnostic namespace, which is conservative but keeps the rules explainable.
/// </remarks>
public sealed class PortAllocator(AppSettings settings)
{
    /// <summary>Highest usable game port: the game also binds port + 1.</summary>
    public const int MaxGamePort = 65534;

    public const int MaxRconPort = 65535;

    /// <summary>
    /// First game port at or above <see cref="AppSettings.GamePortStart"/>, stepping by
    /// <see cref="AppSettings.GamePortStep"/>, whose port and port + 1 are both free of every instance's
    /// game range and RCON port and of <paramref name="hostPort"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No candidate at or below <see cref="MaxGamePort"/> is free.</exception>
    public int NextGamePort(IEnumerable<PortOwner> instances, int hostPort)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var occupied = OccupiedPorts(instances, hostPort);
        var step = RequireStep(settings.GamePortStep, nameof(AppSettings.GamePortStep));

        for (var port = settings.GamePortStart; port <= MaxGamePort; port += step)
        {
            if (port >= 1 && !occupied.Contains(port) && !occupied.Contains(port + 1))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"No free game port at or above {settings.GamePortStart} (step {step}) up to {MaxGamePort}.");
    }

    /// <summary>
    /// First RCON port at or above <see cref="AppSettings.RconPortStart"/>, stepping by
    /// <see cref="AppSettings.RconPortStep"/>, that is free of every instance's game range and RCON port
    /// and of <paramref name="hostPort"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No candidate at or below <see cref="MaxRconPort"/> is free.</exception>
    public int NextRconPort(IEnumerable<PortOwner> instances, int hostPort)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var occupied = OccupiedPorts(instances, hostPort);
        var step = RequireStep(settings.RconPortStep, nameof(AppSettings.RconPortStep));

        for (var port = settings.RconPortStart; port <= MaxRconPort; port += step)
        {
            if (port >= 1 && !occupied.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"No free RCON port at or above {settings.RconPortStart} (step {step}) up to {MaxRconPort}.");
    }

    /// <summary>
    /// Every reason <paramref name="candidate"/> may not use its ports, in a stable order: out-of-range
    /// ports, the candidate's game range overlapping its own RCON port, overlaps with each other owner
    /// (an entry whose <see cref="PortOwner.Name"/> equals the candidate's, ignoring case, is skipped so a
    /// saved instance can be re-checked against the rest), the web UI's <paramref name="hostPort"/>, and
    /// finally OS listeners: the game port and port + 1 against <paramref name="activeUdpPorts"/>, the
    /// RCON port against <paramref name="activeTcpPorts"/>. An empty list means the launch may proceed.
    /// </summary>
    /// <remarks>
    /// The wizard passes <see langword="null"/> for the OS sets; the launch path (Phase 4) passes the live
    /// listener tables, taken while the candidate's own process is not running.
    /// </remarks>
    public IReadOnlyList<PortConflict> FindConflicts(
        PortOwner candidate,
        IEnumerable<PortOwner> others,
        int hostPort,
        IReadOnlySet<int>? activeUdpPorts = null,
        IReadOnlySet<int>? activeTcpPorts = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(others);

        var conflicts = new List<PortConflict>();
        var gamePort = candidate.GamePort;
        var rconPort = candidate.RconPort;

        if (gamePort < 1 || gamePort > MaxGamePort)
        {
            conflicts.Add(new PortConflict(
                gamePort,
                $"Game port {gamePort} must be between 1 and {MaxGamePort} (the game also uses port + 1)."));
        }

        if (rconPort < 1 || rconPort > MaxRconPort)
        {
            conflicts.Add(new PortConflict(rconPort, $"RCON port {rconPort} must be between 1 and {MaxRconPort}."));
        }

        if (rconPort == gamePort || rconPort == gamePort + 1)
        {
            conflicts.Add(new PortConflict(
                rconPort,
                $"RCON port {rconPort} overlaps this instance's own game port range {gamePort}-{gamePort + 1}."));
        }

        foreach (var other in others)
        {
            if (string.Equals(other.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var port in CandidatePorts(candidate))
            {
                if (port == other.GamePort || port == other.GamePort + 1)
                {
                    conflicts.Add(new PortConflict(
                        port,
                        $"Port {port} is used by '{other.Name}' (game port range {other.GamePort}-{other.GamePort + 1})."));
                }
                else if (port == other.RconPort)
                {
                    conflicts.Add(new PortConflict(port, $"Port {port} is used by '{other.Name}' (RCON port)."));
                }
            }
        }

        foreach (var port in CandidatePorts(candidate))
        {
            if (port == hostPort)
            {
                conflicts.Add(new PortConflict(port, $"Port {port} is used by the web UI."));
            }
        }

        if (activeUdpPorts is not null)
        {
            foreach (var port in new[] { gamePort, gamePort + 1 })
            {
                if (activeUdpPorts.Contains(port))
                {
                    conflicts.Add(new PortConflict(port, $"Port {port} is in use by an OS listener (UDP)."));
                }
            }
        }

        if (activeTcpPorts is not null && activeTcpPorts.Contains(rconPort))
        {
            conflicts.Add(new PortConflict(rconPort, $"Port {rconPort} is in use by an OS listener (TCP)."));
        }

        return conflicts;
    }

    /// <summary>The owner's ports in reporting order: game port, game port + 1, RCON port.</summary>
    private static int[] CandidatePorts(PortOwner owner) => [owner.GamePort, owner.GamePort + 1, owner.RconPort];

    private static HashSet<int> OccupiedPorts(IEnumerable<PortOwner> instances, int hostPort)
    {
        var occupied = new HashSet<int> { hostPort };
        foreach (var instance in instances)
        {
            occupied.UnionWith(CandidatePorts(instance));
        }

        return occupied;
    }

    private static int RequireStep(int step, string name) =>
        step >= 1 ? step : throw new InvalidOperationException($"{name} must be at least 1 (was {step}).");
}
