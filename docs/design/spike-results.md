<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Phase 2 spike results

_Run 2026-09-06 from a Windows PowerShell Claude session. Everything lives in `<SpikeDir>`
(throwaway). Tool source: `<SpikeDir>\tools\ArkSpike`, raw logs: `<SpikeDir>\results`._

## Environment

| Item | Value |
| --- | --- |
| OS | Windows 11 Pro 10.0.26200, 64 GB RAM, 6 cores |
| ASA dedicated server | Steam app 2430930, build 25117056, "ARK Version: 93.19", Unreal 5.5.4 |
| Install | `<SpikeDir>\Server` via SteamCMD anonymous, 12.2 GB, ~19 min download |
| Launch args | `TheIsland_WP?listen -log -stdout -FullStdOutLogOutput -servergamelog -NoBattlEye -WinLiveMaxPlayers=4` |
| RCON | CoreRCON 5.4.2, credentials only in `GameUserSettings.ini` `[ServerSettings]` |
| Session | Console-mode runs (`A1`, `A1b`, `B`, `B2`) from an interactive session (session 2), non-admin; `A2` from a LocalSystem Windows service in session 0. |

Sessions: `A1` (shared install, real path), `A1b` (relaunch on the same path, log rotation),
`B` (junction instance `alpha`, INI port), `B2` (junction instance, `-port=` on command line).
`B` ran concurrently with `A1`/`A1b` off the same install.

## Spike A — output capture, log behavior, RCON, restart survival

### (a) stdout

Yes. With `-stdout -FullStdOutLogOutput` every `ShooterGame.log` line is mirrored to stdout as
it is written; stderr carries GameAnalytics and breakpad chatter that is *not* in the log. Volume
is tiny while idle (~250 lines / 10 KB over 2.5 min). `<stdout EOF>` arrives ~0.7 s after the
last log line on exit. Redirecting stdout through a pipe did not change game behavior.

### (b) `Saved\Logs\ShooterGame.log`

- Created fresh ~1.8 s after process start. First bytes are a UTF-8 BOM, first line
  `[ts][  0]Log file open, 09/06/26 21:38:50`; last line on clean exit is
  `[ts][frame]Log file closed, ...`.
- **Relaunch rotates:** the previous file is renamed to
  `ShooterGame-backup-<yyyy.MM.dd-HH.mm.ss>.log` (timestamp = its last write) ~1.2 s after
  process start and a new `ShooterGame.log` is created. No size-based rotation was observed.
- **NTFS tunneling trap:** the new file **inherits the old creation time** (same name, same
  directory, within seconds). Rotation must be detected by *length shrinking below the read
  offset* or by NTFS file ID, never by creation time. The spike tail caught it via length.
