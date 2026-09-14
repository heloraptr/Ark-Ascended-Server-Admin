# Launch options

Launch options are the `-Flag` switches on the dedicated server's command line: the typed flags the
manager knows about, a free-text field for anything else, and the options the manager builds itself
from the instance (map, ports, mods, cluster, logging).

## Three kinds of command-line content

They are kept apart, because they fail in different ways:

1. **Manager-owned options**, built from the instance and never editable as text: the map string,
   `-port`, `-WinLiveMaxPlayers`, `-clusterid`, `-ClusterDirOverride`, `-mods`, `-log`,
   `-servergamelog`.
2. **Typed flags** with a three-way control each, plus two text fields (**Server platform**,
   **Active event**).
3. **Additional arguments**, free text appended verbatim, validated so it cannot contradict 1 or 2.

`?Key=Value` map parameters (the `[ServerSettings]` keys some guides put after the map name) are not
launch options here; they belong in the INI text ([configuration-files.md](configuration-files.md)),
and a `?` anywhere in additional arguments is refused. Those parameters are INI keys in disguise, and
having two places to set `ServerPVE` guarantees they disagree one day.

## The flags editor

On the instance page, **Launch** tab; on the cluster page, **Launch** tab. The top line reminds you:
`Every start also passes -log -servergamelog; the console depends on the log they produce, so they
are not optional.`

Each typed flag has a label, a description, and three buttons:

| Label | Switch when on | Description shown |
|---|---|---|
| **BattlEye disabled** | `-NoBattlEye` | `The anti-cheat is skipped; it only matters for public servers. Off enables BattlEye.` |
| **Tribe logs in game log** | `-ServerGameLogIncludeTribeLogs` | `Tribe events are written into ShooterGame.log, so they show in the console.` |
| **Tribe logs over RCON** | `-ServerRconOutputTribeLogs` | `Tribe events are also sent to RCON clients.` |
| **Exclusive join** | `-exclusivejoin` | `Only whitelisted players can join.` |
| **No wild babies** | `-NoWildBabies` | `Wild creatures never spawn as babies.` |
| **Prevent spawn animations** | `-PreventSpawnAnimations` | `Players and creatures appear without the spawn-in animation.` |
| **Use store** | `-UseStore` | `Player profiles are kept in the cluster's store rather than per server.` |
| **Convert to store** | `-ConvertToStore` | `One-time migration of existing profiles into the store; turn it off again after one start.` |

The three buttons are **On**, **Off**, and either **Default** (a cluster or a standalone instance) or
**Inherit** (a cluster member). With the third one selected, a line under the flag shows what will
happen: `Default: on · passes -NoBattlEye` or `Inherited from the cluster: off ·
-exclusivejoin is not passed`. Every flag defaults to off except **BattlEye disabled**, which
defaults to on: the manager passes `-NoBattlEye` unless you set that flag to **Off**. The main use
here is a private cluster, and BattlEye adds a requirement on the client, so it starts off and is one
click to turn on.

Below the flags:

- **Server platform**: `Crossplay filter passed as -ServerPlatform, e.g. ALL, PC, or PC+XSX. Leave
  empty for the game's default.` A member's empty field inherits the cluster's; the placeholder shows
  `inherit: PC+XSX` or `inherit (unset)`.
- **Active event**: `Seasonal event name passed as -ActiveEvent.` Same inheritance.
- **Additional arguments**: `Free text appended after the manager's own options. Options the manager
  owns (-port, -mods, -clusterid, the typed flags above) and anything containing a question mark are
  rejected.` On a member, `The cluster adds <text> first.` when the cluster has any.

Problems with the additional arguments are listed as you type and **Save launch options** stays
disabled until they are gone. **Reset** returns to the saved values. In the wizard the same editor
appears on the **Launch options** step; there **Save launch options** keeps the values for the
summary, and the hint says `the defaults are fine for a first server`.

Saved flags live on the instance or cluster row as nullable values: `null` is **Inherit** on a member
and **Default** elsewhere. Nothing is written to disk; the line is built at every start.

## How a cluster member resolves each setting

A cluster member resolves each flag on its own: the member's **On** or **Off** wins, **Inherit** takes
the cluster's value, and a cluster flag left on **Default** means off (on for BattlEye disabled).
**Server platform** and **Active event** behave the same way per field. Additional arguments are not
overridden but concatenated: the cluster's text first, then the member's, joined by one space.

A standalone instance has no base; **Default** is the flag's own default.

## The command-line preview

