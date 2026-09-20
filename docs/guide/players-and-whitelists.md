# Players and whitelists

The manager keeps a table of every player its servers have seen, filled from the `joined this ARK!`
and `left this ARK!` lines of each instance's `ShooterGame.log` as they happen, and from the RCON
`ListPlayers` reply when you ask an instance who is on. The table exists for one purpose: ASA's
admin whitelist wants EOS ids, not names, and the **Players** page is where you find an id and put
it on a list. There are three lists, manager-wide, per cluster, and per instance, and their union
is written to the file the game reads at every start.

## The Players page

**Players** in the sidebar lists everyone known, by name, with the columns **Name**, **Status**
(**Online** on *instance*, or "Last seen *N* ago on *instance*"), **Platform**, **EOS id** (with a
copy button), **First seen**, **Last joined**, **Whitelist** (a **Choose a list** drop-down of every
cluster and instance, and **Add**), and a forget button. The summary line reads "*N* players known,
*M* online now." The page follows the log: a join appears without a reload. Before anyone has joined
it says "No players yet. A player appears here the moment they join a running server; nothing to
press."

**Add** puts the row's EOS id on the chosen cluster's or instance's whitelist (the toast says
"Added to *X*'s whitelist"). **Forget** deletes the row; "Forgetting a player only clears this row;
they come back on their next join."

## Who is on a server right now

The instance **Players** tab ("On the server now") asks the server with `ListPlayers` every time the
tab opens and on **List players**; both need the state **Running**. It shows **Name** and **EOS
id** with a copy button, "No players connected", or the RCON error. When the instance is not
running: "The instance is not running. Start it to see who is on it."

The command goes over RCON with the RCON command timeout from Settings, and the reply is parsed as
lines of the form `0. Name, <id>` (a `Name, <id>` line without the index is accepted too);
`No Players Connected` is an empty list. Everyone listed is upserted as online on that instance with
`LastSeenAt` = now; anyone the table thought was online there but who is not in the reply is marked
offline. A `ListPlayers` reply carries no platform, so that column stays as the last join line set
it.

`ListPlayers` runs only when you ask. It answers "who is on right now" and corrects the table at the
same time, without a timer hitting every server.

## Where the names and ids come from

Every instance console is a tail of
`Instances\<slug>\ShooterGame\Saved\Logs\ShooterGame.log`. The tracker watches each console for a
line of this shape (captured from a live server):

```
[2026.09.13-18.48.40:782][696]2026.09.13_18.48.40: PlayerName [UniqueNetId:0002f16bad3d4330b6097fcec38c5610 Platform:None] joined this ARK!
```

and the matching `left this ARK!`. The id is a 32-hex-digit EOS id or a 17-digit Steam id; the
`Platform:` token is stored as the platform (`None` for an EOS login). The bracketed stamp is UTC
and becomes the event time, so lines backfilled after a service restart record when things
happened, not when they were read; an event older than the row's latest evidence is ignored, which
makes a replay harmless. Events are applied one at a time in log order on a background queue, so
the console never waits on the database.

The log is the source rather than a poll over RCON because the game writes the join and leave lines
itself with a UTC stamp. That gives an exact history at no cost, and it survives service restarts
through the backfill, including servers the service re-attached to instead of starting.

A join sets `IsOnline`, `LastJoinedAt`, and `LastInstance`; a leave clears `IsOnline` and sets
`LastLeftAt`. When an instance's process is gone (a stop, a crash, or a re-attach that finds
nothing), every player last seen online on it is marked offline; when they left is unknown, so only
the flag changes.

The table is `KnownPlayers` (`Name`, `EosId`, `Platform`, `FirstSeenAt`, `LastSeenAt`,
`LastJoinedAt`, `LastLeftAt`, `IsOnline`, `LastInstanceId`). Deleting an instance clears
`LastInstanceId` and `IsOnline` on its players; the rows stay.

## The three admin whitelists

| List | Where | Applies to |
|---|---|---|
| Manager-wide | Settings, *Admin whitelist* | Every instance. "Put yourself here once; the other editors show these ids locked." |
| Cluster | Cluster page, **Admin whitelist** | Every member of the cluster. |
| Instance | Instance page, **Settings** tab, **Admin whitelist**; also the wizard's *Ports* step | That instance. |