- Other files in `Logs\`: `ServerGame.<pid>.<yyyy.MM.dd_HH.mm.ss>.log` (from
  `-servergamelog`, one per launch, 400–600 B idle) and `FailedWaterDinoSpawns.log`.

### (c) Event-to-file latency

Tail polled every 100 ms, comparing identical lines from stdout vs. the file:

| Session | Matched lines | Avg | p90 | Max |
| --- | --- | --- | --- | --- |
| A1 | 162 | 201 ms | 298 ms | 300 ms |
| B | 157 | 202 ms | — | 287 ms |
| A1b | 161 | 168 ms | — | 308 ms |

The game flushes each line immediately; the latency is dominated by the poll interval plus the
launcher's own stdout read. **Log tail is viable and is the primary `IOutputSource` (step 14).**

### (d) EOS id line / `ListPlayers` shape

Not exercised — no game client available. `ListPlayers` with nobody connected returns the single
line `No Players Connected`. The populated format (`0. <name>, <eos id>`) is **unverified**; keep
the Known Players refresh tolerant and verify with a real join during Phase 5.

### (e) CoreRCON against ASA

Works: auth, `ListPlayers`, `broadcast`, `saveworld`, `doexit`. Replies:

| Command | Reply | Time after full startup |
| --- | --- | --- |
| `ListPlayers` | `No Players Connected` | 20 ms |
| `broadcast x` | `Server received, But no response!!` | 27 ms |
| `saveworld` | `World Saved` | ~400 ms |
| `doexit` | `Exiting...` | 20 ms |
| `GetGameLog` | `Server received, But no response!!` | — |

Quirks the product must handle:

- **RCON is up long before the world is.** Port 27020 listens ~5 s after launch. Until the
  `advertising for join` line, auth took 5.5–6.9 s and each command 2–5 s; afterward everything
  is <100 ms. The 15 s Starting probe must use the full RCON timeout and treat slowness as
  "still starting", not failure.
- CoreRCON `Dispose()` throws `SocketException` (10057) if the socket never connected. Wrap it.
- The server keeps the client's socket in `CLOSE_WAIT` briefly after dispose; harmless.
- Multi-packet responses were not exercised (no reply exceeded one packet).

### (f) `saveworld` vs. the `.ark` on disk

**The reply comes before the write.** Timeline from A1 (file already existed):

```
23.535  RCON reply "World Saved"      (.ark unchanged, mtime 21:40:05)
23.522  log: "World Save Complete. Took: 0.361756"   <- also before the write
24.306  .ark grows, FileShare.Read open FAILS (sharing violation)
24.430  still growing / locked
24.647  .ark final size, opens with FileShare.Read again
```

- Reply-to-settled: ~1.1 s. The log's "World Save Complete" line is not a completion signal for
  the file either.
- The sharing-violation check in step 28 works exactly as designed: the writer holds the file
  for ~350 ms; before and after it opens with `FileShare.Read`.
- **Before the first save the stub `.ark` (40 KB) is held open the whole time**, so a backup
  attempted before the first save always hits "world file in use". Acceptable; document it.
- Every save also writes `<Map>_<dd.MM.yyyy_HH.mm.ss>.arkrbf` (a full-size rollback copy per
  save; the game keeps a rolling set of about four and deletes the oldest, as seen in `A2`) and
  `<Map>_AntiCorruptionBackup.bak`. Backups should pick `<Map>.ark` + profiles/tribes
  explicitly rather than "everything in the directory", or each archive carries ~5 world-size
  copies, and the rolling deletion will trip the "file removed between inventories" check.
- Quiescence rule for step 28: after the reply, wait until the `.ark` opens with
  `FileShare.Read` **and** its length + mtime have been stable for ≥1 s, bounded by the window.

### Startup and shutdown markers

| Marker (log/stdout) | When (A1) | Meaning |
| --- | --- | --- |
| `Server: "<SessionName>" has successfully started!` | +5 s | too early — world not loaded; RCON port opens around here |
| `Full Startup: 20.91 seconds` | +21 s | engine init done |
| `Server has completed startup and is now advertising for join. (10.10GB Mem)` | +93 s | **the Advertising marker**; RCON becomes fast |
| `Log file closed, ...` | exit | clean shutdown |

`doexit`: saves the world, exits **26 s** later with **exit code -1 (0xFFFFFFFF)** every time.
Non-zero exit is the *normal* exit — crash detection must not key on exit code; use
"we sent doexit" + `Log file closed` instead.

### INI behavior

- The game **rewrites `GameUserSettings.ini` at launch** (180 B → 11 KB, all defaults
  materialized) and again at exit, and creates `Engine.ini`. Confirms step 16: generated files
  are never read back as source.
- RCON credentials in `[ServerSettings]` (`ServerAdminPassword`, `RCONEnabled`, `RCONPort`)
  **are honored** from the INI (step 23 stands).
- **`Port=` under `[SessionSettings]` is ignored**: instance `B` with `Port=7787` in the INI bound
  UDP 7777. `-port=7787` on the command line (`B2`) bound 7787. The launch-argument builder must
  put the game port on the command line; keep `Port` reserved so a user INI cannot mislead.
- **Port collision is silent.** `A1b` launched while `B` held 7777: it bound only 7778, logged
  nothing, and ran "healthy" with no game port. Step 15's collision refusal is load-bearing.
- Every instance also binds **UDP 27015** (Steam query default). Two instances did so at the same
  time without complaint; whether the second one is functional is unknown. Open question: should
  the allocator also hand out `-QueryPort`?

### Parent death / stdout pipe closure (console-mode stand-in for the service restart)

Run `B2`: the launcher (the process holding the game's redirected stdout/stderr pipes) was
killed with `Stop-Process -Force` once the game reached the Advertising marker.

- The game **survived** the parent's death and the pipe closure: alive at every 5 s check for
  the next 100 s, memory steady, no error lines.
- RCON kept working from a fresh client afterward (`ListPlayers` 27 ms, `broadcast`,
  `saveworld` 421 ms).
- `ShooterGame.log` kept receiving output after the pipes were gone (`Saving world...`,
  `World Save Complete`, `Closing by request`, `Log file closed`; file grew 9,863 → 12,064 B),
  and a fresh tail opened at the old offset followed it.
- `doexit` still worked and the process exited cleanly (~29 s, exit code -1 as always).
- Note: an idle server writes **nothing** to the log between the Advertising line and the next
  event (70 s of silence here). "No output" is not a health signal; liveness must come from
  `HasExited` and the RCON probe, as step 22 already says.

This is the interactive-session result; the LocalSystem / session-0 confirmation is the pending
service run below.

### Service (session 0) run — 2026-09-07

Session `A2`: the spike tool hosted as a Windows service (`ArkSpikeSvc`, LocalSystem, manual
start, `Host.CreateApplicationBuilder` + `AddWindowsService`), launching the junction instance
with `-port=7787` and stdout/stderr redirected. Worker log confirmed
`user=<MACHINE>$ session0=True interactive=False`.

- **Launch under SYSTEM in session 0 works** exactly like the interactive runs: log created and
  rotated the same way, stdout mirrored (243 lines / 9.7 KB at 1 min), same markers, same
  timings (`Full Startup` 52 s, Advertising at +66 s), same latency (avg 200 ms, max 276 ms).
  The game process shows `Services` / session 0 in `tasklist`.
- **Service stop does not kill the game.** `Stop-Service -Force` → service `Stopped` in 0.5 s;
  the game was alive at +5 s and +15 s, memory steady, and stayed alive until `doexit` 80 s
  later. No job object, no `Kill` on dispose — the detached-child model in step 21 holds.
- **The closed stdout/stderr pipes do not hurt the game.** No error, no hang, RCON kept
  answering in <100 ms.
- **Restart + attach works.** The restarted service found `A2-pid.txt`, matched the pid
  **and** `Process.StartTime` to the millisecond, and went into attach mode (tail only). Its
  tail then saw the next `Saving world...` / `World Save Complete` lines, so a fresh tail after
  a service restart follows the live log. A second concurrent reader (interactive `tail`) saw
  the same lines; multiple `FileShare.ReadWrite` readers are fine.
- `saveworld` behaved identically under SYSTEM: reply at 17.745, `.ark` locked and growing
  18.301–18.412, settled 18.417 (~0.7 s after reply), rollback `.arkrbf` written alongside.
- **Crash/exit detection by polling works for an attached process:** after `doexit` the
  attached loop saw `HasExited=true` within 2 s of the exit (`attach loop ending: exited=True`).
- RCON from a non-admin interactive process to the SYSTEM-owned game is unrestricted (TCP
  loopback), as expected.
- Cleanup: `sc delete ArkSpikeSvc` succeeded; no game, launcher, or SteamCMD processes left.

**Session-0 behavior is confirmed as what ships**: launch detached from the service, persist
pid + start time, re-attach by identity, tail the log, probe RCON, poll `HasExited`.

## Spike B — junction launch

Layout built by `make-junctions.ps1` (plain `cmd /c mklink /J`, no elevation):

```
Instances\alpha\
  Engine                 -> Server\Engine                 (junction)
  ShooterGame\Binaries   -> Server\ShooterGame\Binaries   (junction)
  ShooterGame\Content    -> Server\ShooterGame\Content    (junction)
  ShooterGame\Plugins    -> Server\ShooterGame\Plugins    (junction)
  ShooterGame\Saved                                       (real)
