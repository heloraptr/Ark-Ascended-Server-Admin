<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# ArkServerAdmin – Design Decisions

Date: 2026-09-06. Outcome of the initial design interview. This is the source of truth for MVP scope;
change this file when a decision changes.

## Context

- Self-hosted ARK Survival Ascended (ASA) dedicated server manager for Windows.
- Runs **on the server box only**; the user remotes in to use it. No remote web access in MVP.
- The manager must stay open while instances run (it owns the output pipes and the backup scheduler).
- Primary user is the author, running effectively single-player across a cluster. Design still has to
  hold for a stranger running 8 instances, but support burden is not a goal.
- Reference projects surveyed: ASA Server Manager (WPF, SteamCMD anonymous, single install, per-profile
  `AltSaveDirectoryName`, mod modes, no INI editing, no stop/RCON) and ARK Server Creation Tool (WPF,
  DepotDownloader, one full install per instance, port step 10, INIs opened in Notepad, hard-kill stop,
  firewall rule per exe). We take ideas, not code.
- Existing asset to keep: `ArkServerAdmin.Core` CurseForge API models/interfaces and the concrete
  `CurseForgeApi` client (currently in the Blazor Server `App` project, to be moved).

## 1. Platform and UI

- **WPF host window + BlazorWebView.** Official Microsoft package, no MAUI workload. WebView2 only paints
  HTML; all Razor and C# run in-process with full file system / Process / WMI / firewall access.
- All UI lives in a **Razor Class Library** (Radzen components, already in use) so a Blazor Server host
  could be added later without rewriting screens.
- Windows-only, `net9.0-windows`. App manifest requests **administrator** (firewall rules).
- The existing Blazor Server `ArkServerAdmin.App` project is removed once `CurseForgeApi` is relocated.

Proposed projects:

| Project | Purpose |
|---|---|
| `ArkServerAdmin.Core` | Domain models, INI merge, port allocation, launch-argument builder, CurseForge client + models, service interfaces. No Windows dependencies. |
| `ArkServerAdmin.Infrastructure` | EF Core + SQLite, SteamCMD runner, process manager, RCON client, junction/folder layout, firewall, backups, DPAPI. |
| `ArkServerAdmin.UI` | Razor Class Library: pages, wizard, console panel, INI editor. |
| `ArkServerAdmin.Desktop` | WPF shell hosting BlazorWebView, DI composition, elevation manifest. |
| `ArkServerAdmin.Tests` | xunit: INI merge, port allocation, argument builder, CurseForge client against recorded responses. No UI tests. |

## 2. Process ownership and console

- Instances are child processes that **survive the manager**. Each launch persists the PID.
- Re-attach on startup: check saved PID; if gone, search running processes by **command line**
  (WMI `Win32_Process.CommandLine`) matching the instance's `AltSaveDirectoryName`; else mark stopped.
  Process-name matching is useless because every instance is the same exe.
- Launch **with the visible Unreal console window** (`-log`) so the user can still remote in and use it.
- Additionally try to capture stdout (`-stdout -FullStdOutLogOutput`); fall back to tailing the
  instance's `Saved/Logs/ShooterGame.log`. **Spike A decides.**
- The manager shows a per-instance console panel: captured output on top, RCON command line below.
  Per-instance by construction; a new launch starts with an empty panel.
- Status derived from output: starting, world loaded, advertising, running; plus stopped/crashed.
- Manager crash while instances run: output is lost until restart; re-attached instance shows file tail.

## 3. Game install (SteamCMD)

- **SteamCMD**, app id 2430930, downloaded and extracted by the manager on first run.
- **Anonymous by default.** Optional Steam account: settings store the **username only**. A
  "Log in to Steam" button runs SteamCMD in a visible console once; the user types password and Steam
  Guard code there; SteamCMD caches a refresh token so later `+login <user>` runs headless.
  No password stored, no encryption needed for it.
- Automatic **retry with backoff** on failed updates (anonymous throttling was the real-world pain).
- `validate` toggle in settings.
- Update flow: broadcast countdown, graceful stop of running instances, SteamCMD, staggered restart
  of the ones that were running. Blocked while instances run unless confirmed.

## 4. Single install, per-instance trees (junctions)

`AltSaveDirectoryName` only relocates saves; `Saved/Config` would otherwise be shared. Layout:

```
<DataRoot>\Server\                                  real install (SteamCMD target)
<DataRoot>\Instances\<slug>\ShooterGame\Binaries    junction -> Server\ShooterGame\Binaries
<DataRoot>\Instances\<slug>\ShooterGame\Content     junction -> Server\ShooterGame\Content
<DataRoot>\Instances\<slug>\Engine                  junction -> Server\Engine
<DataRoot>\Instances\<slug>\ShooterGame\Saved\      real: Config, Logs, SavedArks
<DataRoot>\Clusters\<slug>\                          cluster dir (-ClusterDirOverride) + cluster INI text
<DataRoot>\Backups\<instance>\<timestamp>.zip
```

- Instance is launched through its junction path so Unreal sees a private `Saved`.
- `AltSaveDirectoryName=<slug>` is still passed (re-attach key). **Spike B** proves Unreal resolves
  paths through junctions and whether other top-level folders need junctioning too.

## 5. INI model

- **Raw text editor** in-app (no typed forms). Files: `Game.ini`, `GameUserSettings.ini`.
- **Clustered instance:** cluster owns the raw INI text. Before every launch the manager takes the
  cluster text, applies the instance's typed fields, and writes the generated files to the instance's
  `Saved/Config/WindowsServer`. Hand edits on disk are overwritten at next launch; a backup copy of the
  previous file is kept before overwrite.