All three use the same control: a "Player name or EOS id" box with suggestions drawn from the
Players table ("*Name* · *id*"), an **Add** button, and the list itself, locked rows first (the id,
"from Settings" or "from the cluster", and a lock icon), then the list's own rows with a remove
button. A row whose id has never joined shows **TBD** in place of the name ("Shown once they join a
server") and fills in when they do. Changes are saved with the page's **Save settings** button; the
instance page adds the reminder "The whole list is written to AllowedCheaterAccountIDs.txt at
start."

The manager-wide list is there so you enter your own id once instead of on every cluster and every
instance.

Each list is a one-id-per-line string: `AdminWhitelist` on the App Settings row, on the `Clusters`
row, and on the `Instances` row. Saving trims each line and drops blank ones.

## The file the game reads

At every start the config writer builds the union in this order: manager list, cluster list,
instance list; trimmed, blank and `;`/`#` comment lines skipped, duplicates dropped with the first
occurrence kept. It writes the result, one id per line with Windows line endings, to

```
Instances\<slug>\ShooterGame\Saved\AllowedCheaterAccountIDs.txt
```

atomically (temp file and rename). An empty union writes an empty file, so clearing every list
takes effect. The file is generated, never read back: editing it by hand lasts until the next
start. The **Launch** tab's preview is built from the same inputs.

`AllowedCheaterAccountIDs.txt` is ASA's admin whitelist: an account whose id is in it can use admin
commands on that server without entering the server admin password. The manager writes the file
directly under `ShooterGame\Saved`; the code carries a note that whether the game reads it from
exactly that location when launched with `AltSaveDirectoryName` was still to be confirmed on a live
server, so check on your own server once before relying on it.

## Bans apply to the whole box

The manager does not manage bans, and this is deliberate. On a live server the game keeps a single
`BanList.txt` in the shared install:

```
Server\ShooterGame\Binaries\Win64\BanList.txt
```

Every instance reaches that folder through its junction, so there is one list for all of them. The game
rewrites the file from its own memory on every `BanPlayer` and `UnbanPlayer` and never reads it back
while it runs: a line added by hand disappears at the next ban, and when two servers are up, whichever
bans last overwrites the other one's bans on disk. Two consequences follow. A `BanPlayer` sent from any
instance console bans that account on every server on the box, not just the one you typed it into.
And the file is only a record, not a control: to unban, send `UnbanPlayer` from a running server.

## When an id or a name is refused

| Where | Message | Meaning and what to do |
|---|---|---|
| Players tab, **List players** | Button disabled; "The instance is not running. Start it to see who is on it." | `ListPlayers` needs a `Running` instance. |
| Players tab | `The instance is not running, so there is no server to ask.` | The state changed between opening the tab and the call. |
| Players tab | `RCON credentials could not be read from the generated GameUserSettings.ini.` | The generated config is missing or has no `ServerAdminPassword`/`RCONPort`; restart the instance. |
| Players tab | `RCON timeout failure: ...` / `RCON connect failure: ...` / `RCON authentication failure: ...` | The server did not answer, refused the connection, or rejected the password. See [console-and-rcon.md](console-and-rcon.md). |
| Players tab | `The instance no longer exists.` | Deleted meanwhile. |
| Whitelist editor | `Not a known player. Enter an EOS id (32 hex characters) or pick a name from the suggestions.` | The text is neither a known name nor a 32-hex or 17-digit id. |
| Whitelist editor | `<name or id> is already on the list from Settings.` / `... from the cluster.` | Inherited; remove it there if you must. |
| Whitelist editor | `<name or id> is already on the list.` | Duplicate on this list. |
| Settings save | `AdminWhitelist must hold one id per line with no spaces.` | A line contains whitespace or a control character. |
| Copy button | "Copy failed: The browser refused clipboard access." | Clipboard access needs a secure context; select the id and copy by hand. |

## A player who never appears

Check the instance console for the join line. The tracker only reads lines the console received, so
a server whose log tail stopped (`Log tail stopped: ...` in the console) records nothing until the
instance is restarted.
