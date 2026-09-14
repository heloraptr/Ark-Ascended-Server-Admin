# User guide

Ark Ascended Server Admin runs as a Windows service on the box that hosts your ARK: Survival
Ascended servers and is operated from a browser. This guide is the page-by-page reference: what each
screen does, how to use it, what it writes to disk and to the database, why it works the way it does,
and what it says when it refuses. Start with the mental model below, then [first-run.md](first-run.md).

Every claim in these pages is checked against the code of the release they ship with. Labels, buttons,
and messages are quoted as the UI shows them. If a page and the app disagree, the app is right and the
page has a bug; open an issue.

## The mental model

**One game install, many servers.** SteamCMD installs the dedicated server once, under
`DataRoot\Server`. Every server you define (an *instance*) gets its own folder under
`DataRoot\Instances\<slug>` that contains NTFS junctions into `Server` for `Engine`,
`ShooterGame\Binaries`, `ShooterGame\Content`, and `ShooterGame\Plugins`, plus one real directory,
`ShooterGame\Saved`. The game is launched through the junction path, so Unreal sees a private `Saved`
(world, logs, generated config) while the binaries and content are shared. Updating the game means
updating `Server` once.

**Instances and clusters.** An instance is one server process: a name, a map, a session name, a game
port, an RCON port, a player cap, mods, launch options, and either its own INI files or a cluster's. A
*cluster* is a group of instances that share INI source files, mandatory mods, base launch options, an
admin whitelist, a cluster id, and a transfer directory under `DataRoot\Clusters\<slug>`. An instance
that belongs to no cluster is *standalone* and owns all of that itself.

**Source files and generated files.** `Game.ini` and `GameUserSettings.ini` are edited as plain text
under `Clusters\<slug>\Config` or `Instances\<slug>\Config`. Those are the *source* files. At every
start the manager reads the source, applies the instance's overrides, writes in the handful of keys it
owns (session name, ports, RCON, player cap), and writes the *generated* files under
`ShooterGame\Saved\Config\WindowsServer`, which is what the game reads. Generated files are never read
back as source, because the game rewrites them.

**The database is a mirror, not the master.** `DataRoot\Data\ArkAscendedServerAdmin.db` (SQLite) holds
instances, clusters, maps, the mod library, App Settings, players, backup records, and a copy of every
INI source file with its SHA-256. The file on disk wins; the mirror exists so that a copy of the
database is a complete configuration backup.

**RCON is how the manager talks to a running server.** Saving, stopping, the pre-stop broadcast, the
15-second liveness probe, `ListPlayers`, and the console input all go over RCON on `127.0.0.1`. That is
why an instance cannot start without a `ServerAdminPassword` in its `GameUserSettings.ini`.

