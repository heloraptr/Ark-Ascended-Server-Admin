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

## Keeping private data out

Just before each shot the script rewrites what is on the page:

- The host name of `BASE` becomes `example.com`.
- Any IPv4 address in text becomes `192.0.2.10`, an address reserved for documentation.
- In text boxes and editors, the value on any line whose key contains `password`, `secret`, `apikey`, or `token` becomes `********`.

This changes only the page in the browser. Nothing is saved, and the app is left as it was. The script never opens the Settings page or the instance Connection card, which show the public address and API key.

Still look at every picture before committing it. Player names and ids from a real server, and file paths on the host, are not scrubbed. Use a cluster where nobody has played.
