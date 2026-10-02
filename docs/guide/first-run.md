# First run

The installer has finished, the service is running, and you have a URL. What stands between that and
the first player joining is a long download, a handful of settings, and one server. Installing the app
itself is in the [README](../../README.md#install-from-a-release); the wizard that creates an instance
is covered step by step in [instance-creation.md](../instance-creation.md).

## The URL, and the wait on `/setup`

Open the URL the installer printed. With `LanHttps` that is `https://<box-ip>:5001/` behind a
self-signed certificate; with `Loopback` it is whatever your reverse proxy serves
([hosting.md](../hosting.md)). Every page redirects to `/setup` until the service is Ready. Nothing
useful can be done before the game install exists, and holding the whole UI is more honest than
letting you build an instance that cannot start.

On its first start the service builds the `DataRoot` tree, creates the database, downloads SteamCMD,
installs the dedicated server, and verifies the install before it opens the pages.

The Setup page shows the phase indicator and label (`Initializing`, `Recovering`,
`Installing the game`), the current step (`Preparing the data directory`,
`Migrating and seeding the database`, `Reconciling instance processes`,
`Downloading SteamCMD and installing the server`), and the **SteamCMD** console. The first line of the
console is
`Downloading SteamCMD from https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip ...`, then
`SteamCMD extracted to <DataRoot>\SteamCMD.`, then
`Attempt 1: steamcmd.exe +force_install_dir <DataRoot>\Server +login anonymous +app_update 2430930 +quit`.
SteamCMD's own output follows, with a progress bar and `downloading · 3.2 GB of 12.1 GB` under it.
`SteamCMD updated itself (exit code 7); running again.` on the first run is normal.

The download is about 12 GB. When the console prints `SteamCMD finished and the install is verified.`
the phase becomes **Ready** and the browser lands on the Instances page. Roughly 20 minutes on a fast
line.

## Signing in

The Sign in page asks for the one password you gave the installer. The hint under the field says it:
`The one in appsettings.json on the server box. Five wrong tries lock this address out for five
minutes.` The session is a 12-hour sliding cookie, written as `ArkAscendedServerAdmin.Auth`, and every
command you run afterwards re-checks that cookie server-side before doing anything. **Sign out** at the
bottom of the sidebar ends it.

## Two settings worth filling in first

Open **Settings** in the sidebar.

- **CurseForge API key** under *Game install*. Without it the Mods page adds mods by numeric id
  only (name = id, no thumbnail); with it you get search and metadata. The hint is exact:
  `Stored in plain text in the database. Enables mod search and metadata; without it mods are added
  by id.` The key is yours and subject to CurseForge's API terms. See [mods.md](mods.md).
- **Admin whitelist** at the bottom. This is the manager-wide list: every id here is written into
  every instance's `AllowedCheaterAccountIDs.txt` at start, ahead of the cluster and instance lists.
  Put your own EOS id here once and you are an admin on every server you ever create. ASA's admin
  whitelist wants EOS ids, and the manager learns them from the game log as players join, so until you
  have joined a server the editor cannot know your name. Paste the id (32 hex characters); it shows as
  `TBD` until you join. See [players-and-whitelists.md](players-and-whitelists.md).

Click **Save settings**. Everything else on the page has a working default; the values are explained
in [settings-and-export.md](settings-and-export.md).

## A cluster, if you want transfers

A cluster is only needed when players should travel between maps. If your first server stands alone,
skip this. Otherwise open **Clusters**, click **New cluster**, give it a **Cluster name**, choose where
its **INI files start from** (`Game defaults` is right for a first cluster), and click **Create
cluster**. The cluster page opens; set `ServerAdminPassword` under **Config** before creating members,
because members cannot start without it. See [clusters.md](clusters.md).

## The first instance

![The last step of the New instance wizard: a summary of the choices and the "Start the server right away" box](../images/wizard-8-summary.png)

On **Instances** (the sidebar's first entry, or the home page) click **New instance** or
**Create the first instance**. The wizard has eight steps: **Name**, **Cluster**, **Map**, **Config
source**, **Mods**, **Launch options**, **Ports**, **Summary**. For a first server: type a name and a
session name, pick standalone or the cluster, keep **The Island**, keep `Game defaults` and type a
**Server admin password** (standalone only; a cluster member gets it from the cluster), leave mods
and launch options alone, accept the suggested ports, tick **Start the server right away**, and click
**Create instance**. Each step is explained in [instance-creation.md](../instance-creation.md).

Creating and starting it writes `Instances\<slug>\` with its junctions, the source INI files
(standalone), the generated INI files, the whitelist file, two inbound UDP firewall rules named
`ArkAscendedServerAdmin-<instance id>` for the game port and game port + 1, and the process. The full
sequence is in [instance-creation.md](../instance-creation.md).

## Watching it come up

You land on the instance page with the **Console** tab open. The first manager line is
`Launched pid <n>: <exe> <arguments>`; then `ShooterGame.log` appears within a second of the game
writing it. The state under the title moves from **Starting** (`Loading the world.`) through
`World loaded, waiting to advertise.` and `Advertising, waiting for RCON.` to **Running** once the
first RCON probe answers, a minute or two on a warm box. Rows on the Instances page show the same
state.

## Joining

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

## What the install leaves on disk

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
anonymously, so no Steam account is involved and no Steam credential is stored. SteamCMD gets five
attempts, waiting 30, 60, 120, and 240 seconds between them, because anonymous downloads are the ones
Steam throttles. Success is not SteamCMD's exit code alone: the app manifest must read `StateFlags 4`
afterwards, since an interrupted download leaves a folder behind and nothing else.

## When the first run goes wrong

| Where | Message | Meaning |
|---|---|---|
| Browser | `403` on a plain `http://` request | The app refuses anything that is not HTTPS after forwarded-header processing. Use the proxy URL or `LanHttps`; [hosting.md](../hosting.md). |
| Sign in | `Incorrect password.` | Wrong password; each failure costs a one-second delay. |
| Sign in | `Too many failed attempts. Try again in a few minutes.` | Five failures from one address lock it out for five minutes. |
| Sign in | `No usable login password is configured. Set ArkAdmin:PasswordHash (or ArkAdmin:Password) in appsettings.json and restart the service.` | Neither `PasswordHash` nor `Password` is set, or the hash is malformed; every login is refused. [configuration.md](../configuration.md). |
| Setup | **Install failed** with `SteamCMD failed after 5 attempt(s): ...` | SteamCMD gave up. Click **Retry install**; the hint says why it usually works later: `Anonymous Steam downloads are sometimes throttled; trying again later usually works.` |
| Setup | `Install finished but could not be verified` | SteamCMD exited 0 but the manifest is not `StateFlags 4`. Retry, with *SteamCMD validate* on if it repeats. |
| Setup | **Startup failed** | A startup step threw. `Only a service restart clears a failed pipeline step. The service log has the stack trace.` |
| Wizard summary | `The start will be refused: no admin password is set. Go back to Config source and enter one, or untick this.` | Standalone with `Game defaults` or `Blank` and an empty password. |
| Instance page | `Could not start <name>` with `ServerAdminPassword under [ServerSettings] is missing or empty; RCON is the only stop path, so it is required.` | Set the password in the INI editor (**Config** tab) or the cluster's, then **Start**. |
| Instance page | Notice **Start would be refused.** | The launch preview found a problem; the text names it. |

Every other start refusal is listed in [instances.md](instances.md#refusals-and-failures).
