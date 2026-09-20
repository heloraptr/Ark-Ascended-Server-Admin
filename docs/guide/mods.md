# Mods

Mods are CurseForge projects, identified by their numeric project id, which is also what the
server's `-mods=` argument takes. The **Mods** page holds one library for the whole manager; clusters
and instances pick from it. The manager never downloads a mod: the server fetches mods itself at
start from the ids on its command line.

The library exists so that an id is looked up once and named once, and so that a mod disappearing
from CurseForge does not quietly break a list. The name and the id stay in the database until you
remove them.

## The CurseForge API key

With a key, the library is filled by search and carries names, summaries, and thumbnails. Without
one, you add ids by hand and name them yourself.

The field is on Settings, under *Game install*, as **CurseForge API key**. Its hint on the page:
"Stored in plain text in the database. Enables mod search and metadata; without it mods are added
by id. The key is your own and its use is subject to CurseForge's API terms." Get a key from
CurseForge's developer console; the manager sends it as the `x-api-key` header on every request and
picks up a changed key without a restart.

The key is stored exactly as you type it. It is a read-only key on your own box and it only ever
goes to CurseForge, so the installer restricts the database folder to `SYSTEM` and administrators
rather than the app hiding the value.
[configuration.md](../configuration.md#app-settings-in-the-database) says who can read the database
file.

## Adding a mod

**With a key.** On **Mods**, *Add a mod*: type a name in "Search CurseForge, e.g. Awesome Spyglass"
and press **Search** (or Enter). Each hit shows the thumbnail, name, summary, id, author, "updated
*N* ago", and either **Add** or "in library". Or type a project id in "project id" and press
**Add**; the name, summary, and thumbnail come from CurseForge.

**Without a key.** The page shows the notice "No CurseForge API key." with the fields **Project id**
("The number in the mod's CurseForge URL.") and **Name**, and **Add to library**. The entry has no
summary or thumbnail until a key is added and **Refresh names** is pressed.

Adding an id that is already in the library updates its metadata instead of failing.

The Mods page is not the only way in. Every mod list — a cluster's, an instance's, and the
wizard's — can take a project id or a search of its own and put the result straight into the
library, so a mod you have just found does not cost you the page you are on.

## The library list

Every entry shows its id, "updated *N* ago" when known, its summary, and where it is used ("map
*X*", "cluster *Y*", or instance names) or "not used". A map's own mod carries the tag "map mod ·
*Map name*". Each row has an open-on-CurseForge button and a remove button, which is disabled with
the tooltip "Remove it from every list first" while the entry is referenced. **Refresh names** at
the top re-fetches metadata for every entry (key required) and reports how many changed.

Usage is worked out on the spot from the cluster lists, the instance lists, and the `Maps.ModId`
column. Removing is refused while any of them reference the id; deleting an instance or a cluster
removes its assignment rows, which frees the entry.

## Cluster mods and instance mods

On a cluster page, the **Mods** tab: "Mandatory for every member and loaded before each member's own
mods." Add from the library, reorder with the arrows, **Save mods**. The toggle at the start of each row
disables a mod without removing it: it keeps its place in the list, is left out of `-mods` on every
member, and shows as "disabled there" on the members' own lists.

On an instance page, the **Mods** tab shows an ordered list: the map mod first (locked, "from the
map"), then the cluster's mods (locked, "from the cluster"), then the instance's own with move up,
move down, and remove, plus the same enable/disable toggle. A disabled mod keeps its place but is
left out of the start command, which is the quick way to sort out a mod conflict without rebuilding
the list. "Add a mod from the library" is a filterable drop-down (case does not matter); it never
offers a map mod. **Save mods** writes the list. The footer reminds you: "Order matters: mods load in
this order, the map's own mod first, then cluster mods. A disabled mod keeps its place but is left out
of the start command. Changes to the list need a restart." The wizard's
*Mods* step is the same editor ([instance-creation.md](../instance-creation.md)).

Under the drop-down, every one of those lists carries a second way in, for a mod the library does
not hold yet. It starts with the question "Not in the library?". With a key it is one box, "Search
CurseForge, or paste a project id", and **Look up**: a bare number is fetched by id and added at
once, anything else is a search, and each hit gets **Add** or the word "listed" when the list
already has it. Without a key it is the same **project id** and **name** pair the Mods page offers,
under the same hint about what a key buys you.

One press does both halves of the job. The mod goes into the library through the same call the Mods
page makes, and the row is appended to the list you are editing: "Added *Name* to the library — It
is on this list too; Save mods keeps it there." The library drop-down above picks it up in the same
moment, so a second list on the same page can use it. Nothing reaches the instance or the cluster
until **Save mods**, and in the wizard nothing is written until **Create instance**. A lookup that
fails says so under the box, where the rest of the editor's problems appear, and the list is left
alone.

## The order they load in

At every start (and in the **Launch** tab's "What a start would run" preview) the argument builder
concatenates the map's mod id, the cluster's enabled mods in cluster order, and the instance's
enabled mods in instance order, drops duplicates, and emits one `-mods=<id>,<id>,...`. No mods, no argument. The
server downloads and loads them; the manager's console shows the game's own output about that in
`ShooterGame.log` ([launch-options.md](launch-options.md)).

## Map mods

A custom map row on the Maps page carries the id of the mod that ships it. Saving the map puts that
id into the library if it is missing (with CurseForge metadata when a key is set, otherwise named
after the map). Every list save runs the same guard: an id that is some map's mod is refused with
"Map mods load automatically with their map and cannot be listed here: *id* (*Map name*)." The
editors also filter map mods out of their drop-downs. See [maps.md](maps.md).

The mod sits on the map row rather than on a mod list because the game needs it loaded before any
other mod, on every instance that runs the map. Choosing the map is then enough, a cluster member
cannot forget it, and a map re-released under a new id is one edit on the Maps page instead of a
hunt through every list.

## Where the data lives

The library is the `ModLibrary` table (id, name, summary, thumbnail URL, `DateModified`,
`AddedAt`). Assignments are `ClusterMods` and `InstanceMods`, each with an `Order` and an `Enabled` column;
saving a list updates the rows in place and removes the ones that left the list. Nothing about mods is written to disk by the manager.

CurseForge calls go to `https://api.curseforge.com` with `gameId=83374` (ARK: Survival Ascended):
`/v1/mods/search` for **Search** (every page of results is fetched), `/v1/mods/{id}` for **Add**
by id, and `POST /v1/mods` for **Refresh names**.

## Messages, and what they mean

| Where | Message | Meaning and what to do |
|---|---|---|
| Search, Add by id, Refresh names | `Add a CurseForge API key on the Settings page to search. Mods can still be added by id.` | No key. Add one on Settings or use the manual form. |
| Search, Add, Refresh names | `CurseForge rejected the API key. Check it on the Settings page.` | CurseForge answered 401 or 403. The key is wrong or revoked. |
| Search, Add, Refresh names | `CurseForge could not be reached: <error>` | Network or a non-2xx answer. Retry later; the error is the HTTP client's message. |
| Add | `A CurseForge mod id is a positive number.` | Zero or negative id. |
| Add without a key | `Give the mod a name so it can be recognized in the lists.` | The **Name** field is empty. |
| "Not in the library?" on a mod list | `Mod <id> is already on this list.` | The id is on the list already, whether as the map's mod, a cluster mod, or one of your own rows. |
| Remove | `Remove it from cluster X, instance Y, map Z first.` | The entry is referenced. Take it off those lists (or change the map's mod id) first. |
| Save mods (cluster, instance, wizard) | `One of the chosen mods is no longer in the library.` | The entry was removed while the editor was open. Reload the page. |
| Save mods (cluster, instance, wizard) | `Map mods load automatically with their map and cannot be listed here: <id> (<map>).` | The id belongs to a custom map. Choose the map instead. |
| Launch preview / Start | `Mod id <n> is not a valid CurseForge project id.` | A non-positive id reached the launch builder; fix the row on the Mods or Maps page. |
| Settings | `CurseForgeApiKey must not contain whitespace or control characters.` | The pasted key has a stray space or line break. |

A mod that CurseForge has updated is not detected by the manager beyond the "updated *N* ago"
stamp; whether the server picks up a new version is between the server and CurseForge at the next
start.
