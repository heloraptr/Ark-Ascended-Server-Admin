# First run

The installer has finished, the service is running, and you have a URL. This page takes you from that
point to the first player joining: the game install on `/setup`, signing in, the two settings worth
filling in before anything else, the first cluster (optional) and instance, and the `open` command
players use. Installing the app itself is in the [README](../../README.md#install-from-a-release);
creating an instance in detail is in [instance-creation.md](../instance-creation.md).

## What it does

On its first start the service builds the `DataRoot` tree, creates the database, downloads SteamCMD,
installs the dedicated server (about 12 GB), verifies the install, and only then opens the pages. You
then set a CurseForge key (optional, for mod search) and your own EOS id (so you are an admin on every
server), create an instance, start it, and join.

## How to use it

### 1. Open the URL and wait on `/setup`

Open the URL the installer printed. With `LanHttps` that is `https://<box-ip>:5001/` behind a
self-signed certificate; with `Loopback` it is whatever your reverse proxy serves
([hosting.md](../hosting.md)). Every page redirects to `/setup` until the service is Ready.

The Setup page shows the phase lamp and label (`Initializing`, `Recovering`, `Installing the game`),
the current step (`Preparing the data directory`, `Migrating and seeding the database`,
`Reconciling instance processes`, `Downloading SteamCMD and installing the server`), and the
**SteamCMD** console. The first line of the console is
`Downloading SteamCMD from https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip ...`, then
`SteamCMD extracted to <DataRoot>\SteamCMD.`, then
`Attempt 1: steamcmd.exe +force_install_dir <DataRoot>\Server +login anonymous +app_update 2430930 +quit`.
SteamCMD's own output follows, with a progress bar and `downloading · 3.2 GB of 12.1 GB` under it.
`SteamCMD updated itself (exit code 7); running again.` on the first run is normal.

When the console prints `SteamCMD finished and the install is verified.` the phase becomes **Ready**
and the browser lands on the Instances page. Roughly 20 minutes on a fast line.

### 2. Sign in

The Sign in page asks for the one password you gave the installer. The hint under the field says it:
`The one in appsettings.json on the server box. Five wrong tries lock this address out for five
minutes.` The session is a 12-hour sliding cookie; **Sign out** in the rail ends it.

### 3. Settings worth filling in first

Open **Settings** in the rail.

- **CurseForge API key** under *Game install*. Without it the Mods page adds mods by numeric id
  only (name = id, no thumbnail); with it you get search and metadata. The hint is exact:
  `Stored in plain text in the database. Enables mod search and metadata; without it mods are added
  by id.` The key is yours and subject to CurseForge's API terms. See [mods.md](mods.md).
- **Admin whitelist** at the bottom. This is the manager-wide list: every id here is written into
  every instance's `AllowedCheaterAccountIDs.txt` at start, ahead of the cluster and instance lists.
  Put your own EOS id here once and you are an admin on every server you ever create. Until you have
  joined a server the editor cannot know your name, so paste the id (32 hex characters); it shows as
  `TBD` until you join. See [players-and-whitelists.md](players-and-whitelists.md).

Click **Save settings**. Everything else on the page has a working default; the values are explained
in [settings-and-export.md](settings-and-export.md).

### 4. A cluster, if you want transfers

A cluster is only needed when players should travel between maps. If your first server stands alone,
skip this. Otherwise open **Clusters**, click **New cluster**, give it a **Cluster name**, choose where
its **INI files start from** (`Game defaults` is right for a first cluster), and click **Create
cluster**. The cluster page opens; set `ServerAdminPassword` under **Config** before creating members,
because members cannot start without it. See [clusters.md](clusters.md).

### 5. The first instance

On **Instances** (the rail's first entry, or the home page) click **New instance** or
**Create the first instance**. The wizard has eight steps: **Name**, **Cluster**, **Map**, **Config
source**, **Mods**, **Launch options**, **Ports**, **Summary**. For a first server: type a name and a
session name, pick standalone or the cluster, keep **The Island**, keep `Game defaults` and type a
**Server admin password** (standalone only; a cluster member gets it from the cluster), leave mods
and launch options alone, accept the suggested ports, tick **Start the server right away**, and click
**Create instance**. Each step is explained in [instance-creation.md](../instance-creation.md).

### 6. Watch it come up

You land on the instance page with the **Console** tab open. The first manager line is
`Launched pid <n>: <exe> <arguments>`; then `ShooterGame.log` appears within a second of the game
writing it. The state under the title moves from **Starting** (`Loading the world.`) through
`World loaded, waiting to advertise.` and `Advertising, waiting for RCON.` to **Running** once the
first RCON probe answers, a minute or two on a warm box. Rows on the Instances page show the same lamp.

### 7. Join

From the game's main menu console:

```
open <ip>:<game-port>
```

`<game-port>` is the instance's game port, shown on its row and on its **Settings** tab. On a LAN
`<ip>` is the box's LAN address. From the internet it is the router's public address with the UDP game
port forwarded to the box; that, and the shapes that do and do not work, are in
[exposing-servers.md](../exposing-servers.md). The server also registers with Epic and appears in the
in-game list under its session name after a few minutes, when the port is reachable from outside.

Once you have joined, the **Players** page lists you with your EOS id, and every whitelist editor
offers your name.

## What happens underneath

After the install, before the first instance:

```
<DataRoot>\
  Server\                    the game install; Server\steamapps\appmanifest_2430930.acf says StateFlags 4
  SteamCMD\steamcmd.exe
  Data\ArkAscendedServerAdmin.db     schema migrated; official maps and default App Settings seeded
  keys\                      the cookie key ring
  Instances\  Clusters\  Backups\  Archive\  Exports\   empty
```

The install command is exactly `+force_install_dir <DataRoot>\Server +login anonymous +app_update
2430930 +quit`, with `validate` appended only when the App Setting *SteamCMD validate* is on. It runs
anonymously; no Steam account is involved. SteamCMD gets five attempts, waiting 30, 60, 120, and 240
seconds between them. Success is not SteamCMD's exit code alone: the app manifest must read
`StateFlags 4` afterwards.

Signing in writes the cookie `ArkAscendedServerAdmin.Auth`. Every command you run afterwards
re-checks that cookie server-side before doing anything.

Creating and starting the first instance writes `Instances\<slug>\` with its junctions, the source
INI files (standalone), the generated INI files, the whitelist file, two inbound UDP firewall rules
named `ArkAscendedServerAdmin-<instance id>` for the game port and game port + 1, and the process.
The full sequence is in [instance-creation.md](../instance-creation.md).

## Why it works this way

The install is verified by the manifest rather than by a folder existing because an interrupted
download leaves a folder. Anonymous SteamCMD is enough for the dedicated server and avoids storing
any Steam credential; the design note records that anonymous throttling was the real-world pain,
which is what the retry with backoff is for. Pages are held on `/setup` until Ready because nothing
useful can be done before the install exists, and holding the whole UI is simpler and more honest
than letting you build an instance that cannot start.

The manager-wide whitelist exists so that the owner enters one id once. ASA's admin whitelist wants
EOS ids, and the manager learns them from the game log as players join; before your first join it
cannot know yours, hence the paste-and-`TBD` path.

## When it refuses or fails

| Where | Message | Meaning |
|---|---|---|
| Browser | `403` on a plain `http://` request | The app refuses anything that is not HTTPS after forwarded-header processing. Use the proxy URL or `LanHttps`; [hosting.md](../hosting.md). |
| Sign in | `Incorrect password.` | Wrong password; each failure costs a one-second delay. |
| Sign in | `Too many failed attempts. Try again in a few minutes.` | Five failures from one address lock it out for five minutes. |
| Sign in | `No usable login password is configured. Set ArkAdmin:PasswordHash (or ArkAdmin:Password) in appsettings.json and restart the service.` | Neither `PasswordHash` nor `Password` is set, or the hash is malformed; every login is refused. [configuration.md](../configuration.md). |
| Setup | **Install failed** with `SteamCMD failed after 5 attempt(s): ...` | SteamCMD gave up. Click **Retry install**; the hint says why it usually works later: `Anonymous Steam downloads are sometimes throttled; trying again later usually works.` |
| Setup | `Install finished but could not be verified` | SteamCMD exited 0 but the manifest is not `StateFlags 4`. Retry, with *SteamCMD validate* on if it repeats. |
| Setup | **Startup failed** | A pipeline step threw. `Only a service restart clears a failed pipeline step. The service log has the stack trace.` |
| Wizard summary | `The start will be refused: no admin password is set. Go back to Config source and enter one, or untick this.` | Standalone with `Game defaults` or `Blank` and an empty password. |
| Instance page | `Could not started <name>` with `ServerAdminPassword under [ServerSettings] is missing or empty; RCON is the only stop path, so it is required.` | Set the password in the INI editor (**Config** tab) or the cluster's, then **Start**. |
| Instance page | Notice **Start would be refused.** | The launch preview found a problem; the text names it. |

Every other start refusal is listed in [instances.md](instances.md#when-it-refuses-or-fails).
