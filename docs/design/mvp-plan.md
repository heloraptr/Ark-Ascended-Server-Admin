<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: ArkAscendedServerAdmin MVP — Blazor Server host, Windows service

_Locked via grill — by Claude + the owner. Revised through 6 rounds of Codex review (APPROVED on round 6) (see
`PLAN-REVIEW-LOG.md`). Supersedes the platform/UI,
security, and process sections of `DESIGN.md` where they conflict; everything else in DESIGN.md
still applies._

_**Status 2026-09-08:** Phases 0–5 done. Phase 5 (UI) landed 2026-09-07/08: the Components RCL
(rail layout, dashboard, instance page with console/INI/overrides/mods/launch/settings/backups,
wizard, clusters, mods, players, maps, settings, setup), the command facades
`IInstanceCommands` / `IClusterCommands` / `IConfigCommands` / `IModCommands` /
`IPlayerCommands` / `IMapCommands` and the extended `IMaintenanceCommands`, and
`Core/Rcon/ListPlayersParser` (format unverified, see HANDOVER §5). Verified end to end on the VM
against the real ASA build: create → set password → start → console → RCON → backup → stop, and
cluster create → clustered instance → delete with archive. The design notes are in
`UI-DESIGN.md`._

_**Status 2026-09-12:** Phase 6 done. The Phase 3/4 suites already covered steps 33–34; Phase 6
added `Infrastructure.IntegrationTests/Commands/*` (91 tests over every Server command facade on a
real SQLite database with fakes for processes, RCON, CurseForge, and backups; see HANDOVER "What
Phase 6 built"). Totals: 451 unit, 219 integration, all green. One bug surfaced and was fixed:
`PlayerCommands.RefreshAsync` matched known players by EOS id case-sensitively in SQL and would
have inserted a duplicate row for an id reported in different casing. Still open: the deferred
`ListPlayers` capture (HANDOVER §5). Committed as `74d05f5`. A new session starts with
`HANDOVER.md`, then this plan._

## Goal

Build a self-hosted ARK Survival Ascended dedicated-server manager that runs as a **Windows service on
the game box** and is operated entirely through a **Blazor Server web UI** from another machine on the
LAN (via the owner's existing Nginx Proxy Manager for HTTPS), so the owner never has to
remote-desktop in. It manages a single SteamCMD game install shared by many instances via junction
trees, generates INI/launch arguments from a cluster-base + instance-override model, starts/stops
instances with graceful RCON shutdown, streams each instance's console to the browser, and takes
interval world backups. All UI lives in a Razor Class Library so a WPF standalone host can be added
later without rewriting screens; that host is explicitly not part of this plan.

## Approach

### Phase 0 — Solution restructure (no behavior yet)

1. Retarget everything to **.NET 10**. TFMs: `Core`, `UI`, `UnitTests` = `net10.0`;
   `Infrastructure`, `Server`, `Infrastructure.IntegrationTests` = `net10.0-windows` (a `net10.0` project cannot
   reference a `net10.0-windows` one, so every project that references `Infrastructure` is
   `-windows`).
2. Create projects: `ArkAscendedServerAdmin.Infrastructure`, `ArkAscendedServerAdmin.Components` (RCL, Radzen),
   `ArkAscendedServerAdmin.Server` (Blazor Server host, renamed from `ArkAscendedServerAdmin.App`),
   `ArkAscendedServerAdmin.UnitTests` (xunit + NSubstitute, pure `Core`),
   `ArkAscendedServerAdmin.Infrastructure.IntegrationTests` (xunit, real filesystem, references `Infrastructure`).
3. Move `CurseForgeApi` from `App/Services` into `Core/CurseForge` next to `ICurseForgeApi`.
4. Delete the legacy WPF project `src/ArkAscendedServerAdmin` (net9 WPF with `DataContext`,
   `ServerModService`, `Settings.xaml`). Nothing in it is reused; the future `Desktop` project starts
   fresh. Remove the `Counter`/`Weather`/`Sandbox` template pages.
5. Dependency direction: `Server` → `Components`, `Infrastructure`; `Components` → `Core`; `Infrastructure` → `Core`;
   `Core` → nothing. `Components` talks only to `Core` interfaces.

### Phase 1 — Host, configuration, storage, auth

6. `Server` uses `WebApplication.CreateBuilder` + `builder.Services.AddWindowsService()`
   (`Microsoft.Extensions.Hosting.WindowsServices`) with `ContentRootPath = AppContext.BaseDirectory`
   so the service does not resolve `appsettings.json` relative to `System32`. Installed with
   `sc create` / `New-Service` under **LocalSystem**.
7. **Terminology (binding for this plan and for review):**
   - **`appsettings.json`** = on-disk .NET config, read at startup, change requires service restart.
     Holds: `DataRoot` (default `%ProgramData%\ArkAscendedServerAdmin`), Kestrel bind address + port (default
     `127.0.0.1:5000`), `KnownProxies` (the Nginx Proxy Manager address), login `Password`, logging.
   - **App Settings (database)** = SQLite table edited from the web UI at runtime. Holds: stagger
     delay (30 s), SteamCMD `validate` toggle (off), game port start/step (7777/2), RCON port
     start/step (27020/1), default backup interval (30 min) and retention (10), backup quiescence
     window (10 s), pre-stop broadcast minutes, graceful-stop timeout (60 s), RCON command timeout
     (10 s), console backfill line count (200), CurseForge API key (**plain text**).
8. **Authentication/authorization.** ASP.NET Core cookie auth with a single password from
   `appsettings.json`: anonymous SSR `/login` form (antiforgery on), fixed 1 s delay per failed
   attempt and a 5-failure / 5-minute lockout keyed by client IP, 12 h sliding expiry,
   `SecurePolicy = Always` (cookie attribute only — it is not the HTTP guard), and a
   password-hash claim checked in `OnValidatePrincipal` on every cookie request. **HTTPS guard:**
   `UseForwardedHeaders` with `KnownProxies` from `appsettings.json` runs first; then a middleware
   returns 403 for any request whose effective `Request.IsHttps` is false, unless
   `appsettings.json` `AllowInsecureHttp=true` (development only, logged as a warning at startup).
   Real ingress protection is the loopback bind. Fallback authorization policy = authenticated user,
   so every page, the Blazor hub, and any endpoint require the cookie unless explicitly
   `[AllowAnonymous]`. **Live circuits:** a `RevalidatingServerAuthenticationStateProvider` with a
   5-minute interval re-checks the password-hash claim and cookie expiry; on failure the provider
   reports anonymous, `AuthorizeView` hides every action, and the layout calls
   `NavigateTo("/login", forceLoad: true)` which tears the circuit down — so a changed password
   invalidates cookies immediately and circuits within 5 minutes. **Visibility is not
   authorization:** the UI never calls the singleton managers directly; it calls scoped command
   facades (`IInstanceCommands`, `IMaintenanceCommands`, `IConfigCommands`, `ISettingsCommands`)
   whose every method first awaits `IAuthorizationGuard.EnsureAuthorizedAsync()` — a re-check of
   the current `AuthenticationState` plus the password-hash claim against the live password —
   before enqueueing any work, so a stale event handler that fires around invalidation is refused
   server-side. Cookie encryption requires Data
   Protection keys that survive service restarts: `PersistKeysToFileSystem(DataRoot\keys)` (this is
   for the auth cookie only; the API key stays plain text).
9. EF Core + SQLite in WAL mode. `DbContext` and migrations live in `Infrastructure`; `dotnet ef`
   runs with `--startup-project Server`. DB file lives under `DataRoot`. **All data access goes
   through `IDbContextFactory<T>`** — a context per operation, short-lived — because the process
   manager, backup timers, and Blazor circuits all touch the DB concurrently from singletons.
   "Export config backup" in Settings uses `SqliteConnection.BackupDatabase` to write a consistent
   copy; a raw file copy is documented as safe only with the service stopped.
10. Tables: AppSettings, Clusters, Instances (`Slug`, `LastPid`, `LastLaunchedAt`, `State`), Maps,
    ModLibrary, ClusterMods (ordered), InstanceMods (ordered), IniDocuments (cluster- or
    instance-owned mirror of `Game.ini` / `GameUserSettings.ini` source text), ExtraOverrides
    (section/key/value per instance), KnownPlayers (name, EOS id), BackupRecords,
    MaintenanceState (single row: phase `None|Installing|Stopping|Updating|Restarting`, plus a JSON
    list of `{instanceId, done}` for the instances to restart after update). Instances also store
    `LastProcessStartTime` (the actual `Process.StartTime`).
11. **Startup readiness pipeline** (a hosted `StartupOrchestrator` publishing `ReadinessState`):
    create `DataRoot` tree → migrate DB → seed (official ASA maps + every App Setting default,
    idempotent) → **reconcile instance processes first** (step 21) → reconcile any surviving
    `steamcmd.exe` from `DataRoot\SteamCMD` (wait for exit, then kill if it exceeds a bound) →
    **resume `MaintenanceState`** by phase: `Stopping` → re-run the stop sweep for still-alive
    listed instances; `Updating` → re-run SteamCMD; `Restarting` → enqueue listed instances not yet
    `done`; only when the list is fully resolved is the phase cleared → check
    `DataRoot\SteamCMD\steamcmd.exe` → check the install is **complete** via
    `Server\steamapps\appmanifest_2430930.acf` (`StateFlags` = 4), never a directory-exists check →
    if anything is missing, run the install in the background → `Ready`. **Readiness states are
    `Initializing → Recovering → Ready`** (or `Installing`). The recovery orchestrator launches
    through an **internal** `ILaunchQueue.EnqueueRecovery` path that is allowed once the install is
    verified and the maintenance gate is free; the **public** `Start` command stays refused until
    `Ready`, so recovery cannot deadlock on its own guard. **`Ready` does not wait for pending
    restarts to complete** — it is reached once the install is verified, processes are reconciled,
    and the pending restarts have been *handed to the queue*. Each recovery launch that fails
    (missing password, port conflict, `Process.Start` error) records a persisted per-entry
    `error` in `MaintenanceState` instead of blocking; the Dashboard shows an "update recovery
    incomplete" banner listing each failed instance with **Retry** (re-enqueue) and **Skip** (mark
    `done`) actions, and all configuration pages remain reachable so the owner can fix the cause.
    `MaintenanceState` returns to `None` only when every entry is `done`. A middleware redirects to
    `/setup` while not `Ready`, with an explicit
    allowlist so setup itself works: `/setup`, `/login`, `/_blazor`, `/_framework`, `/_content`,
    static files. `/setup` shows the live install console (authenticated) and redirects to `/` on
    `Ready`. The process manager also refuses Start while not `Ready`, so readiness is not enforced
    by routing alone.

### Phase 2 — Spikes (run from a Windows PowerShell Claude session, not WSL)

_**Done 2026-09-06/07.** Full results in `SPIKE-RESULTS.md`; the findings are folded into the
steps below as **Spike result** notes. One item is deferred: the populated `ListPlayers` /
EOS-id format (12d) needs a real client join, which happens once the app is running end to end —
the owner joins, the log and RCON output are read outside the app, and the Known Players parser
is confirmed then._

12. **Spike A — output capture, log behavior, RCON, restart survival.** Launch
    `ArkAscendedServer.exe` with `-log -stdout -FullStdOutLogOutput`, stdout redirected. Record:
    (a) do bytes arrive on stdout, (b) does `Saved\Logs\ShooterGame.log` reset per session or
    append/rotate, (c) event-to-file latency, (d) EOS id line format and `ListPlayers` RCON output
    shape, (e) CoreRCON works against ASA (auth, multi-packet `ListPlayers`), (f) what `saveworld`
    returns and whether the `.ark` file is fully written when it returns. Run it **from a test
    Windows service**, then **stop and restart that service while the game keeps running** and
    confirm: the game survives, the stdout pipe's closure does not affect it, and the log file keeps
    receiving output that a fresh tail can follow. Session-0 behavior is what ships.
    **Spike result (ASA build 25117056, v93.19):** (a) yes, stdout mirrors every log line
    (stderr carries GameAnalytics noise); (b) `ShooterGame.log` is created fresh per launch and
    the old one is renamed `ShooterGame-backup-<ts>.log` ~1 s after the next launch — the new
    file **inherits the old creation time** (NTFS tunneling), so rotation is detected by a
    length drop or file ID only; (c) event-to-file latency avg 200 ms / max 310 ms with a
    100 ms poll — the game flushes per line; (d) deferred, see above; (e) CoreRCON 5.4.2 works
    for auth, `ListPlayers`, `broadcast`, `saveworld`, `doexit` — RCON listens ~5 s after
    launch but auth takes 5–7 s and commands 2–5 s until the Advertising line, <100 ms after;
    `Dispose()` throws if the socket never connected; (f) `saveworld` replies `World Saved` and
    logs `World Save Complete` **~0.8 s before the `.ark` is rewritten**; the write holds the
    file for ~350 ms (sharing violation on `FileShare.Read`) and settles ~1.1 s after the reply.
    `doexit` saves, exits in ~26 s, and **always exits with code -1** (normal, not a crash). The
    game rewrites `GameUserSettings.ini` at launch and at exit and creates `Engine.ini`.
    **Service run confirmed:** under LocalSystem in session 0 the game survives a service stop,
    ignores the closed stdout pipes, keeps writing the log, RCON keeps working, a restarted
    service re-attaches by pid + `StartTime` and a fresh tail follows the log; `HasExited`
    polling catches the exit within 2 s. An idle server logs nothing for minutes, so silence is
    not a health signal.
13. **Spike B — junction launch.** Build `Instances\<slug>` with junctions for
    `ShooterGame\Binaries`, `ShooterGame\Content`, `Engine`; real `ShooterGame\Saved`. Launch through
    the junction path with `AltSaveDirectoryName=<slug>`; confirm `Saved` is private, world loads,
    whether any other top-level folder needs junctioning, and what WMI reports as `ExecutablePath`
    (junction path or real path) for the identity check in step 21.
    **Spike result:** junctions for `Engine`, `ShooterGame\Binaries`, `ShooterGame\Content`,
    **and `ShooterGame\Plugins`** (sentry crash handler + mod loader live there), real
    `ShooterGame\Saved`; nothing else needs linking (`steamapps`, `Manifest_*.txt`, top-level
    Steam DLLs are unused). The game creates `ShooterGame\.sentry-native` and
    `Saved\Cache\HttpFiles\<slug>` under the instance path. `Saved` is private; two instances
    ran concurrently off one install. **`AltSaveDirectoryName=<slug>` moves the world to
    `Saved\<slug>\<Map>\<Map>.ark`** (not `SavedArks`). WMI `ExecutablePath`, `CommandLine`, and
    `Process.MainModule` all report the **junction path**; `CreationDate` matches
    `Process.StartTime` to the millisecond. **`Port=` in `GameUserSettings.ini` is ignored**;
    `-port=<n>` on the command line is honored; `RCONPort`/`RCONEnabled`/`ServerAdminPassword`
    from the INI are honored. A port collision is **silent** — the second instance binds only
    port+1, logs nothing, and runs without a game port. Every instance also binds UDP 27015.
14. **`IOutputSource` selection rule — resolved: continuous log tail**, single implementation,
    identical before and after re-attach (Spike A: avg 200 ms latency). No stdout fallback and no
    mode indicator. The tail must detect rotation by length-below-offset or NTFS file ID, never
    by creation time, and must open with `FileShare.ReadWrite | FileShare.Delete`.

### Phase 3 — Core domain (pure, unit-tested)

15. Port allocator: next free game/RCON ports from App Settings start/step, excluding every defined
    instance and the Kestrel port; start refused on collision. **Spike result:** the collision
    refusal is load-bearing — the game does not report a failed bind, it runs "healthy" without a
    game port. Before launch, also check the OS (`GetActiveUdpListeners` / `GetActiveTcpListeners`)
    for the game port, port+1, and the RCON port, not just the DB.
16. INI pipeline. **Canonical source text lives at `Clusters\<slug>\Config\` (clustered) or
    `Instances\<slug>\Config\` (standalone)** — never under `Saved\`. UI save is **serialized per
    document** (one lock per source file) and uses **optimistic concurrency**: the editor holds the
    SHA-256 of the text it loaded, save is rejected with "changed since you opened it — reload" if
    the on-disk hash differs. The write is temp + atomic rename, then the mirror row in
    `IniDocuments` (text + hash) is updated; a mirror failure is surfaced as a UI warning with a
    "retry mirror" action that compares hashes before writing, the file write is authoritative.
    Launch reads source text and applies instance-level keys (SessionName, Port, RCONPort,
    `RCONEnabled=True`, MaxPlayers, admin whitelist union, extra overrides). **Spike result:** the
    game honors `RCONPort`/`RCONEnabled`/`ServerAdminPassword` from the INI but **ignores
    `Port`**; the INI `Port` is still written (for consistency) but the effective game port is
    the `-port=` argument from step 17. **Manager-owned keys
    are authoritative in the final effective configuration:** one shared reserved-key list (step 17)
    is enforced across every channel — reserved keys in source INI text are replaced by the
    generated values (with an editor warning naming them), reserved keys in `ExtraOverrides` are
    rejected at save time, and typed values (SessionName, map key, ports) are validated to contain
    no `?`, `=`, or line breaks. The pipeline writes the **generated** files to
    `Instances\<slug>\ShooterGame\Saved\Config\WindowsServer` (temp + rename, one `.bak` of the
    previous file). Generated files are never read back as source, so the game's shutdown rewrite of
    `GameUserSettings.ini` cannot leak into tomorrow's source. Files are never rewritten on service
    start or re-attach. "Restore from database" is the only DB → disk direction and is a one-time
    explicit action.
17. Launch-argument builder: map, `?`-keys, typed `-Flag` fields (cluster base + instance override),
    **`-port=<game port>` (mandatory — the INI value is ignored by the game)**, `-mods` = cluster
    mods (cluster order) then instance mods (instance order), `-clusterid`,
    `-ClusterDirOverride=DataRoot\Clusters\<slug>`, `AltSaveDirectoryName`, `-WinLiveMaxPlayers`,
    `-log -servergamelog` (no `-stdout`; the log tail is the output source), `-NoBattlEye`
    unless a typed flag enables it, then free-text additional args. **One
    reserved-key policy** in `Core` (`ReservedKeys`) lists every manager-owned command-line option and
    INI key: `-port`, `-clusterid`, `-ClusterDirOverride`, `-mods`, `-WinLiveMaxPlayers`,
    `AltSaveDirectoryName`, `Port`, `RCONPort`, `RCONEnabled`, `SessionName`, `ServerAdminPassword`
    is *not* reserved (it is user-owned INI text the manager only reads). **Free text is tokenized
    and validated** against it: a reserved token or any token containing `?` (map-string delimiter
    injection) is rejected at save time with the offending token named. The same list drives step
    16's INI/override enforcement. Process launch uses `ProcessStartInfo.ArgumentList` — no shell.
18. Slug generation from instance/cluster name: filesystem-safe, unique across live instances **and**
    `Archive\` entries, immutable after creation.

### Phase 4 — Infrastructure services (singletons / hosted services, never tied to a Blazor circuit)

19. **Concurrency model.** One `SemaphoreSlim` per instance serializes Start/Stop/Restart/Backup/
    Delete for that instance (a second request while one is running is rejected with "operation in
    progress"). One **global launch queue** is the only thing that calls `Process.Start` for game
    servers; every Start, Start Selected, and Start All enqueues, and the queue applies the stagger
    delay between consecutive launches regardless of which button produced them. A
    **maintenance gate** (`ReaderWriterLockSlim` semantics): install/update take it exclusively
    **as their very first action — before the countdown, before collecting the running set** — and
    hold it through SteamCMD verification; launches take it shared **from before `Process.Start`
    until the process is registered in the in-memory process table and `LastPid`/
    `LastProcessStartTime` are persisted**. A persistence failure is retried **3 times over ~10 s**;
    if it still fails, the Start terminates with a visible error, the in-memory registration is
    kept with state `IdentityUnpersisted`, and **both the shared gate and the instance lock are
    released** so a storage failure never becomes a management outage. In `IdentityUnpersisted`:
    Stop, Delete, and a "Retry persist" action are permitted; Start is not (the process is
    already running); maintenance is refused with "instance identity unresolved" until a retry
    succeeds, reconciliation matches the process by token/path, or the process's exit is
    verified. While it is held exclusively, new
    Start requests are rejected with "update in progress" and the launch queue is drained
    (queued-but-not-started launches are cancelled and reported). The exclusive acquire waits for
    in-flight launches to finish registration, so the running set collected afterward is complete.
    Separately, a **maintenance operation lock** (a single `SemaphoreSlim`) is held by the whole
    update/install workflow from its first action through `MaintenanceState = None`; a second
    Update is rejected while it is held **or while `MaintenanceState` is anything but `None`**
    (i.e. an unresolved recovery), with the reason shown.
20. **SteamCMD runner**: download + extract on first run into `DataRoot\SteamCMD`; anonymous login
    only; `+app_update 2430930` (+`validate` when the App Setting is on); **retry with exponential
    backoff** (5 attempts, 30 s → 8 min) on non-zero exit; after the last failure the UI shows an
    explicit "install failed — retry" state, never a spinner. Completion is verified by the
    `appmanifest` check from step 11. Stdout streams through the shared `ConsolePanel`. Used by
    first-run install (phase `Installing`, cleared by the runner on verified success) and by the
    update flow, which owns `MaintenanceState` itself (step 29) — the runner never clears a phase it
    did not set. **Spike result:** a fresh `steamcmd.exe` self-updates and exits with code 7
    without running the requested commands — treat exit 7 as "run again immediately", outside
    the backoff budget. `+force_install_dir` must precede `+login`. With stdout redirected the
    progress lines are parseable (`Update state (0x61) downloading, progress: 15.67 (bytes /
    total)`) for a progress bar; success is `Success! App '2430930' fully installed.` A full
    12.2 GB download took ~19 min.
21. **Process manager**: launches instances through the junction path as detached children (they
    survive service restarts); persists `LastPid` + `LastProcessStartTime` (= `Process.StartTime`)
    immediately after `Start` returns. **Reconciliation on service start, for every instance (with
    or without a PID):** enumerate `ArkAscendedServer.exe` processes via WMI `Win32_Process`
    (`ProcessId`, `CommandLine`, `ExecutablePath`, `CreationDate`); parse each command line's map
    string into `?`-tokens and match **exact** `AltSaveDirectoryName=<slug>`; require the
    executable to be `ArkAscendedServer.exe` under `DataRoot` (junction or real path per Spike B).
    Identity: if `LastPid` + `LastProcessStartTime` are known and a process with that PID has a
    `CreationDate` within 2 s of the stored start time and passes the token/path check, attach to
    it; otherwise (persistence interrupted, PID reused) fall back to the token/path match alone.
    Exactly one candidate → attach; zero → Stopped; more than one → `Unknown` state with a UI
    warning and no automatic action. Crash detection for attached processes uses periodic
    `HasExited` polling (the `Exited` event only fires for processes this service started).
    **Spike result:** WMI reports the junction path, so the executable check is "under
    `DataRoot\Instances\`" with no real-path resolution; `CreationDate` equals `StartTime` to the
    millisecond. **A normal `doexit` exit has code -1** — an unexpected exit is "exited without a
    manager-initiated stop", never "non-zero exit code". Launch with `RedirectStandardOutput` off
    (the log is the source); the game survives its parent's death either way.
22. **Console service**: per-instance bounded ring buffer (5,000 lines) fed by `IOutputSource`; on
    re-attach, backfills the last N lines from `ShooterGame.log` and tags the panel "re-attached —
    log history". Blazor components subscribe to an event and marshal to the circuit via
    `InvokeAsync`. **Liveness and readiness are separate:** liveness = process alive; readiness =
    RCON auth succeeds. Fresh launches derive Starting → WorldLoaded → Advertising from output
    markers but are promoted to Running by a successful RCON probe (polled every 15 s while
    Starting); re-attached instances skip markers entirely and go Running on the first successful
    RCON probe. Starting longer than a bound (10 min) becomes `StartingUnconfirmed` — still alive,
    still probed, shown in yellow. An attached process whose RCON probe keeps failing past the same
    bound becomes `Unreachable` (alive, red badge, "check ServerAdminPassword/RCONPort"); backups
    for `Unreachable`/`StartingUnconfirmed` instances are recorded as "skipped — RCON unreachable"
    so a missed schedule is visible, never silent. **Spike result — markers and timings:**
    `Server: "<name>" has successfully started!` (+5 s, too early to mean anything), `Full
    Startup: N seconds` (+20–55 s) = WorldLoaded, `Server has completed startup and is now
    advertising for join.` (+65–95 s) = Advertising; `Log file closed` = clean shutdown. RCON
    listens from ~+5 s but is slow (auth 5–7 s, commands 2–5 s) until Advertising, so the
    Starting-phase probe uses the full RCON timeout and a slow reply is "still starting". Backfill
    on re-attach: read the current `ShooterGame.log` tail; the previous session's lines are in
    `ShooterGame-backup-<ts>.log` and are not needed.
23. **RCON** via CoreRCON: `saveworld`, `doexit`, `broadcast`, `ListPlayers`. Every command has the
    App-Setting timeout. **RCON credentials are loaded from the generated
    `Saved\Config\WindowsServer\GameUserSettings.ini` both at launch and at attach** (the generated
    file persists on disk across service restarts, and it is what the running process actually
    read). At launch, if `ServerAdminPassword` is missing or empty, **Start is refused** with a
    message naming the key (RCON is the only stop path, so it is mandatory); at attach, a missing
    password leads to `Unreachable` rather than a refusal. **Spike result:** confirmed — the
    game reads the credentials from the INI, and the launch-time rewrite preserves them. Wrap
    CoreRCON `Dispose()` in a try/catch (it throws `SocketException` when never connected) and
    create a client per command batch rather than holding one open.
24. **Stop sequence** with an overall deadline: optional broadcast countdown (App Setting minutes)
    → `saveworld` (bounded) → `doexit` (bounded) → wait for exit up to the
    graceful timeout → kill with a visible warning. RCON auth failure or a timed-out command falls
    through to the next step immediately rather than hanging. Update and Delete require a
    **verified** exit (`HasExited`) before proceeding. Runs as a background job so a closed browser
    tab does not abort it. **"Stop now" is not a second operation:** the running stop job exposes a
    `SkipCountdown` `CancellationTokenSource`; the button signals it, the countdown ends, the
    sequence continues under the same instance lock.
25. **Start orchestration**: Start (one), Start Selected (checkbox set), Start All (cluster) — all
    through the global launch queue (step 19). Same three shapes for Stop (stops may run in
    parallel; only launches are staggered).
26. **Firewall** (`WindowsFirewallHelper`): **port-based inbound rules for the game port and port+1
    (UDP) only — no inbound RCON rule**, RCON is consumed locally by the manager. Rules are named
    `ArkAscendedServerAdmin-<instanceId>` and **reconciled on every Start** (recreate if the instance's ports
    changed), removed on delete. A rule for the Kestrel port is created only when the bind address
    is not loopback, scoped to `KnownProxies`.
27. **Junctions** (as in DESIGN.md §4 — real NTFS junctions, not symlinks): .NET's
    `Directory.CreateSymbolicLink` creates symlinks, which need `SeCreateSymbolicLinkPrivilege` and
    are untested with Unreal; create junctions via `cmd /c mklink /J` or a
    `DeviceIoControl`/`FSCTL_SET_REPARSE_POINT` P/Invoke. Idempotent "ensure layout" routine, also
    used by restore-from-database.
28. **Backups are explicitly best-effort live backups**, labeled as such in the UI. Per-instance
    hosted timer that fires only while the instance is Running and runs under the instance lock
    (so it cannot overlap Stop/Delete or a manual backup):
    1. `saveworld`, then wait for the completion signal Spike A (f) established: the `World
       Saved` reply arrives **before** the write, so after the reply poll until `<Map>.ark` opens
       with `FileShare.Read` **and** its length + last-write time have been stable for ≥1 s,
       bounded by the quiescence window (observed: locked for ~350 ms, settled ~1.1 s after
       the reply). Before the instance's first save the stub `.ark` is held open permanently —
       that attempt is recorded "skipped — world file in use", which is correct.
    2. **Inventory:** enumerate the world directory **`Saved\<slug>\<Map>\`** (the
       `AltSaveDirectoryName` location — `SavedArks` is not used) plus the cluster dir, selecting
       `<Map>.ark`, `*.arkprofile`, `*.arktribe`, and the cluster-dir files explicitly; **exclude
       `*.arkrbf` and `*_AntiCorruptionBackup.bak`** (the game keeps a rolling set of ~4
       world-size rollback copies and deletes the oldest on each save, which would both bloat
       the archive and trip the "removed between inventories" check). Record (relative path,
       length, last-write time) for each selected file.
    3. **Snapshot by copy** to `Backups\<instance>\.snap-<guid>\`, computing SHA-256 of every file
       as it is copied. Each file is opened with `FileShare.Read` only — a sharing violation means a
       writer holds it and the attempt is retried after the quiescence window (3 attempts, then
       "skipped — world file in use").
    4. **Re-inventory** and compare the complete before/after lists (paths, lengths, times): any
       added, removed, or changed entry → retry from step 2 once, then skip with reason. Write
       `manifest.json` (relative path, length, SHA-256 for every file) into the snapshot.
    5. Zip the snapshot to `.tmp-<guid>.zip`, delete the snapshot directory.
    6. **Verify recoverability explicitly** (`ZipArchive` in .NET 10 does not validate CRC on read):
       for every manifest entry, require a matching zip entry, extract it to a stream, and compare
       length and SHA-256 against the manifest; require `<MapKey>.ark` to be in the manifest.
       Failure → delete the temp file, record "failed — verification".
    7. Atomic rename to `<yyyyMMdd-HHmmss>-<seq>.zip` → insert `BackupRecord` → prune oldest beyond
       retention (manual and scheduled counted alike), only after the new record is committed.
    Manual button uses the same job. Accepted risks, documented in the UI: no VSS/immutable
    snapshot, so a save that begins between the check and the copy can still produce a torn world
    file (mitigated by the sharing-violation and size/mtime rechecks); another cluster member may
    write the shared cluster dir during the copy.
29. **Update flow** (a state machine in `Core`, driven by `Infrastructure`, persisted at every
    transition): refuse while instances run unless confirmed → **take the maintenance gate
    exclusively** (rejects/drains launches, waits for in-flight ones) → **collect every alive
    instance** (Running, StartingUnconfirmed, Unreachable — anything with a live process) → if any
    instance is `Unknown`, **refuse the update** ("resolve ambiguous instances first") → write
    `MaintenanceState = Stopping` with the list → broadcast countdown → graceful stop with verified
    exit, marking each instance `done` as it exits → **safety invariant before SteamCMD:** enumerate
    `ArkAscendedServer.exe` processes whose executable is under `DataRoot` (WMI, same query as
    step 21); if any exist, refuse to proceed and surface them → `Updating` → SteamCMD → verify
    `appmanifest` → `Restarting` (all `done` flags reset) → release the gate → for each listed
    instance: launch through the queue and mark `done` **only after `Process.Start` succeeded and
    `LastPid`/`LastProcessStartTime` are persisted**; a launch that fails records a per-entry
    `error` (visible on the Dashboard with Retry/Skip, see step 11) → when all are `done`, phase
    `None` and the maintenance operation lock is released. The maintenance operation lock is held
    for the entire flow, so a second Update cannot start while restarts are pending. If the service
    restarts mid-flow, step 11 resumes from the persisted phase: a `Restarting` entry not `done`
    whose instance reconciliation already found alive is marked `done` without launching; the rest
    are launched via the recovery path. Nothing is re-stopped or double-launched.
30. **Instance delete**: the confirmation dialog (including the keep/delete world-data choice, with
    "delete" requiring the instance name to be typed) is completed **before** the job is enqueued;
    the job then runs to completion in the background under the instance lock with no further
    browser interaction: stop with verified exit → remove firewall rules → remove junctions →
    **keep** moves `Instances\<slug>\ShooterGame\Saved` to `Archive\<slug>-<yyyyMMdd-HHmmss>\`
    (slug reservation honored by step 18), **delete** removes it → delete DB rows. The lock is
    released in a `finally`.

### Phase 5 — UI (Radzen, in the `Components` RCL)

31. Pages: Login, Setup (install console), Dashboard/instance list (grouped by cluster, standalone
    last; checkbox selection; Start/Stop/Restart/Backup per row; Start/Stop Selected; Start/Stop All
    per cluster; status badge incl. `Unknown` / `StartingUnconfirmed`), Instance detail (console
    panel with RCON input, INI editor tabs (`RadzenTextArea`) editing the **source** files, mods
    (cluster mods greyed/locked on top, instance mods sortable), launch flags with validation
    errors, ports, backups list), Cluster page (INI source, cluster mods, cluster launch flags, admin
    whitelist), Mod library (CurseForge search with thumbnails, manual id entry when no key), Known
    players (refresh via `ListPlayers`, pick into whitelist), Maps, Settings (App Settings, "export
    config backup", read-only `appsettings.json` values such as `DataRoot`), Instance wizard (name →
    cluster/standalone → map → config source → mods → launch flags → ports → summary).
32. Shared `ConsolePanel` component is used by instance consoles and the SteamCMD console.

### Phase 6 — Tests

33. `UnitTests` (xunit + NSubstitute, `net10.0`): port allocation, INI merge/generation incl.
    reserved-key replacement, launch-argument builder incl. additional-args rejection cases, slug
    generation incl. archive reservations, backup retention pruning, command-line reconciliation
    matcher (exact token, PID/start-time identity, ambiguity), **update state machine — every
    persisted phase × every recovery input (alive/dead listed instances, partial `done` flags,
    interruption before enqueue / after enqueue / after `Process.Start` / after identity
    persistence / after `done`) yields the expected next action and never double-stops or
    double-launches; `Unknown` instances and live processes under `DataRoot` block `Updating`;
    a second Update is rejected while `Restarting` has unresolved entries; a failed recovery
    launch records an error and does not block readiness; Retry/Skip resolve entries**, launch
    registration ordering (the shared gate is held until persistence succeeds, so an update
    collecting instances after the gate sees the new process; a persistence failure that never
    succeeds ends Start with an error after bounded retries, releases the gate and instance lock,
    leaves Stop usable, and keeps maintenance blocked until retry/reconcile/exit resolves it),
    launch
    queue + maintenance gate ordering with fake clocks (launch after gate acquire is rejected,
    queued launches are drained), password-hash claim validation (stale hash → invalid), CurseForge
    client against recorded responses.
34. `Infrastructure.IntegrationTests` (xunit, `net10.0-windows`, real temp filesystem): junction layout ensure/remove
    (idempotent), INI source → generated write with atomic replace and `.bak`, optimistic-concurrency
    save rejection, **backup round trip: inventory → snapshot → manifest → zip → verify → rename →
    extract to a fresh directory → byte-compare with the source** (the restoration smoke test),
    plus negative cases: a truncated entry, an entry with the same length but corrupted bytes, and
    a file added between inventories — all must be rejected; prune; process reconciliation against
    a stub process list. No UI/browser tests (cookie + circuit revalidation is verified manually
    during Spike A's service run), no OS-level crash injection (the state machine is pure and
    exhaustively unit-tested instead).

## Key decisions & tradeoffs

- **Blazor Server as a Windows service first; WPF later via the shared RCL.** The owner runs it as a
  service and never remotes in. RCL is cheap insurance for a possible OSS standalone host.
- **Service runs as LocalSystem.** Removes the elevation problem entirely (firewall, WMI, process
  ownership). Trade: everything the web UI can do runs as SYSTEM — which is exactly why auth is not
  optional. Revisit before any OSS release.
- **Loopback-bound Kestrel behind the owner's reverse proxy, single-password cookie auth.** Kestrel
  defaults to `127.0.0.1` — that is the real ingress control; the proxy terminates HTTPS; the
  app additionally rejects any non-HTTPS effective request so a LAN rebind without the proxy fails
  closed. One password, no users/roles; throttle + lockout + hash-claim revalidation (cookie and
  circuit) are the minimum for a UI that runs as SYSTEM.
- **No visible Unreal console window.** A service runs in session 0, so `-log`'s window is never
  visible to anyone; the browser console panel *is* the console. DESIGN.md §2's "visible console so
  the user can still remote in" is dropped. `-log` is still passed because it drives log output.
- **Log tail over stdout capture (confirmed by Spike A).** Re-attach after a service restart can
  only ever use the log, the game flushes each line immediately (200 ms average latency with a
  100 ms poll), so one log-tail implementation serves both states. Stdout is not captured.
- **Anonymous SteamCMD only; retry with backoff.** Authenticated Steam (interactive password/Steam
  Guard) has no sane path through a service and is out of MVP.
- **CurseForge API key stored plain text in SQLite.** Read-only key on the owner's box. Data
  Protection is still configured — but only because cookie auth needs a stable key ring.
- **`DataRoot` in `appsettings.json`, not in the DB.** The DB lives under it, so it cannot be a DB
  setting. Changing it is a file edit + restart; moving data is post-MVP.
- **Source INI on disk under `Config\`, generated INI under `Saved\`, DB is a backup mirror.** The
  split keeps the game's own rewrites out of the source of truth and keeps the "copy the DB, restore
  recreates everything" story.
- **Serialization over cleverness.** Per-instance lock + one launch queue + one maintenance gate is
  the entire concurrency model; it is easy to reason about and the workload is a handful of
  instances on one box.
- **Port-based firewall rules, game ports only.** Program rules were ambiguous through junctions;
  RCON never needs to be reachable from off-box.
- **CoreRCON** over hand-rolled RCON: handles multi-packet responses; swap only if Spike A shows
  ASA-specific quirks.
- **All long-running work is hosted/background, not circuit-bound.** Blazor Server circuits die when
  a tab closes; installs, stops, backups, and staggered starts must not.
- **Backups are best-effort live snapshots, not consistent point-in-time captures.** A backup that
  forces a stop defeats an interval scheduler, and a VSS snapshot is out of proportion for MVP.
  `saveworld` + copy-with-sharing-check + size/mtime recheck + full archive verification is the
  bar; anything short of it is recorded as skipped/failed with a reason, never silently kept, and
  the UI labels the feature honestly.
- **Known players refreshed on demand only.** Its real job is "find my own EOS id".
- **Radzen + plain `RadzenTextArea` for INI.** Line numbers/code editor only if it hurts in practice.
- **Graceful stop on OS shutdown deliberately not handled.** Auto-save + interval backups are the
  protection; racing the shutdown window is fragile.

## Risks / open questions

- ~~Log-tail latency~~ — resolved (Spike A: 200 ms avg). ~~Log rotation semantics~~ — resolved
  (rename to `-backup-<ts>` on next launch; creation time is tunneled). ~~`saveworld` reply vs.
  disk~~ — resolved (reply precedes the write; quiescence rule in step 28). ~~WMI path through a
  junction~~ — resolved (junction path).
- ~~Exact EOS-id source for the whitelist (log line vs. `ListPlayers`) and the populated
  `ListPlayers` format~~ — resolved 2026-09-13: the owner joined a test instance; `ListPlayers`
  replied `0. <gamertag>, <eos-id>` and the log's join line carried the same
  id as `UniqueNetId`. The parser already accepted the format; the real line is pinned in a unit
  test. `ListPlayers` stays the whitelist source.
- ~~Whether `-WinLiveMaxPlayers` or INI `MaxPlayers` is authoritative~~ — resolved 2026-09-12: the
  flag (ark.wiki.gg marks INI `MaxPlayers` as not honored in ASA). Builder unchanged.
- ~~Every instance binds UDP 27015~~ — resolved 2026-09-12: vestigial Steam socket, ASA uses EOS;
  nothing to increment or forward (see HANDOVER §7).
- SteamCMD anonymous throttling may still exceed the backoff budget on bad days; the UI shows an
  explicit failed state with a retry button.
- Implementation runs from Windows PowerShell on the VM that will host the service;
  `Infrastructure`, `Server`, and `Infrastructure.IntegrationTests` cannot build or run from WSL.
- Junction creation via `mklink /J` shells out to `cmd`; a P/Invoke is cleaner but more code.
  Decide at implementation time; either is fine under LocalSystem.
- Accepted risk: backups are best-effort — no VSS; a save starting between the sharing check and
  the copy can still tear a world file, and cluster-directory contents may change during the copy.

## Out of scope

WPF `Desktop` host (and its tray/close-blocking behavior); authenticated Steam; API-key encryption;
graceful instance shutdown on OS shutdown; scheduled restarts; crash auto-restart; Discord webhooks;
player list UI beyond the known-players refresh; app self-update; multihome IP binding; typed INI
forms; backup restore UI; pinned backups; passive mods; changing `DataRoot` after setup; HTTPS inside
Kestrel (handled by the owner's reverse proxy); users and roles; least-privilege service account;
OS-level service-interruption tests and browser/UI tests (file-level backup round-trip tests are
in scope, see step 34).
