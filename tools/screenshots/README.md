# Screenshot capture

This folder holds the script that produces the pictures in `docs/images/`. It logs in to a running copy of the app and photographs a fixed list of pages, so the set can be regenerated after the interface changes.

The script only looks. It never starts or stops a server and never saves anything. The one thing it types is a search term in the mod search box, and it opens the RCON command list without sending a command.

## What you need

- Node.js 20 or later and Microsoft Edge (the script launches it headless).
- A running copy of the app with a cluster that has two instances running, ideally a cluster with a couple of mods and a CurseForge API key so mod search works. Use a throwaway cluster: the pictures show whatever is on the pages.

## Run it

```
cd tools/screenshots
npm install
BASE=https://your-admin-host ARKADMIN_PASSWORD=... node capture.mjs
```

On Windows PowerShell, set the variables first:

```
$env:BASE = "https://your-admin-host"
$env:ARKADMIN_PASSWORD = "..."
node capture.mjs
```

## Settings

All settings are environment variables.

| Variable | Meaning | Default |
| --- | --- | --- |
| `BASE` | Address of the app, without a trailing slash. Required. | none |
| `ARKADMIN_PASSWORD` | The login password. Required. | none |
| `OUT` | Folder for the PNG files. | `docs/images` in this repository |
| `CLUSTER_ID` | Id of the cluster to photograph (the number in `/clusters/2`). | `2` |
| `ISLAND_ID` | Id of the first instance, used for the console, players, and backups pictures. | `15` |
| `SCORCHED_ID` | Id of the second instance, used for the mod list picture. It should be a clustered instance with its own map mod. | `16` |
| `MOD_SEARCH` | Term typed into the mod search. | `spyglass` |
| `ONLY` | Comma-separated picture names to retake, for example `console,players`. | all |

## Pictures

Each picture is 1440 by 900 pixels at twice the pixel density, so the PNG is 2880 by 1800. Every one includes the page's own dark background, edge to edge.

| File | Page |
| --- | --- |
| `instances.png` | Instances list |
| `console.png` | Instance console with the RCON command list open |
| `mods-library.png` | Mod library |
| `mods-search.png` | Mod library after a CurseForge search |
| `cluster.png` | Cluster page, members tab (cropped to the content) |
| `cluster-mods.png` | Cluster page, mods tab |
| `ini-editor.png` | Cluster configuration editor |
| `instance-mods.png` | Mods tab of a clustered instance |
| `players.png` | Players tab of an instance (cropped) |
| `backups.png` | Backups tab of an instance (cropped) |
| `launch.png` | Cluster Launch tab: the base launch options |
| `cluster-settings.png` | Cluster Settings tab (cropped) |
| `schedule.png` | Cluster Schedule tab (cropped) |
| `instance-launch.png` | Instance Launch tab, top of the page |
| `instance-launch-preview.png` | Instance Launch tab scrolled down to the command line a start would run |
| `instance-settings.png` | Instance Settings tab, including the Connection card |
| `instance-schedule.png` | Instance Schedule tab (cropped) |
| `maps.png` | Maps list |
| `settings.png` | Global Settings page, top (see below) |
| `settings-export.png` | Global Settings page scrolled to the Host and Configuration data sections (see below) |
| `update.png` | Update page as it is, never starting a run (see below) |

## The Settings and Update pictures

The Settings page has a field for the CurseForge API key, so `settings` and `settings-export` are left out of a normal run. They are taken only when you name them, and only when `BASE` points at localhost:

```
BASE=http://127.0.0.1:5080 ARKADMIN_PASSWORD=... ONLY=settings,settings-export node capture.mjs
```

Run it against a throwaway copy of the app with an empty data folder and no API key, never against a real installation. If `BASE` is anything other than localhost, the script stops with an error instead of taking the picture.

`update` photographs the Update page as it is and never starts a run. A picture from the middle of a run (the SteamCMD output and the progress bar) has to be taken by starting the update by hand and then running `ONLY=update` while it goes.

## Keeping private data out

Just before each shot the script rewrites what is on the page:

- The host name of `BASE` becomes `example.com`.
- Any IPv4 address in text becomes `192.0.2.10`, an address reserved for documentation.
- A `BASE` host of `localhost` or a bare IP address is left alone.
- Values such as `ServerPassword=...` in plain text, like the launch command line, become `********`.
- In text boxes and editors, the value on any line whose key contains `password`, `secret`, `apikey`, or `token` becomes `********`.

This changes only the page in the browser. Nothing is saved, and the app is left as it was. A default run never opens the global Settings page. The instance Connection card is photographed: a public address is rewritten like any other host name, and addresses on the local network become `192.0.2.10`.

Still look at every picture before committing it. Player names and ids from a real server, and file paths on the host, are not scrubbed. Use a cluster where nobody has played.
