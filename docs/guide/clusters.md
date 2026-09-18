# Clusters

A cluster is a set of instances that share INI source files, mandatory mods, base launch options, an
admin whitelist, a cluster id, and a transfer directory, so players can move characters, items, and
creatures between the maps.

The Clusters page lists your clusters; each cluster page has its members, its INI editors, its mods,
its launch flags, and its settings. Everything on it applies to every member at that member's next
start. An instance that is not in a cluster is standalone and carries the same things itself.

## What a cluster shares

| Shared by a cluster | Where it comes from | How a member can differ |
|---|---|---|
| `Game.ini` and `GameUserSettings.ini` source text | `DataRoot\Clusters\<slug>\Config\` | Per-instance **Overrides** (single keys) on the member's Config tab. |
| Mods | The cluster's **Mods** tab; loaded before the member's own | A member adds mods after them, never removes one; a mod disabled on the cluster is off for every member. |
| Launch options | The cluster's **Launch** tab, the base | A member sets any flag to **On** or **Off** instead of **Inherit**; its additional arguments are appended after the cluster's. |
| Admin whitelist | The cluster's **Settings** tab | A member has its own list; the union is written. |
| Cluster id | `-clusterid=<id>` on every member | Cannot differ; that is the point. |
| Transfer directory | `-ClusterDirOverride=<DataRoot>\Clusters\<slug>` on every member | Cannot differ. |

Not shared: the map, session name, ports, player cap, backup interval and retention, and the world.

Everything the members have to agree on lives in one place, and the per-member files are generated
from it, instead of you keeping several INI files in step by hand. The cluster owns the raw text; the
manager takes it, applies the member's typed fields and overrides, and writes the generated files
before every launch. The price is that a member cannot have its own INI file, so the answer to "this
one map needs a different difficulty" is an override, not a copy.

## Creating one

**Clusters** in the sidebar, then **New cluster**. The form has two fields:

- **Cluster name**. The hint: `Also the initial cluster id passed as -clusterid; both can be changed
  on the cluster page.` The slug (lower-case letters, digits, hyphens, at most 32 characters) is
  derived from it once and names `Clusters\<slug>`; it never changes.
- **INI files start from**: `Game defaults`, `Blank files`, `Copy from <standalone instance>`, or
  `Copy from the <cluster> cluster`. Copies are snapshots of the current source text; later edits on
  the source are not followed.

**Create cluster** creates the row, the folder, and the two INI files, then opens the cluster page.
With `Game defaults` the `GameUserSettings.ini` has an empty `ServerAdminPassword=`; set it on the
**Config** tab before creating members, because a member cannot start without it
([configuration-files.md](configuration-files.md)).

On disk that is:

```
<DataRoot>\Clusters\<slug>\
  Config\Game.ini
  Config\GameUserSettings.ini
```

and in the database a `Clusters` row (name, slug, cluster key = slug, empty whitelist, default launch
flags) plus two `IniDocuments` mirror rows with the SHA-256 of each file. Cluster mods are
`ClusterMods` rows in order, each with an `Enabled` flag.

## The cluster list

One row per cluster: the name, `id <cluster id> · <slug>`, `2 members`, `1 cluster mod`, and an
arrow to open it.

The empty list reads `No clusters yet.` with `Standalone instances work without one. Create a
cluster when you want players to travel between maps.`

## The cluster page

Header: the name, `cluster id <id> · 2 members · 1 mod`, **Start all**, **Stop all** (each acts on
the members that are eligible), and a delete icon that is disabled while the cluster has members.
While any member runs, a notice says `Members are running. INI, mod, and launch changes apply to each
member at its next start.`

| Tab | Content |
|---|---|
| **Members** | One row per member with the state triangle, name, session name, map, ports, and state label, and an arrow to the instance page. **New instance** in the header opens the wizard with this cluster already chosen. Empty: `No members yet.` with the same **New instance** button. |
| **Config** | The `GameUserSettings.ini` and `Game.ini` editors for the cluster's source files. Same editor as a standalone instance's ([configuration-files.md](configuration-files.md)). |
| **Mods** | `Mandatory for every member and loaded before each member's own mods. A custom map's own mod loads ahead of these on that member alone and is not listed here.` The ordered list with **Add**, an enable/disable toggle per row, move up and down, remove, **Save mods**. A disabled mod stays listed and is left out of every member's `-mods`. |
| **Launch** | `The base every member starts from; a member can override each flag.` The flags editor with **Default** / **On** / **Off** per flag ([launch-options.md](launch-options.md)). |
| **Settings** | **Cluster name** (`The folder stays Clusters\<slug>.`), **Cluster id** (`Passed as -clusterid; every member must share it for transfers to work. Changing it strands existing transfers.`), **Admin whitelist** (`Merged with the Settings list and each member's own list at start.`). **Save settings**, **Reset**. |

