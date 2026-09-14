# Settings and export

The **Settings** page holds the App Settings that live in the database and apply immediately when
saved, shows the host settings that come from `appsettings.json` read-only, prints the app version
and the disclaimer, and has two buttons that act on the configuration data: **Export config
backup**, which writes a consistent copy of the database, and **Restore INI files from database**,
which rewrites every INI source file on disk from its database mirror.

## What it does

- Edits the runtime settings: launch stagger, stop countdown and timeouts, RCON timeout, console
  backfill, port ranges, backup defaults, SteamCMD `validate`, the CurseForge API key, and the
  manager-wide admin whitelist.
- Validates every value on save and shows the problems under the form; nothing is written until
  all of them pass.
- Shows `DataRoot`, the bind URLs, known proxies, whether plain HTTP is allowed, whether a usable
  password is configured, the hosting model, and the version.
- Exports the database while the service runs, using SQLite's online backup so the copy is
  consistent.
- Restores the INI source files from the database.

## How to use it

The page lead: "Runtime settings live in the database and apply immediately. Host settings come
from appsettings.json and need a service restart."

### App Settings

| Section | Field | Default | Range or rule |
|---|---|---|---|
| Starting and stopping | **Stagger between launches, seconds** | 30 | 0 to 3600 |
| | **Countdown before a stop, minutes** | 1 | 0 to 60; zero skips the broadcast |
| | **Graceful stop timeout, seconds** | 60 | 5 to 3600 |
| | **RCON command timeout, seconds** | 10 | 1 to 300 |
| | **Console history on re-attach, lines** | 200 | 0 to 5000 |
| Ports | **Game port start** / **Game port step** | 7777 / 2 | 1 to 65535 / 1 to 1000 |
| | **RCON port start** / **RCON port step** | 27020 / 1 | 1 to 65535 / 1 to 1000 |
| Backups | **Default interval, minutes** | 30 | 1 to 10080 |
| | **Default backups to keep** | 10 | 1 to 1000 |
| | **Settle window after saveworld, seconds** | 10 | 1 to 600 |
| Game install | **SteamCMD validate** | off | Adds `validate` to every install and update |
| | **CurseForge API key** | empty | No whitespace or control characters; trimmed on save |
| Admin whitelist | the whitelist editor | empty | One id per line, no spaces |

Each field's hint on the page says what it feeds; the pages that use them are
[instances.md](instances.md) (stagger, countdown, timeouts, backfill, ports),
[backups.md](backups.md), [game-updates.md](game-updates.md), [mods.md](mods.md), and
[players-and-whitelists.md](players-and-whitelists.md).

**Save settings** validates and writes; the button shows "Saving…" for about two seconds so the
click visibly did something, then a toast says "Saved settings". **Reset** reloads the stored
values and discards edits.

### Host

"From appsettings.json; edit the file and restart the service to change these."

| Row | Value |
|---|---|
| **Data root** | The resolved `DataRoot`. |
| **Bind URLs** | Every Kestrel endpoint URL. |
| **Known proxies** | The reverse-proxy addresses, or `none`. |
| **Plain HTTP allowed** | `yes (development only)` or `no`. |
| **Password configured** | `yes`, or `no: every login is refused`. `yes` means a usable credential: `ArkAdmin:Password` or a well-formed `ArkAdmin:PasswordHash`. |
| **Hosting** | `Windows service` or `Console`. |
| **Version** | The assembly's informational version, `1.0.0+<commit sha>` for a release build. The same string sits in the rail footer on every page. |

[configuration.md](../configuration.md) explains each host setting and how to change it.

### Configuration data

**Export config backup**: "Export a consistent copy of the database to `Exports` under the data
root. Copying the .db file by hand is only safe with the service stopped." The toast "Exported
config backup" carries the path, and the page shows "Written to *path*".

**Restore INI files from database**: "Rewrite every INI source file on disk from its database copy.
This is the only direction the database ever writes files, and it overwrites whatever is in the
Config folders now." It asks first: "Every Game.ini and GameUserSettings.ini under Clusters\*\Config
and Instances\*\Config is overwritten with the database copy. Unsaved edits on disk are lost."
Confirm with **Overwrite the files**.

