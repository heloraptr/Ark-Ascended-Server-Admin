# Instances

An instance is one `ArkAscendedServer.exe` process and the definition behind it: name, slug, map,
session name, game and RCON port, player cap, whitelist, mods, launch options, and either a cluster
or its own INI files.

The Instances page at `/` is the dashboard: every server the box runs, grouped by cluster, with its
state, ports, last backup, and the start, stop, restart, and backup buttons. Each row opens the
instance page, where one server is configured and watched. Start, stop, restart, back up, and delete
all finish with a toast that says what happened or why it was refused. Creating an instance is the
wizard, described step by step in [instance-creation.md](../instance-creation.md).

## The dashboard

The lead line reads `Every server this box runs, grouped by cluster.` or, once you have some,
`2 instances running of 3.` **New instance** opens the wizard. With no instances the page shows
`No instances yet.` and **Create the first instance**.

Rows are grouped under a section per cluster (title: the cluster name; summary:
`cluster <cluster id> · 1 of 2 up`; actions **Start all** and **Stop all** for that group), and a
final **Standalone** section for instances without a cluster. Each row shows:

| Column | Content |
|---|---|
| Checkbox | Adds the row to the selection. A bar appears above the groups: `2 instances selected`, **Start selected**, **Stop selected**, **Clear**. |
| Name | The small triangle for the state, the instance name (a link to its page), the session name under it. |
| Map | The map name and `3 mods` or `vanilla`. The count is what a start passes in `-mods`: the map's own mod, the cluster's enabled mods, and the instance's enabled mods. |
| Ports | `7777 game`, `27020 rcon`. |
| State and backup | The state label ([README](README.md#instance-states)) and the last backup: `Backed up 12 min ago`, `Skipped 1 h ago`, `Failed`, or `no backup yet`. Hovering the backup shows its reason. When a scheduled action applies to the instance, a third line: `Next action 03:00`, `Next action tomorrow 03:00`, or `Next action in 12 min`; hovering it shows the full date and time ([scheduled actions](#scheduled-actions)). |
| Actions | While a process is live: **Stop**, **Restart**, **Back up now** (enabled only when **Running**). While stopping: **Stop now** (skips the countdown), which turns into a disabled **Stopping…** once the exit has been requested. Otherwise **Start** (disabled when the state is **Unknown**). Always: **Open console**. |

**Start all**, **Stop all**, **Start selected**, and **Stop selected** submit every eligible row at
once; the launch queue staggers the starts. The toast is `Started 3 instances`, or
`Started 2 of 3` with the refused names and reasons.

A notice above the groups appears only during a game update or its recovery
([game-updates.md](game-updates.md)): the phase, one line per instance (`waiting`, `done`, or an
error with **Retry** and **Skip**), and **Resume update** while the phase is stopping or updating.

## Start

**Start** on a row or on the instance page, **Start all**, **Start selected**, and the wizard's
**Start the server right away** all run the same sequence. The toast `Started <name>` arrives when
the process is up and its identity is saved; refusals arrive as `Could not start <name>` with the
reason. The console shows the details as they happen.

1. Refused unless the service is Ready, no update holds the maintenance gate, no other operation
   holds this instance, and the instance has no live process.
2. Queued. The launch queue runs one launch at a time and waits *Stagger between launches, seconds*
   (Settings, 30 by default) after each successful launch before the next one. A first launch does
   not wait.
3. The junction tree is checked and repaired.
4. The generated INI files and `AllowedCheaterAccountIDs.txt` are written; every reserved key the
   source contained shows up in the console as `Config: Reserved key 'Port' in [SessionSettings] of
   GameUserSettings.ini (line 12) was replaced by the manager value.` ([configuration-files.md](configuration-files.md)).
5. `ServerAdminPassword` and `RCONPort` are read back from the generated file; an empty password
   refuses the start.
6. Ports are checked against every other instance, the web UI's port, and the operating system's live
   UDP and TCP listener tables.
7. The two firewall rules `ArkAscendedServerAdmin-<instance id>` (inbound UDP, game port and
   game port + 1, all profiles) are created or repaired. A firewall failure does not stop the launch:
   `Firewall: <error> The server starts anyway; open UDP 7777-7778 manually if players cannot join.`
8. The command line is built ([launch-options.md](launch-options.md)) and the process started through
   `Instances\<slug>\ShooterGame\Binaries\Win64\ArkAscendedServer.exe`, without a console window.
   The console prints `Launched pid <n>: <exe> <arguments>`.
9. The PID and process start time are written to the database (three retries over about ten
   seconds). Then the log tail, the RCON probe (every 15 s), and the liveness poll (every 2 s) begin.

The state is **Starting** until the first RCON probe answers, then **Running**.

Step 6 is the one that earns its keep. The game does not report a failed bind: it starts, answers
RCON, and is never reachable from outside. Refusing the start is the only honest place to catch that.
The queue in step 2 exists because several servers loading at once fight over the disk and RAM, and
the stagger delay is the one knob you get.

## Stop

**Stop** on a row or the instance page, **Stop all**, **Stop selected**, the first half of
**Restart**, delete, and a game update all run the same stop job. It holds the instance's lock for
its whole duration, so a start, backup, or delete asked for meanwhile is refused with
`operation in progress`.

1. The state becomes **Stopping**.
2. Countdown: once a minute for *Countdown before a stop, minutes* (Settings, 1 by default), the
   server broadcasts `Server shutting down in 2 minutes.` ... `Server shutting down in 1 minute.`,
   then `Server shutting down now.` Each command is echoed to the console as `RCON: broadcast ...`.
   The countdown targets an absolute deadline set when the stop is accepted, so a slow reply never pushes
   `doexit` later than that deadline plus one reply; if it still goes out more than 5 s late the console
   says so (`doexit went out 7 s after the deadline ...`). **Stop now** on the row or the page ends the
   countdown early (`Countdown skipped.`); zero minutes in Settings skips it always.
3. `RCON: doexit`. The server replies `Exiting...`, saves the world itself (the log shows
   `Saving world...` before `Closing by request`), and exits with code -1, which is normal. No
   separate `saveworld` is sent.
4. The job waits up to *Graceful stop timeout, seconds* (Settings, 60 by default) for the process to
   exit. If it is still there: `The server did not exit within 60 s; killing pid <n>.` and the
   process tree is killed, then up to 30 more seconds for the exit to be observed.
5. The liveness poll sees the exit and prints `Server exited (code -1).`; the state becomes
   **Stopped** and the saved PID is cleared. The toast `Stopped <name>` arrives now, so a stop reports a
   verified exit, never a sent command.

An RCON failure during the countdown or at `doexit` is logged (`RCON 'doexit' failed (Connect): ...
Continuing with the next step.`) and the job falls through to the timeout and the kill. A process the
manager has no RCON credentials for (re-attached without a generated `GameUserSettings.ini`) skips
`doexit` outright: `No RCON credentials for this process; skipping doexit and waiting for the graceful
timeout before killing.`

RCON is the only stop path the game offers. It saves and exits cleanly on `doexit` and has no other
remote signal, and killing the process loses whatever was not saved. That is why the manager verifies
the exit rather than trusting the command, why a start is refused without an admin password, and why
`doexit`'s exit code -1 is never treated as a crash. The design note asked for `saveworld` before
`doexit`; the code sends only `doexit`, after observing that the game saves twice on its own on the
way out.

## Restart

**Restart** is the stop job followed by a start. If the stop is refused or its exit cannot be
verified, the start is not attempted and the toast carries the stop's reason.

## Finding servers again after a service restart

Game processes are not killed when the service stops. That is on purpose: the servers stay up through
app upgrades and service restarts, and players do not notice. The price is that the service has to
find them again on its next start.

During the `Reconciling instance processes` step it lists every running `ArkAscendedServer.exe`
through WMI and, for each instance, looks for one whose executable lies under `DataRoot\Instances`
and whose command line carries exactly `AltSaveDirectoryName=<slug>`. The saved PID plus start time
wins when it matches; otherwise the slug token alone decides. Matching on that token rather than the
process name is the only option, since every instance is the same executable.

- Exactly one match: the instance is **Starting** again with the console line
  `re-attached — log history (pid <n>, started 2026-09-14 08:12:03)`, the last *Console history on
  re-attach, lines* (Settings, 200 by default) of `ShooterGame.log` shown as history, and the RCON
  probe promotes it to **Running** at the first answer. The generated INI files are not rewritten;
  the RCON password and port are read from the ones the process started with.
- No match: **Stopped**.
- More than one match: **Unknown**, with `2 running processes claim this instance (pids 4120,
  5316); stop the extra ones by hand and restart the service.` **Start** is disabled. The manager
  will not guess which process belongs to the slug.

An **Unreachable** re-attach (`Cannot probe RCON: ... Check ServerAdminPassword / RCONPort; the
process is still watched for exit.`) is still stoppable; the stop skips `doexit` as described above.

## The instance page

The header shows the name, the crumb (`Instances / <cluster>`), the state, map, ports, and slug, and
the buttons **Start** / **Stop**, **Restart**, **Back up now**, **Stop now**, and the delete icon. Under
it, a notice appears for **Starting, unconfirmed**, **Unreachable**, **Unknown**, or
**Identity not saved** (with **Retry persist**), and `Start would be refused.` whenever the launch
preview finds a problem while the instance is stopped.

| Tab | What it holds |
|---|---|
| **Console** | The `ShooterGame.log` tail and the RCON input. [console-and-rcon.md](console-and-rcon.md). |
| **Players** | `On the server now`: asks the server with `ListPlayers` each time the tab opens, **List players** to ask again. [players-and-whitelists.md](players-and-whitelists.md). |
| **Config** | Standalone: the `GameUserSettings.ini` and `Game.ini` editors. Member: `INI files come from the <cluster> cluster.` Both: the **Overrides** table with **Add override**. [configuration-files.md](configuration-files.md). |
| **Mods** | The ordered mod list: the map's mod and cluster mods locked, the instance's own below. [mods.md](mods.md). |
| **Launch** | The flags editor and `What a start would run`, the exact command line. [launch-options.md](launch-options.md). |
| **Settings** | **Instance name**, **Session name**, **Max players**, **Game port**, **RCON port**, **Backup interval, minutes**, **Backups to keep**, **Ignore the cluster's schedule** (members only), **Admin whitelist**; **Save settings**, **Reset**. While the process is live: `Port and player changes apply at the next start.` The slug and the map cannot change. |
| **Backups** | The backup list with outcome, archive name, size, and trigger, a restore icon on each successful row, and the list of restores. [backups.md](backups.md). |
| **Schedule** | The scheduled actions and their history ([below](#scheduled-actions)). |

## Scheduled actions

The **Schedule** tab runs actions on a schedule. A row is a schedule, an action, and an **Enabled**
box; **Save schedule** writes the whole list, and a row that is not enabled stays listed but never
runs.

- **Restart** counts down, then stops the server the normal way and starts it again.
- **RCON command** sends the command on the row, exactly as you would type it into the console;
  the command and the reply show there too. `SaveWorld` is the usual one.
- **Wipe wild dinos** counts down, then sends `DestroyWildDinos`.

A schedule is a standard cron expression: five fields for minute, hour, day of month, month, and
day of week, read in the server's local time. `0 4 * * *` is 04:00 every day, `30 3 * * 1-5` is
03:30 Monday to Friday, and `0 */6 * * *` is every six hours from midnight. The list shows each row
in plain words (`At 04:00`, `Every 6 hours`); hovering the words shows the expression and the next
time it runs. The pencil next to them opens the builder, which is also what **Add action** opens
first: pick **Every day at**, **Every N hours**, or **On these days** and fill in the boxes, or pick
**Custom expression** and type one. The builder shows the expression it will save, the same plain
words, and the next three times it would run. An expression it cannot read says so and **OK** stays
disabled until it can.

The time in the expression is the moment of the action itself. A restart or a wipe also has a
warning of 0 to 60 minutes (the **min warning** box after the action): the countdown starts that
many minutes early and players see a broadcast each minute until the action, so a restart at
`04:00` with a 10 minute warning starts talking at `03:50` and restarts at `04:00`. An RCON command
has no warning. A schedule that repeats faster than its own warning is refused. When the clocks go
forward, a time inside the missing hour runs right after the change (a `02:30` job runs at
`03:00`); when they go back, a fixed time in the repeated hour runs once, the first time it comes
round, while an expression that repeats within the hour runs in both.

A cluster member runs its cluster's rows as well as its own. They appear in the member's list
locked, with `Comes from the cluster; change it on the cluster page.` on hover. Tick
**Ignore the cluster's schedule** on the Settings tab and save it to run only the instance's own
rows; the locked rows disappear from the list ([clusters.md](clusters.md#the-cluster-page)).

One action runs on an instance at a time. The **History** box under the editor holds the last 200
runs across the rows, newest first, and scrolls once it is full: when the run was scheduled for,
when it started and ended, the action, the outcome, and a reason.

| Outcome | Meaning |
|---|---|
| **Done** | The action finished. |
| **In progress** | It is running now. |
| **Failed** | The server refused it or the command returned an error; the reason says which. |
| **Skipped** | The time passed without the action: `The server is not running.`, `An update is in progress.`, `Another action is in progress.`, or the instance was busy with an operation. |
| **Interrupted** | The service stopped while the action was in flight (`The service was restarted before the action finished.`). Nothing is retried; the next occurrence is normal. |

A skipped occurrence is recorded so a missed action is visible rather than silent. Runs older than
30 days are removed.

## Deleting an instance

The delete icon in the header opens `Delete <name>`. If the server is running the dialog says so:
`The server is running. It will be saved and stopped first; the delete waits for the process to exit.`
The stop runs under the lease the delete already holds, so a running instance can be deleted in one step.
There are two independent choices. First, the world data:

- **Keep the world data**: `The Saved folder moves to Archive under DataRoot with a timestamp. The
  slug stays reserved.`
- **Delete the world data**: `The Saved folder, including the world and player files, is removed.`

Then the checkbox **Delete the backup archives too**, which starts ticked: every zip under
`Backups\<slug>` is removed with the instance. Clear it and `The zips under Backups\<slug> are left
on disk. Their entries in the backup history go with the instance either way, so restoring one
afterwards means unzipping it by hand.` The button names the combination you picked, from **Delete
instance, keep world and backups** through to **Delete instance, world, and backups**.

Anything irreversible needs the instance name typed into the confirmation field first: that is any
delete that removes the world, the backups, or both. The only combination that does not ask is
keeping both.

The job then runs: a stop with verified exit (the console prints `Delete: stopping the instance with
verified exit.`), removal of the firewall rules, removal of the junctions, then `ShooterGame\Saved`
is moved to `Archive\<slug>-<yyyyMMdd-HHmmss>` or deleted, the rest of `Instances\<slug>` (including
a standalone instance's `Config\` source files) is removed, `Backups\<slug>` if you asked for it, and
finally the database rows: the instance, its mods, overrides, INI mirror rows, and backup records;
players last seen on it are marked offline. The last console line reads
`Instance '<slug>' deleted; its world data was archived to <path> and its backups were deleted.`,
with each half naming what you chose, and you land on the Instances page.

The backup history in the database always goes with the instance row. Keeping the zips keeps the
files, not the list of them.

## What each operation touches

| Operation | Database | Disk | RCON | Console |
|---|---|---|---|---|
| Start | `LastPid`, `LastProcessStartTime`, `LastLaunchedAt`, `State` on the instance row. | Generated INI, `.bak`, whitelist file, firewall rules, the process. | Probe `ListPlayers` every 15 s. | `Config:`, `Firewall:`, `Launched pid`. |
| Stop | `State`; PID cleared on exit. | Nothing. | `broadcast` per minute, `doexit`. | `RCON:` lines, replies, `Server exited`. |
| Re-attach | PID and start time re-saved. | Reads the generated `GameUserSettings.ini` for credentials. | Probe as for a start. | `re-attached — log history`, backfill lines. |
| Delete | Rows removed in one transaction. | Firewall rules, junctions, `Saved` archived or deleted, `Backups\<slug>` when the checkbox is ticked. | The stop's commands. | `Delete:` lines. |

The runtime state you see (**Starting**, **Running**, ...) lives in memory in the process manager and
is mirrored into the instance row best-effort; the sidebar's `N instances up` and every state
indicator update from the same events, without a page reload. A crash (an exit without a stop from the
manager) prints `Server exited unexpectedly (code <n>).` and the state returns to **Stopped**; nothing
restarts it.

## Refusals and failures

| Message | Meaning and what to do |
|---|---|
| `The service is not ready yet (<phase>: <message>).` | Startup has not reached Ready; watch `/setup`. |
| `update in progress` | A game update or install holds the maintenance gate, or the launch was queued when one began. Try after it finishes. |
| `operation in progress` | A stop, backup, delete, or another start of this instance is running. |
| `The instance is already running.` (or `starting`, `stopping`, ...) | There is already a live process. |
| `Instance <id> no longer exists.` | Deleted while the launch was queued. |
| `ServerAdminPassword under [ServerSettings] is missing or empty; RCON is the only stop path, so it is required.` | Set it in the INI editor (**Config** tab) or the cluster's `GameUserSettings.ini`. |
| `RCONPort under [ServerSettings] is missing or invalid (was '...').` | The generated file has no usable port; check the instance's **RCON port** on the Settings tab. |
| `Port conflict: Port 7777 is used by 'Island' (game port range 7777-7778).` | Another instance owns it. Change a port on the Settings tab. |
| `Port conflict: Port 7778 is in use by an OS listener (UDP).` | Something else on the box holds it, possibly a server the manager does not know about. |
| `Port conflict: Port 5000 is used by the web UI.` | The instance's ports collide with the manager's own listener. |
| `Process.Start failed: ...` | The executable under the junction is missing or unreadable. Check `Server\ShooterGame\Binaries\Win64` and the junctions. |
| `<path> exists and is not a junction; move it aside and retry.` | A real folder sits where a junction belongs under `Instances\<slug>`. |
| `The server started (pid <n>) but its identity could not be saved after 3 retries: ... Use 'Retry persist'.` | The server is up; the database write failed. **Retry persist** on the instance page. |
| `Launch cancelled before it started.` / `The service is shutting down.` | The queued launch was abandoned; try again. |
| `The instance is not running.` | Stop asked for an instance without a live process. |
| `Pid <n> is still alive after kill; its exit could not be verified.` | The kill did not take within 30 s. Look at the process in Task Manager; the state stays **Stopping** with this detail. |
| `No countdown to skip` (`The stop is already past the broadcast phase.`) | **Stop now** was clicked in the moment after the countdown ended, before the button turned into **Stopping…**. |
| `The instance is not waiting for its identity to be persisted.` | **Retry persist** on an instance that is not in **Identity not saved**. |
| `An operation is in progress for this instance; try again when it finishes.` | Delete asked while the instance lock is held. |
| `The service is stopping; the delete was not started.` | Delete asked after the service began shutting down; nothing was touched. Retry once it is back. |
| `The instance could not be stopped with a verified exit: ...` | Delete aborted before touching anything; the reason is the stop's. |
| `Delete failed: ...` | A file operation failed after the stop; the console has the line. Check `Instances\<slug>` by hand. |
| `Instance name is required.` / `Instance name must be 100 characters or fewer.` / `An instance named '<name>' already exists.` | Settings tab validation. Names are compared ignoring case. |
| `Session name is required; it is what players see in the server browser.` / `SessionName must not contain '?'.` (or `'='`, `line breaks`) | The session name is written into the INI verbatim, so those characters are refused. |
| `Max players must be between 1 and 500.` | |
| `Backup interval must be between 1 and 10080 minutes, or left blank to use the default.` / `Backups to keep must be between 1 and 1000, or left blank to use the default.` | |