## Members, and why you cannot move one

Membership is chosen on the wizard's **Cluster** step and cannot be changed afterwards: the
instance's Settings tab has no cluster field, and there is no move action. The slug, the world
directory, the INI ownership, and the launch line all depend on it, and changing it under a live
world has more failure modes than recreating an instance.

To move a server between clusters, or from standalone into a cluster, create a new instance in the
target and delete the old one, keeping the world; the world files under `Archive\` can then be copied
into the new instance's `ShooterGame\Saved` by hand while it is stopped. The new instance has a new
slug, so the world directory name under `Saved\<slug>\<MapKey>\` changes too.

## What a member's start does with all this

At every member's start the manager reads the cluster's two source files (not the member's; a member
has no `Config\` folder), generates the member's files from them, unions the manager-wide, cluster,
and instance whitelists into the member's `AllowedCheaterAccountIDs.txt`, and passes on the command
line:

```
-clusterid=<cluster id> -ClusterDirOverride=<DataRoot>\Clusters\<slug>
-mods=<map mod>,<enabled cluster mods in cluster order>,<enabled instance mods in instance order>
```

plus the resolved launch flags (cluster base, instance override). The transfer directory is the
folder itself; the game writes uploaded characters, items, and creatures under it, and every member
that points at the same folder with the same id can download them. Transfers between servers on one
box therefore never touch the network.

`-ClusterDirOverride` is passed for you because it is the one setting the game requires for
cross-server transfers, and getting it wrong on one member breaks transfers silently. Cluster mods
are mandatory and locked in the member's list for the same reason: a player who uploads a creature
from a mod to a server without that mod loses it.

The cluster id is validated on save: required, at most 64 characters, no `?`, `=`, line breaks, or
spaces, because it is written verbatim into the command line.

## Deleting a cluster

The delete icon is enabled only when the cluster has no members. The confirmation says exactly what
happens: `The cluster row, its mod list, and its INI mirror rows are removed. The folder under Clusters
stays on disk.` The transfer directory and the source INI files are left where they are.

## Messages the cluster pages give back

| Message | Meaning and what to do |
|---|---|
| `Cluster name is required.` / `Cluster name must be 100 characters or fewer.` | On create and save. |
| `A cluster named '<name>' already exists.` | Names are unique ignoring case. |
| `The cluster folder could not be prepared: ...` | Creating `Clusters\<slug>` or seeding the INI files failed; the row is removed again. Check permissions on `DataRoot\Clusters` and, for a copy, that the source still exists. |
| `Cluster id is required; it is passed as -clusterid so members can transfer between each other.` | The **Cluster id** field is empty. |
| `Cluster id must be 64 characters or fewer.` / `ClusterKey must not contain '?'.` (or `'='`, `line breaks`) / `Cluster id must not contain spaces.` | The id goes on the command line verbatim. |
| `Move or delete its instances first: <names>.` | Delete asked with members. There is no move; delete or recreate them. |
| `The cluster no longer exists.` | The page was open while the cluster was deleted elsewhere. |
| `One of the chosen mods is no longer in the library.` | The mod list references a library entry that was removed; reload the page and save again. |
| Notes from the INI pipeline, on a member's Launch tab | Reserved keys in the cluster's source text are replaced at start; see [configuration-files.md](configuration-files.md#what-the-editor-the-override-dialog-and-a-start-refuse). |

Members that are running keep their old files until they restart; the notice on the cluster page
says so. There is no "apply now".