At the bottom of the page: "Not affiliated with, sponsored by, or endorsed by Studio Wildcard or
Snail Games. ARK: Survival Ascended and related marks are trademarks of their respective owners."

## What happens underneath

**App Settings** are rows in the `AppSettings` table, one per key, decoded into one settings
object that is cached in memory after the first read and replaced on save. Saving runs the
validation, then updates or inserts each row in one `SaveChanges`. Because the cache is replaced,
every consumer (the launch queue, the port allocator, the backup scheduler, the CurseForge
handler that adds the `x-api-key` header) sees the new value on its next call, with no restart.

**Host values** are read once at startup from `appsettings.json` (plus `appsettings.Production.json`
and environment variables) into a read-only record; the page shows that record. The one exception
is the password: the credential follows configuration reloads, so a changed `Password` or
`PasswordHash` invalidates every cookie on its next request and every circuit at its next
revalidation without a restart, while the **Password configured** row still shows the value from
startup.

**Export** opens a second SQLite connection to `DataRoot\Exports\config-yyyyMMdd-HHmmss.db.tmp`,
runs SQLite's online backup API from the live connection into it (consistent even with
write-ahead-log traffic in flight), closes it, and renames it to `config-yyyyMMdd-HHmmss.db`
(local time). Nothing else is written. The file is a complete copy of the database: clusters,
instances, maps, the mod library and assignments, the INI text mirrors, overrides, known players,
backup records, the maintenance state, and every App Setting, **including the CurseForge API key in
plain text**. The installer restricts `DataRoot\Exports` to `SYSTEM` and administrators for that
reason. No import button exists; to use an export you stop the service, replace
`DataRoot\Data\ArkAscendedServerAdmin.db` (and delete any `-wal` and `-shm` next to it), and start
the service again.

**Restore INI files from database** reads every row of `IniDocuments` (cluster or instance owner,
file, text) and writes each one atomically to its source path, `Clusters\<slug>\Config\<file>` or
`Instances\<slug>\Config\<file>`. It does not touch the generated files under
`ShooterGame\Saved\Config\WindowsServer`, which are rewritten from the source at the next start;
see [configuration-files.md](configuration-files.md).

## Why it works this way

DESIGN.md split configuration in two: host settings that need a restart, in `appsettings.json`,
and runtime settings in SQLite that the UI edits; every path in the database is relative to one
`DataRoot` so the database restores on another box. The same section states the config backup
story: copy the SQLite file, and "restore from database" recreates the INI files. The export
button exists because a raw copy of a SQLite database that is being written is not a consistent
file; the online backup API produces one while the service runs, so the owner never has to stop
the service to keep a configuration copy. The installer's own database copies during an upgrade
are a different thing ([backups.md](backups.md#the-installers-backups_app-copies)).

The key is stored as it is typed because, as the hint on the field says, it is your own key on
your own box and it is only ever sent to CurseForge; the folders that hold the database and its
copies are locked down by the installer instead. The disclaimer and the version are on this page
because it is the one page every owner opens at least once.

## When it refuses or fails

| Where | Message | Meaning and what to do |
|---|---|---|
| Save settings | `<Name> must be between <min> and <max> (was <n>).` for any numeric field, for example `StaggerDelaySeconds must be between 0 and 3600 (was 5000).` | Fix the field. Nothing was saved. |
| Save settings | `CurseForgeApiKey must not contain whitespace or control characters.` | Re-paste the key without spaces or line breaks. |
| Save settings | `AdminWhitelist must hold one id per line with no spaces.` | Fix the offending line in the whitelist editor. |
| Export | The toast is missing and an error page or notification appears | `DataRoot\Exports` could not be written (disk full, or the folder ACL changed). Check the service log ([troubleshooting.md](troubleshooting.md#where-every-log-lives)). |
| Restore | `Restore failed: <error>` | A source file could not be written, typically a folder that no longer exists because the instance was deleted outside the manager, or a permission problem. The message is the OS error. |
| Host | **Password configured** shows `no: every login is refused` | Neither `ArkAdmin:Password` nor a well-formed `ArkAdmin:PasswordHash` is set. Run `install.ps1 -SetPassword` ([hosting.md](../hosting.md#changing-the-password)). |
