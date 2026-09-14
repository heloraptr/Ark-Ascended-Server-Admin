# Mods

Mods are CurseForge projects, identified by their numeric project id, which is also what the
server's `-mods=` argument takes. The **Mods** page holds one library for the whole manager; clusters
and instances pick from it. With a CurseForge API key the library is filled by search and carries
names, summaries, and thumbnails; without one you add ids by hand and name them yourself. The
manager never downloads a mod: the server fetches mods itself at start from the ids on its command
line.

## What it does

- Keeps a global mod library (id, name, summary, thumbnail, last CurseForge modification date).
- Searches CurseForge and adds by id when an API key is set on Settings; adds by id with a typed
  name when it is not.
- Shows where each entry is used (maps, clusters, instances) and refuses to remove an entry that is
  still referenced.
- Puts a cluster's mods on every member, locked, ahead of the member's own mods.
- Treats a custom map's own mod as part of the map: it loads first and cannot be put on any list.
- Builds the `-mods=` argument from the map mod, the cluster mods, and the instance mods, in that
  order.

## How to use it

**The API key.** Settings, *Game install*, **CurseForge API key**. The field's hint on the page:
"Stored in plain text in the database. Enables mod search and metadata; without it mods are added
by id. The key is your own and its use is subject to CurseForge's API terms." Get a key from
CurseForge's developer console; the manager sends it as the `x-api-key` header on every request and
picks up a changed key without a restart. [configuration.md](../configuration.md#app-settings-in-the-database)
says who can read the database file.

**Adding a mod, with a key.** On **Mods**, *Add a mod*: type a name in "Search CurseForge, e.g.
Awesome Spyglass" and press **Search** (or Enter). Each hit shows the thumbnail, name, summary,
id, author, "updated *N* ago", and either **Add** or "in library". Or type a project id in
"project id" and press **Add**; the name, summary, and thumbnail come from CurseForge.

**Adding a mod, without a key.** The page shows the notice "No CurseForge API key." with the fields
**Project id** ("The number in the mod's CurseForge URL.") and **Name**, and **Add to library**. The
entry has no summary or thumbnail until a key is added and **Refresh names** is pressed.

**The library** lists every entry with its id, "updated *N* ago" when known, its summary, and where
it is used ("map *X*", "cluster *Y*", or instance names) or "not used". A map's own mod carries the
tag "map mod · *Map name*". Each row has an open-on-CurseForge button and a remove button, which is
disabled with the tooltip "Remove it from every list first" while the entry is referenced.
**Refresh names** at the top re-fetches metadata for every entry (key required) and reports how
many changed.

**Cluster mods.** On a cluster page, the **Mods** tab: "Mandatory for every member and loaded before
each member's own mods." Add from the library, reorder with the arrows, **Save mods**.

**Instance mods.** On an instance page, the **Mods** tab shows an ordered list: the map mod first
(locked, "from the map"), then the cluster's mods (locked, "from the cluster"), then the instance's
own with move up, move down, and remove. "Add a mod from the library" is a filterable drop-down; it
never offers a map mod. **Save mods** writes the list. The footer reminds you: "Order matters: mods
load in this order, the map's own mod first, then cluster mods. New mods on the list need a
restart." The wizard's *Mods* step is the same editor ([instance-creation.md](../instance-creation.md)).

## What happens underneath

The library is the `ModLibrary` table (id, name, summary, thumbnail URL, `DateModified`,
`AddedAt`). Assignments are `ClusterMods` and `InstanceMods`, each with an `Order` column; saving a
list replaces the rows. Nothing about mods is written to disk by the manager.

CurseForge calls go to `https://api.curseforge.com` with `gameId=83374` (ARK: Survival Ascended):
`/v1/mods/search` for **Search** (every page of results is fetched), `/v1/mods/{id}` for **Add**
by id, and `POST /v1/mods` for **Refresh names**. Adding an entry that already exists updates its
metadata instead of failing.

**Usage** is computed on the fly from the three tables plus `Maps.ModId`. Removing is refused
while any of them reference the id; deleting an instance or cluster removes its assignment rows,
which frees the entry.

**The launch line.** At every start (and in the **Launch** tab's "What a start would run" preview)
the argument builder concatenates the map's mod id, the cluster's mods in cluster order, and the
instance's mods in instance order, drops duplicates, and emits one `-mods=<id>,<id>,...`. No mods,
no argument. The server downloads and loads them; the manager's console shows the game's own
output about that in `ShooterGame.log` ([launch-options.md](launch-options.md)).

**Map mods.** A custom map row on the Maps page carries the id of the mod that ships it. Saving
the map puts that id into the library if it is missing (with CurseForge metadata when a key is set,
otherwise named after the map). Every list save runs the same guard: an id that is some map's mod
is refused with "Map mods load automatically with their map and cannot be listed here: *id*
(*Map name*)." The editors also filter map mods out of their drop-downs. See [maps.md](maps.md).

## Why it works this way

DESIGN.md decided on a global library fed by CurseForge search with manual id entry as the
fallback, cluster mods that are mandatory for every member and shown locked on the instance, and an
emitted order of cluster mods then instance mods; it also decided that the manager never downloads
mods, because the server does that itself from `-mods=`. The library exists so an id is looked up
once and named once, and so removing a mod from CurseForge's side does not silently break a list:
the name and id stay in the database until you remove them.

A custom map's mod is bound to the map rather than to a mod list because the game needs it loaded
before any other mod and on every instance that runs the map. Putting it on the map row means
choosing the map is enough, a cluster member cannot forget it, and re-releasing the map under a new
id is one edit on the Maps page rather than a hunt through every list. The API key is stored in
plain text by design: the code's own note is that it is a read-only key on the owner's box, and
the installer restricts the database folder to `SYSTEM` and administrators.

## When it refuses or fails

| Where | Message | Meaning and what to do |
|---|---|---|
| Search, Add by id, Refresh names | `Add a CurseForge API key on the Settings page to search. Mods can still be added by id.` | No key. Add one on Settings or use the manual form. |
| Search, Add, Refresh names | `CurseForge rejected the API key. Check it on the Settings page.` | CurseForge answered 401 or 403. The key is wrong or revoked. |
| Search, Add, Refresh names | `CurseForge could not be reached: <error>` | Network or a non-2xx answer. Retry later; the error is the HTTP client's message. |
| Add | `A CurseForge mod id is a positive number.` | Zero or negative id. |
| Add without a key | `Give the mod a name so it can be recognized in the lists.` | The **Name** field is empty. |
| Remove | `Remove it from cluster X, instance Y, map Z first.` | The entry is referenced. Take it off those lists (or change the map's mod id) first. |
| Save mods (cluster, instance, wizard) | `One of the chosen mods is no longer in the library.` | The entry was removed while the editor was open. Reload the page. |
| Save mods (cluster, instance, wizard) | `Map mods load automatically with their map and cannot be listed here: <id> (<map>).` | The id belongs to a custom map. Choose the map instead. |
| Launch preview / Start | `Mod id <n> is not a valid CurseForge project id.` | A non-positive id reached the launch builder; fix the row on the Mods or Maps page. |
| Settings | `CurseForgeApiKey must not contain whitespace or control characters.` | The pasted key has a stray space or line break. |

A mod that CurseForge has updated is not detected by the manager beyond the "updated *N* ago"
stamp; whether the server picks up a new version is between the server and CurseForge at the next
start.