Under the editor on the instance page, **What a start would run** with a **Refresh** button shows the
exact line, prefixed `ArkAscendedServer.exe`, that the next **Start** would pass. It is rebuilt after
every save on the Launch, Config, Mods, and Settings tabs. Above the line, any problem that would
refuse the start, and `Notes from the INI pipeline` with the reserved-key warnings from the generated
configuration ([configuration-files.md](configuration-files.md)). The same problem is repeated in the
header notice `Start would be refused.` on every tab except Config while the instance is stopped.

## The order the arguments come out in

The argument list is built from the database row, never from a shell string, and passed to the
process as individual arguments in this order:

```
<MapKey>?listen?AltSaveDirectoryName=<slug>
-port=<game port>
-WinLiveMaxPlayers=<max players>
-clusterid=<cluster id> -ClusterDirOverride=<DataRoot>\Clusters\<cluster slug>   (members only)
-mods=<map mod>,<cluster mods>,<instance mods>                                   (when any; duplicates dropped)
-log -servergamelog
-ServerGameLogIncludeTribeLogs  -ServerRconOutputTribeLogs                        (when on)
-NoBattlEye                                                                      (unless BattlEye disabled is Off)
-exclusivejoin -NoWildBabies -PreventSpawnAnimations -UseStore -ConvertToStore   (each when on)
-ServerPlatform=<value> -ActiveEvent=<value>                                     (when set)
<additional arguments, tokenized>
```

What the manager adds itself, and why:

| Argument | Source | Reason |
|---|---|---|
| `<MapKey>` | The instance's map (`TheIsland_WP`, ...) | The map is the first argument. |
| `?listen` | Fixed | Dedicated server. |
| `?AltSaveDirectoryName=<slug>` | The instance slug | Keeps the world under `Saved\<slug>\<MapKey>\` and is the token the service uses to recognize the process again after a restart. |
| `-port=` | **Game port** | Mandatory; the game ignores `Port` in the INI. |
| `-WinLiveMaxPlayers=` | **Max players** | ASA ignores the INI `MaxPlayers`; this flag is the cap. |
| `-clusterid=`, `-ClusterDirOverride=` | The cluster | Transfers. Never on a standalone instance. |
| `-mods=` | The map's mod, then cluster mods in cluster order, then instance mods in instance order | Load order. A custom map's mod comes from the map row, not from any list. |
| `-log -servergamelog` | Fixed | Produces `ShooterGame.log`, which the console tails and the player tracker reads. |

Those last two are not optional because the console, the startup markers, and the player list all
come from the log they produce. A server without them would be invisible to the manager.

## How free text is read, and why it is fenced in

Additional arguments are split on whitespace; double quotes group a token and are stripped
(`-foo="a b"` becomes one argument `-foo=a b`). There is no escape for a literal quote. Each token's
name (the part before `=`) is compared case-insensitively against the reserved list. The same
validation runs when you save (the editor), when the wizard creates the instance, and when the start
builds the line, so a value that got in by another route is still caught.

The field is there for the long tail, but it may not name anything the manager already emits. Two
`-port` arguments on one line would let the text silently win over the port the manager checked for
collisions, and a free-text `?` could break re-attach, since the `AltSaveDirectoryName` token is also
the key the service matches a running process against.

## What stops a save or a start

| Message | Meaning and what to do |
|---|---|
| `'-port=7777' is managed by the manager; set it in the instance instead.` | A manager-owned option in additional arguments. Use the Settings tab (ports, players), the wizard (map, cluster), or the Mods tab. |
| `'-NoBattlEye' has a typed launch option; use the typed option instead of additional arguments.` | Use the three-way control for that flag. |
| `'-foo?bar' contains '?'; map parameters belong in the INI, not in additional arguments.` | Put the key in `GameUserSettings.ini` or `Game.ini`. |
| `'-' is not a valid argument.` | A lone hyphen or empty token. |
| `Additional arguments contain an unbalanced double quote.` | Close the quote. |
| `ServerPlatform must not contain '?'.` (or `'='`, `line breaks`) / `ActiveEvent must not contain ...` | The two text fields are written verbatim as `-ServerPlatform=<value>`, so those characters are refused. |
| At start: `Server platform must not contain whitespace.` / `Active event must not contain whitespace.` / `Cluster key must not contain whitespace.` | The value would split into two arguments. Fix the field; a cluster key is edited on the cluster's Settings tab. |
| At start: `Mod id -5 is not a valid CurseForge project id.` | A mod row with a non-positive id; fix it on the Mods page. |
| At start: `Instance '<name>' has no map.` | The instance's map row is gone. The map cannot be changed after creation, so recreate the instance. |
| At start: `Game port 70000 must be between 1 and 65534.` / `Max players 0 must be between 1 and 500.` | Out of range on the Settings tab. |

None of these touch a running server: launch options are read at start, and a member that is running
keeps its old line until it restarts.