**Servers outlive the service.** Game processes are detached children. Restarting the service (or the
box's session) does not stop them; on startup the service finds them again by their command line and
re-attaches.

## DataRoot layout

`DataRoot` is set by the installer (`C:\ArkAscendedServerAdmin` by default) and shown read-only on the
Settings page under **Host**.

| Path | What lives there |
|---|---|
| `Server\` | The game install, SteamCMD's target. `Server\steamapps\appmanifest_2430930.acf` with `StateFlags 4` is what "installed" means. |
| `SteamCMD\` | `steamcmd.exe`, downloaded and extracted on first run. |
| `Instances\<slug>\` | One folder per instance: junctions into `Server`, the real `ShooterGame\Saved`, and (standalone only) `Config\` with the INI source files. |
| `Instances\<slug>\ShooterGame\Saved\` | `Config\WindowsServer\*.ini` (generated at every start, plus `.bak`), `AllowedCheaterAccountIDs.txt`, `Logs\ShooterGame.log`, and the world under `<slug>\<MapKey>\`. |
| `Clusters\<slug>\` | The cluster's transfer directory (`-ClusterDirOverride`) and `Config\` with its INI source files. |
| `Backups\<slug>\` | Verified world backups, one zip per run. |
| `Backups\_app\` | Database copies the installer takes before an upgrade. |
| `Archive\<slug>-<timestamp>\` | `ShooterGame\Saved` of a deleted instance when you chose to keep the world. The slug stays reserved while the folder exists. |
| `Exports\` | Copies of the database written by **Export config backup** on the Settings page. |
| `Data\` | `ArkAscendedServerAdmin.db` and its WAL side files. |
| `keys\` | The Data Protection key ring for the login cookie, the `LanHttps` certificate, the installer's journal. |
| `App\` | The installed app, when `InstallDir` was left at its default under `DataRoot`. |

## The rail

The left rail is on every page. At the top, the navigation:

| Entry | Page |
|---|---|
| **Instances** | The dashboard at `/`: every instance grouped by cluster, standalone last. |
| **Clusters** | The cluster list and each cluster's page. |
| **Mods** | The CurseForge mod library and where each mod is used. |
| **Players** | Everyone the servers have seen, recorded from the game log. |
| **Maps** | Official and custom maps. |
| **Settings** | App Settings, the read-only host values, config export. |

At the bottom, the system panel: a lamp and label for the readiness phase (**Ready** when the game
install is verified), the number of instances up (`2 instances up`), the outcome of the last game update
run in this service session (`Up to date, build 12345678 · 3 h ago`), and the **Update game** button,
which is disabled until the service is Ready and leads to the Update page. While an update or install
runs, the panel shows its phase instead (`Stopping instances for update`, `Updating the game`,
`Restarting instances`). **Sign out** ends the 12-hour cookie session.

The thin horizon line at the top of the content area changes tone when the service is not ready or a
maintenance run is in progress, and again when startup failed.

## The state lamp and instance states

Every instance carries a lamp (the small triangle) and a label. One color per meaning: steady for
running, pulsing for a transition, hollow for stopped, red for anything that needs you.

| Label | Meaning | What the manager does |
|---|---|---|
| **Stopped** | No process. | Nothing. **Start** is available. |
| **Starting** | The process is up; no successful RCON probe yet. The hint under it follows the log: `Loading the world.`, `World loaded, waiting to advertise.`, `Advertising, waiting for RCON.` | Tails the log, probes RCON every 15 s. |
| **Running** | An RCON probe succeeded. | Keeps probing; the backup scheduler includes the instance. |
| **Starting, unconfirmed** | `Alive for over 10 minutes without answering RCON.` | Keeps probing. Check `ServerAdminPassword` and `RCONPort`. |
| **Unreachable** | A re-attached process whose RCON has not answered in 10 minutes, or one whose generated `GameUserSettings.ini` is missing. | Keeps watching for exit. Stop will skip `doexit` and wait for the graceful timeout before killing. |
| **Stopping** | The stop job is running: countdown, `doexit`, wait, kill if needed. | **Stop now** skips the countdown. |
| **Identity not saved** | `The process runs but its PID could not be saved.` | The server is fine; click **Retry persist** on the instance page. |
| **Unknown** | `More than one process matched; nothing is done automatically.` Two or more `ArkAscendedServer.exe` under `Instances\` carry this instance's slug. | Nothing. **Start** is disabled. Stop the extra processes by hand and restart the service. |

The same lamp shape appears in the rail for the service itself (readiness) and on the Setup page.

## The readiness pipeline and `/setup`

The service does not answer requests for pages until it has proven that the game install is usable.
Every request to any page is redirected to `/setup` until then (the login page, the Blazor hub, and
static files are exempt), and the Setup page redirects to `/` the moment the service is Ready. The rail
shows the same phase, so you always know where the service is.

| Phase (rail label) | Message on the Setup page | What is happening |
|---|---|---|
| **Initializing** | `Preparing the data directory`, then `Migrating and seeding the database` | Creates the `DataRoot` tree; applies database migrations; seeds the official maps and default settings. |
| **Recovering** | `Reconciling instance processes`, `Reconciling SteamCMD`, `Resuming interrupted maintenance` | Finds servers that survived a service restart and re-attaches; waits for or kills a leftover `steamcmd.exe`; resumes an interrupted game update. |
| **Installing the game** | `Downloading SteamCMD and installing the server` or `Installing the server` | `steamcmd.exe` is missing, or the app manifest under `Server\steamapps` does not say fully installed. The SteamCMD console on the Setup page shows every line and a progress bar. About 12 GB. |
| **Install failed** | `Install failed` with the error, or `Install finished but could not be verified` | SteamCMD gave up after its retries. The **Retry install** button re-runs it. |
| **Startup failed** | `Startup failed; check the log and restart the service` | A pipeline step threw. Only a service restart clears this; the Windows event log has the stack trace. |
| **Ready** | `Ready` | Everything above passed. Pages open, **Start** works. |

The check for "installed" is the app manifest, never a directory-exists test; an aborted download leaves
a folder but not a manifest with `StateFlags 4`. Until Ready, **Start** is refused with
`The service is not ready yet (<phase>: <message>).` even if you reach a page by other means.

## Pages

| Page | Covers |
|---|---|
| [first-run.md](first-run.md) | After install: `/setup`, the game install, the first Settings, the first cluster and instance, joining. |
| [instances.md](instances.md) | The Instances page, start, stop, restart, re-attach, the instance page and its tabs, deleting. |
| [clusters.md](clusters.md) | What a cluster shares, creating and editing one, transfers. |
| [configuration-files.md](configuration-files.md) | `Game.ini` and `GameUserSettings.ini`: source and generated, overrides, the INI editor, reserved keys. |
| [launch-options.md](launch-options.md) | The flags editor, cluster base plus instance additions, the command-line preview. |
| [console-and-rcon.md](console-and-rcon.md) | The `ShooterGame.log` tail, startup markers, the RCON input. |
| [backups.md](backups.md) | How a backup runs, manual and scheduled, the Backups tab, restoring by hand. |
| [game-updates.md](game-updates.md) | The Update page, verified stops, SteamCMD, resume after a service restart, the recovery banner. |
| [mods.md](mods.md) | The CurseForge key, search versus manual ids, cluster and instance mods, map mods. |
| [maps.md](maps.md) | Official and custom maps, the map's mod, choosing a map. |
| [players-and-whitelists.md](players-and-whitelists.md) | Players recorded from the log, `ListPlayers`, the three admin whitelists. |
| [settings-and-export.md](settings-and-export.md) | App Settings, host values, the version, config export. |
| [troubleshooting.md](troubleshooting.md) | The symptoms you meet first and what to do. |
| [../hosting.md](../hosting.md) | Bind modes, reverse proxies, the certificate, the event log, upgrading, uninstalling. |
| [../configuration.md](../configuration.md) | Every host setting, what the installer writes, hashing the password, file permissions. |
| [../exposing-servers.md](../exposing-servers.md) | Which port must be reachable and the network shapes that work. |
| [../instance-creation.md](../instance-creation.md) | Every step behind the wizard's **Create instance** button and **Start the server right away**. |