- **Standalone instance:** same pipeline, the instance holds the raw text. Creation offers
  "copy from instance X" or "game defaults".
- **Instance-level keys** (manager owned, written into the generated INI or command line):
  SessionName, Port, RCONPort, MaxPlayers, mods, map. Everything else (passwords, transfer rules, rates)
  is cluster/standalone INI text.
- Instance also has an **extra overrides** list (`section`, `key`, `value`) for rare per-map differences.
  Modeled now, minimal UI.
- INI text is stored **both on disk and in SQLite**; saving from the UI writes both; "restore from
  database" rewrites the files. Changes made outside the manager are not mirrored.
- The server rewrites GameUserSettings.ini on shutdown; those additions are discarded. Accepted.

## 6. Launch flags

Three kinds of command-line content, kept distinct in the model:

1. `?Key=Value` after the map = `[ServerSettings]` INI keys. These live in INI text; the manager does
   not emit them (except the instance-level keys above where convenient).
2. True `-Flag` options with no INI form become typed UI fields: NoBattlEye, ServerPlatform (crossplay),
   ExclusiveJoin, NoWildBabies, UseStore, ConvertToStore, ServerGameLog family,
   ActiveEvent, plus free-text **additional arguments**.
3. Manager-owned: map, `-port`, RCON port, `-mods`, `-clusterid`, `-ClusterDirOverride`,
   `AltSaveDirectoryName`, `-WinLiveMaxPlayers`, `-log -servergamelog`, stdout flags.

Launch flags follow the **cluster base + instance override** model like the INI. Passive mods dropped.

## 7. Ports

- Game port starts 7777, **step 2** (ASA uses port and port+1). RCON starts 27020, **step 1**.
- Starting values and steps editable in settings. Wizard pre-fills next free ports.
- Start is refused when ports collide with another defined instance.

## 8. Mods (CurseForge)

- Global **mod library** built from CurseForge search (thumbnails from the API), manual ID entry when no
  key is configured (name = id, no thumbnail).
- **Cluster mods** are mandatory for every member and locked/greyed in the instance list; instances add
  their own on top. Emitted order: cluster mods (cluster order) then instance mods (instance order).
- Standalone: copy-from-instance at creation.
- "Updated on CurseForge since last start" hint from `dateModified`. Manager never downloads mods.
- API key stored in SQLite, column protected with Windows DPAPI (current user).

## 9. Storage

- **SQLite via EF Core.** Tables: app settings, clusters, instances, maps, mod library, cluster/instance
  mod assignments (ordered), INI text, extra overrides, known players, backup records.
- All paths stored **relative to a single configurable DataRoot** so the DB restores on another box.
- Backup story for config = copy the SQLite file; "restore from database" recreates folders, junctions
  and INI files. Game files are re-downloaded; world backups are separate (section 11).

## 10. Clusters and lifecycle

- Multiple clusters on one box are supported; instance list groups by cluster with standalone at the end.
- Cluster folder `Clusters/<slug>` passed automatically as `-ClusterDirOverride`.
- **Start cluster / start all:** fixed configurable stagger delay between launches (RAM surge).
- **Stop:** RCON `saveworld`, wait, RCON `doexit`, wait up to **60 s** (configurable), then kill with a
  visible warning. Restart = stop + start.
- **Pre-stop broadcast:** RCON `broadcast` countdown (configurable minutes) with a "stop now" option.
- **Firewall:** manager creates inbound rules at first start (elevated). Rule scoping decided by Spike B
  (program rule through junction path vs. per-instance port rules).

## 11. World backups (MVP)

- Per instance: RCON `saveworld`, then zip `Saved/SavedArks` (+ cluster folder) to
  `Backups/<instance>/<timestamp>.zip`.
- Manual button + in-manager interval scheduler that **runs only while the instance is running**.
- Per-instance interval and retention count configurable; manual backups count toward retention.
- No restore UI in MVP.

## 12. Admin whitelist (AllowedCheaterAccountIDs)

- Editable per cluster and per instance (union written to the instance file).
- ASA wants **EOS IDs**. Manager keeps a **known players** table populated from log/RCON `ListPlayers`
  (name + EOS ID); the editor lets you pick by name or paste an ID.

## 13. Maps

- `Maps` table: id, key (e.g. `TheIsland_WP`), friendly name, IsOfficial. Seeded on first run, editable.
- Map picker shows Official, divider, Custom. Adding a new official map is a row, not a redeploy.

## 14. Instance wizard

Pages: 1 name, 2 cluster or standalone (everything after depends on it), 3 map, 4 config source
(cluster / copy from / defaults), 5 mods, 6 launch flags, 7 ports (pre-filled), summary.

## 15. Out of MVP

Scheduled restarts, crash auto-restart, Discord webhooks, player list UI, app self-update, multihome IP
binding, typed INI forms, backup restore, passive mods, remote web access.

## 16. Spikes (first tasks, before any UI)

- **Spike A – output capture:** launch `ArkAscendedServer.exe` with `-log -stdout -FullStdOutLogOutput`
  and redirected stdout; confirm whether bytes arrive and the console window still appears. Fallback:
  log-file tail. Determines the console panel implementation.
- **Spike B – junction launch:** build the junction tree, launch through it, confirm `Saved` is private
  and the game loads; check firewall rule matching through the junction.
- Both use a hard-coded instance after a real SteamCMD download.

## 17. Open items

- Exact EOS-ID source for the whitelist (log line format / `ListPlayers` output): confirm in Spike A.
- Whether `-WinLiveMaxPlayers` vs INI `MaxPlayers` is authoritative in current ASA builds.
- Radzen theme / look; tray icon behaviour on close while instances run.
