# Exposing servers to players

The app manages the servers on the box; getting players to them is network work outside the app.
None of it is automated in 1.0.0: the app writes the Windows firewall rule and you do the rest.

## What must be reachable

| Traffic | Direction | Port | Needed |
|---|---|---|---|
| Game | Inbound UDP | The instance's game port (`-port=`, 7777 by default for the first instance) | Yes. This is the one port a player connects to. |
| Game, second socket | Inbound UDP | Game port + 1 | The game binds it; the app's firewall rule opens it. Forward it with the game port when your router lets you enter a range; it is not known to be required for joins. |
| Server list | Outbound HTTPS | 443 to Epic Online Services | Yes, and it is outbound, so nothing to open. The server registers itself and Epic fills in the public address it saw. |
| Steam query | Inbound UDP | 27015 | No. ARK: Survival Ascended does not use it. Every server still opens UDP 27015 as a vestigial Steam socket and several instances can share it. Guides that tell you to forward 27015 or 7778 are Survival Evolved leftovers. |
| RCON | TCP | The instance's RCON port (27020 by default for the first instance) | **Never from outside.** The app talks to RCON on loopback only. The admin password crosses that connection in plain text, and RCON gives full control of the server. Do not forward it, do not open it in the firewall. |
| The web UI | TCP | 5000 or 5001 | Not for players. See [hosting.md](hosting.md). |

The app creates one inbound UDP firewall rule per instance (game port and game port + 1) when the
server starts and removes it when the instance is deleted. That covers the Windows firewall on the
box; it does not cover a router, a cloud security group, or a firewall appliance in front of the
box.

The game port is chosen on the Ports step of the New instance wizard and shown on the instance's
Settings tab.

## The three shapes that work

### A home box behind a router

Forward the game port (UDP) on the router to the box's LAN address. Give the box a fixed LAN
address (a DHCP reservation) so the forward does not point at the wrong machine after a reboot.
Then start the server; it appears in the in-game list under the session name within a few minutes,
and players who cannot find it join with `open <your-public-ip>:<game-port>`.

This needs a real public IPv4 address on the router's WAN side. Check the router's status page
against what a "what is my IP" site shows: if they differ, and the WAN address starts with `100.64`
to `100.127`, `10.`, or `192.168`, you are behind carrier-grade NAT (CGNAT), and port forwarding
cannot work. Nothing on the box changes that; the options are asking the ISP for a public address,
a cloud VM, or the private shape below.

If the router offers UPnP, you can map the port by hand from its interface. The app does not do it
for you, because leases expire, vanish on a router reboot, and fail silently.

Two things to know before choosing this shape: every player who joins learns your home IP address
(it is the address in the server list), and a UDP game port is a flood target for anyone who wants
to make your evening unpleasant. Both are true of every home-hosted game server; they are not
specific to ARK or to this app.

### A cloud Windows VM

Install the app on a Windows VM at any provider, allow the UDP game port inbound in the provider's
security group or firewall, and start the server. It lists with the VM's public address and your
home network is never involved. The tradeoffs are money and disk: the game install is 12 GB before
the first world, a populated map wants 16 GB of RAM or more, and the provider bills for all of it
around the clock. The web UI on the VM should then sit behind a reverse proxy with a real
certificate (`Loopback` or `Proxy` bind mode); do not put `LanHttps` on a public address.

### Private: Tailscale, ZeroTier, or a LAN

Install Tailscale or ZeroTier on the box and on each player's machine, or play on one LAN. Players
join from the main-menu console with

```
open <tailscale-or-lan-ip>:<game-port>
```

The server never appears in the public list (Epic sees the box's real public address, which is not
reachable, and the list entry is useless), nothing is exposed to the internet, and nobody learns
your home address. The Windows firewall rule the app writes applies to the virtual adapter as well,
so no extra rule is needed. This is the shape for a group of friends; it does not scale to strangers
because each of them needs to be in your network.

A cluster works in every shape: transfers between servers on the same box go through the cluster
directory on disk, not over the network.

## Shapes that do not work

- Tunnels and relays: playit.gg, a VPS forwarding UDP to your home, and similar. The server
  registers with Epic from the box, so the list shows the home WAN address, not the relay's. The
  server appears in the list and every join through the list fails; joins with `open
  <relay-ip>:<port>` may work, which is the private shape with more moving parts. The launch flag
  `-PublicIPForEpic=<ip>` is documented for Survival Evolved and is untested on Survival Ascended;
  if you try it, pass it through the instance's extra launch arguments and report what you find.
- ngrok carries TCP and HTTP, not UDP. Cloudflare Tunnel carries no public UDP either. Neither can
  front a game port.
- An Epic relay. There is none for dedicated servers. Console and crossplay listings still need the
  UDP port reachable.
- Forwarding RCON so a remote tool can reach it. See the table. Use the web UI over HTTPS instead;
  it has the RCON console.

## Checking

From another network (a phone off Wi-Fi is enough), in the game's main-menu console:

```
open <public-ip>:<game-port>
```

If that joins, the port is open and the server is up; if the server is also missing from the list,
wait a few minutes after start and check the session name filter. If `open` times out, work from
the box outward: the instance is Running on the Instances page, the Windows firewall rule exists
(`Get-NetFirewallRule -DisplayName 'ArkAscendedServerAdmin*'`), the router forward points at the
right LAN address, and the WAN address is not CGNAT.
