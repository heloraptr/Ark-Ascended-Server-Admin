# User guide

Ark Ascended Server Admin runs as a Windows service on the machine that hosts your ARK: Survival
Ascended servers. You work with it from a browser.

This guide covers every page in the app. If you have just installed it, read
[first-run.md](first-run.md) next and come back to the rest when you need it.

Buttons, labels, and messages are quoted the way the app shows them. Where these pages and the app
disagree, the app is right and the page has a bug, so please open an issue.

## How it works

### One game install, many servers

SteamCMD installs the dedicated server once, under `DataRoot\Server`. Every server you define (an
instance) gets a folder under `DataRoot\Instances\<slug>` holding NTFS junctions back into `Server`
for `Engine`, `ShooterGame\Binaries`, `ShooterGame\Content`, and `ShooterGame\Plugins`, plus one real
directory, `ShooterGame\Saved`.

The game is launched through the junction path, so Unreal sees a private `Saved` folder with its own
world, logs, and generated config, while the binaries and content are shared. Updating the game means
updating `Server` once.

### Instances and clusters

An instance is one server process: a name, a map, a session name, a game port, an RCON port, a player
cap, mods, launch options, and either its own INI files or a cluster's.

A cluster is a group of instances that share INI source files, mandatory mods, base launch options, an
admin whitelist, a cluster id, and a transfer directory under `DataRoot\Clusters\<slug>`. An instance
in no cluster is standalone and owns all of that itself.

### Source files and generated files

You edit `Game.ini` and `GameUserSettings.ini` as plain text under `Clusters\<slug>\Config` or
`Instances\<slug>\Config`. Those are the source files.

At every start the manager reads the source, applies the instance's overrides, fills in the handful of
keys it owns (session name, ports, RCON, player cap), and writes the generated files under
`ShooterGame\Saved\Config\WindowsServer`. Those are what the game reads. Generated files are never read
back as source, because the game rewrites them as it pleases.

### The database

`DataRoot\Data\ArkAscendedServerAdmin.db` is SQLite. It holds instances, clusters, maps, the mod
library, App Settings, players, backup records, and a copy of every INI source file with its SHA-256.

The file on disk always wins. The database keeps a copy so that a copy of the database is a complete
backup of your configuration.

### RCON

The manager talks to a running server over RCON on `127.0.0.1`. Saving, stopping, the countdown
broadcast, the liveness check every 15 seconds, `ListPlayers`, and anything you type into the console
all go that way. That is why an instance will not start without a `ServerAdminPassword` in its
`GameUserSettings.ini`.

### Servers keep running when the service does not

Game processes are detached children. Restarting the service, or signing out of Windows, does not stop
them. When the service comes back it finds them again by their command line and picks up where it left
off.

## What lives under DataRoot

The installer sets `DataRoot`, `C:\ArkAscendedServerAdmin` unless you changed it. Settings shows it
read-only under **Host**.