```

- **Nothing else needs junctioning.** Not linked and not missed: `steamapps\`, the three
  `Manifest_*_Win64.txt`, and the top-level Steam DLLs (`steamclient64.dll`, `tier0_s64.dll`,
  `vstdlib_s64.dll`, `steamwebrtc64.dll` — copies also live in `Binaries\Win64`).
- `ShooterGame\Plugins` **must** be linked (sentry crash handler and mod loader live there).
- The game creates `ShooterGame\.sentry-native\` and `Saved\Cache\HttpFiles\<slug>\` under the
  **instance** path — private, fine.
- **`Saved` is private.** The shared install's `Saved` was untouched by the junction instance
  and vice versa; two instances ran concurrently off one install.
- **`AltSaveDirectoryName=alpha` changes the save folder to `Saved\alpha\<Map>\<Map>.ark`**, not
  `Saved\SavedArks\...`. Step 28's inventory must enumerate `Saved\<slug>\` (plus the cluster
  dir), and the `<MapKey>.ark` check looks inside `Saved\<slug>\<MapKey>\`.
- **WMI reports the junction path.** `Win32_Process.ExecutablePath`, `CommandLine`, and
  `Process.MainModule.FileName` all return
  `<SpikeDir>\Instances\alpha\ShooterGame\Binaries\Win64\ArkAscendedServer.exe`. Step 21's
  identity check compares against the junction path (an "under `DataRoot\Instances`" prefix test
  is sufficient); no real-path resolution needed.
- `CreationDate` from WMI vs. `Process.StartTime`: identical to the millisecond
  (`21:41:22.4286640` vs `.4286646`), so the 2 s tolerance in step 21 is generous.

## SteamCMD notes (step 20)

- First ever run self-updates, prints `Update complete, launching...`, and **exits with code 7
  without running the requested commands**. The second run works. Treat exit 7 on a fresh
  `SteamCMD\` as "run again now", not as a backoff-worthy failure.
- With stdout redirected, progress lines are parseable:
  `Update state (0x61) downloading, progress: 15.67 (1912380961 / 12206318952)`; success is
  `Success! App '2430930' fully installed.`; `appmanifest_2430930.acf` shows `StateFlags 4`.
- `+force_install_dir` must precede `+login`. Anonymous login worked without throttling today.

## Plan impacts (summary)

| Step | Change |
| --- | --- |
| 14 | Log tail confirmed as the single `IOutputSource`; no stdout fallback needed. |
| 15 | Keep collision refusal; consider allocating a query port too. |
| 16/17 | Game port goes on the command line (`-port=`); INI `Port` is ignored by the game. |
| 21 | Compare `ExecutablePath` against the junction path; exit code -1 is a normal exit. |
| 22 | Rotation = length drop / file ID, not creation time; Advertising marker = "now advertising for join"; RCON is slow (2–7 s) until then. |
| 23 | INI-sourced RCON credentials confirmed. |
| 28 | Inventory `Saved\<slug>\<Map>\`; select `.ark` + profiles rather than the whole directory (`.arkrbf` pile); quiescence = openable + stable ≥1 s after reply. |
| 20 | Exit 7 on first run = retry immediately; parse progress lines for the console. |

## Spike B1 — ban list and exclusive-join files (2026-09-19, live box, instance `<instance>`)

Owner sent the RCON commands on the running server; the file was read from the box after each step.

| Step | Result |
|---|---|
| `BanPlayer <own eos id>` | The file appears at `<DataRoot>\Server\ShooterGame\Binaries\Win64\BanList.txt`. Nothing under `Saved\`. Through the instance junction it is the same file (`Instances\<slug>\ShooterGame\Binaries\Win64\BanList.txt`), so **every instance on the box shares one ban list**. |
| File shape | One line per ban, CRLF: `<id>,<id>,0`. Both fields were the 32-character EOS id (likely `<eos id>,<platform id>,<expiry>` with the platform id falling back to the EOS id; `0` = permanent). 69 bytes per line. |
| Second `BanPlayer` (id with the last two characters changed) | A second line; file 138 bytes. |
| File handle | An exclusive open from PowerShell succeeded while the server ran: **the game does not keep the file open**. |
| `UnbanPlayer <second id>` | File rewritten to one line (the second line removed). |
| Hand-added line while running, then `BanPlayer <second id>` again | **The hand-added line was dropped**: the game rewrites the whole file from its in-memory list on every ban/unban and does not re-read the file first. A manager edit made while the server runs survives only until the next RCON ban/unban, and almost certainly is not consulted for join checks either (not tested by joining). |
| `-exclusivejoin` read location | Not tested yet. |
| Two instances banning at once | Not tested yet; moot for correctness since the file is shared and each process rewrites it from its own memory: **the last writer wins and the other server's bans are lost from disk** until that server writes again. |

**Plan impact (B5/B6):** the live RCON path is the only way to change a running server's bans; the file is a per-box artifact the manager may read (for display) and may write only while no instance is running (for the manager-wide list at start). With several instances up, the on-disk file cannot be trusted as the union of bans; B5 should keep its own ban records and apply them by RCON to each running instance, and write the file only at start (before launch) as the seed, the same way `AllowedCheaterAccountIDs.txt` is written. Issue #8 (per-instance `Binaries\Win64` junction) would make the file per instance but would not remove the in-memory rewrite behavior.