| Path | What lives there |
|---|---|
| `Server\` | The game install, SteamCMD's target. `Server\steamapps\appmanifest_2430930.acf` with `StateFlags 4` is what "installed" means. |
| `SteamCMD\` | `steamcmd.exe`, downloaded and extracted on first run. |
| `Instances\<slug>\` | One folder per instance: junctions into `Server`, the real `ShooterGame\Saved`, and, for a standalone instance, `Config\` with its INI source files. |
| `Instances\<slug>\ShooterGame\Saved\` | `Config\WindowsServer\*.ini` (generated at every start, plus `.bak`), `AllowedCheaterAccountIDs.txt`, `Logs\ShooterGame.log`, and the world under `<slug>\<MapKey>\`. |
| `Clusters\<slug>\` | The cluster's transfer directory (`-ClusterDirOverride`) and `Config\` with its INI source files. |
| `Backups\<slug>\` | Verified world backups, one zip per run. |
| `Backups\_app\` | Database copies the installer takes before an upgrade. |
| `Archive\<slug>-<timestamp>\` | `ShooterGame\Saved` of a deleted instance, when you chose to keep the world. The slug stays reserved while the folder is there. |
| `Exports\` | Copies of the database written by **Export config backup** on the Settings page. |
| `Data\` | `ArkAscendedServerAdmin.db` and its WAL side files. |
| `keys\` | The Data Protection key ring for the login cookie, the `LanHttps` certificate, the installer's journal. |
| `App\` | The installed app, if you left `InstallDir` at its default under `DataRoot`. |

## The sidebar

The menu down the left side is on every page. At the top, the pages:

| Entry | Page |
|---|---|
| **Instances** | The dashboard at `/`: every instance grouped by cluster, standalone ones last. |
| **Clusters** | The cluster list and each cluster's page. |
| **Mods** | The CurseForge mod library and where each mod is used. |
| **Players** | Everyone the servers have seen, recorded from the game log. |
| **Maps** | Official and custom maps. |
| **Settings** | App Settings, the read-only host values, config export. |

At the bottom sits a panel for the service itself: an indicator and label for how far startup has got
(**Ready** once the game install is verified), how many instances are up (`2 instances up`), how the
last game update went in this service session (`Up to date, build 12345678 · 3 h ago`), and the
**Update game** button, which stays disabled until the service is Ready and takes you to the Update
page. During an update or install the panel shows the phase instead: `Stopping instances for update`,
`Updating the game`, `Restarting instances`. **Sign out** ends the 12-hour session.

The thin line across the top of the content area changes color when the service is not ready, when a
maintenance run is going, and again when startup has failed.

## Instance states

Every instance shows a small triangle and a label. Steady means running, pulsing means something is
changing, hollow means stopped, red means it wants you.

| Label | Meaning | What the manager does |
|---|---|---|
| **Stopped** | No process. | Nothing. **Start** is available. |
| **Starting** | The process is up but RCON has not answered yet. The hint underneath follows the log: `Loading the world.`, `World loaded, waiting to advertise.`, `Advertising, waiting for RCON.` | Tails the log, tries RCON every 15 s. |
| **Running** | RCON answered. | Keeps checking; the backup scheduler picks the instance up. |
| **Starting, unconfirmed** | `Alive for over 10 minutes without answering RCON.` | Keeps trying. Check `ServerAdminPassword` and `RCONPort`. |
| **Unreachable** | A re-attached process whose RCON has not answered in 10 minutes, or one whose generated `GameUserSettings.ini` has gone missing. | Watches for the process to exit. A stop still tries `doexit` when the credentials were readable and kills as soon as it goes unanswered; without them it skips the command and waits the graceful timeout out first. |
| **Stopping** | The stop is running: countdown, `doexit`, wait, kill if it has to. | **Stop now** skips the countdown; once the exit has been requested it reads **Closing…** and is disabled until the process is gone. |
| **Identity not saved** | `The process runs but its PID could not be saved.` | The server is fine. Click **Retry persist** on the instance page. |
| **Unknown** | `More than one process matched; nothing is done automatically.` Two or more `ArkAscendedServer.exe` under `Instances\` carry this instance's slug. | Nothing. **Start** is disabled. Stop the extra processes by hand and restart the service. |

The same indicator shows up in the sidebar for the service itself and on the setup page.

## Starting up, and `/setup`

The service will not serve pages until it has proved the game install is usable. Until then every
request lands on `/setup` (the login page, the Blazor hub, and static files are exempt), and the setup
page sends you to `/` the moment the service is ready. The sidebar shows the same phase, so you can see
where things stand from any page.

| Phase | Message on the setup page | What is happening |
|---|---|---|
| **Initializing** | `Preparing the data directory`, then `Migrating and seeding the database` | Creates the `DataRoot` tree, applies database migrations, seeds the official maps and default settings. |
| **Recovering** | `Reconciling instance processes`, `Reconciling SteamCMD`, `Resuming interrupted maintenance` | Finds servers that outlived a service restart and re-attaches to them, waits out or kills a leftover `steamcmd.exe`, resumes a game update that was cut off. |
| **Installing the game** | `Downloading SteamCMD and installing the server` or `Installing the server` | `steamcmd.exe` is missing, or the app manifest under `Server\steamapps` does not say fully installed. The console on the setup page shows every line and a progress bar. About 12 GB. |
| **Install failed** | `Install failed` with the error, or `Install finished but could not be verified` | SteamCMD gave up after its retries. **Retry install** runs it again. |
| **Startup failed** | `Startup failed; check the log and restart the service` | A startup step threw. Only a service restart clears this; the Windows event log has the stack trace. |
| **Ready** | `Ready` | Everything above passed. Pages open and **Start** works. |

"Installed" is decided by the app manifest, never by checking that a folder exists, because an aborted
download leaves a folder behind but not a manifest with `StateFlags 4`. Until the service is ready,
**Start** is refused with `The service is not ready yet (<phase>: <message>).` even if you find your
way to a page.

## Pages

| Page | Covers |
|---|---|
| [first-run.md](first-run.md) | After install: `/setup`, the game install, the first settings, the first cluster and instance, joining. |
| [instances.md](instances.md) | The Instances page, start, stop, restart, re-attach, the instance page and its tabs, scheduled actions, deleting. |
| [clusters.md](clusters.md) | What a cluster shares, creating and editing one, transfers. |
| [configuration-files.md](configuration-files.md) | `Game.ini` and `GameUserSettings.ini`: source and generated, overrides, the INI editor, reserved keys. |
| [launch-options.md](launch-options.md) | The flags editor, cluster base plus instance additions, the command-line preview. |
| [console-and-rcon.md](console-and-rcon.md) | The `ShooterGame.log` tail, startup markers, the RCON input. |
| [backups.md](backups.md) | How a backup runs, manual and scheduled, the Backups tab, restoring by hand. |
| [game-updates.md](game-updates.md) | The Update page, verified stops, SteamCMD, resuming after a service restart, the recovery banner. |
| [mods.md](mods.md) | The CurseForge key, search versus manual ids, cluster and instance mods, map mods. |
| [maps.md](maps.md) | Official and custom maps, the map's mod, choosing a map. |
| [players-and-whitelists.md](players-and-whitelists.md) | Players recorded from the log, `ListPlayers`, the three admin whitelists. |
| [settings-and-export.md](settings-and-export.md) | App Settings, host values, the version, config export. |
| [troubleshooting.md](troubleshooting.md) | What goes wrong first and what to do about it. |
| [../hosting.md](../hosting.md) | Bind modes, reverse proxies, the certificate, the event log, upgrading, uninstalling. |
| [../configuration.md](../configuration.md) | Every host setting, what the installer writes, hashing the password, file permissions. |
| [../exposing-servers.md](../exposing-servers.md) | Which port has to be reachable, and the network setups that work. |
| [../instance-creation.md](../instance-creation.md) | Every step behind the wizard's **Create instance** button and **Start the server right away**. |
