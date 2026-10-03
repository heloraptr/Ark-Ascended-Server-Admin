<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: ArkServerAdmin MVP — Blazor Server host, Windows service
Act 1 (grill) complete — plan locked with the user. MAX_ROUNDS=5 (raised to 6 by the owner after round 5).

## Round 1 — Codex

The plan still has material security, recovery, and data-integrity problems. References below are to numbered plan steps; no files were modified.

1. **Build blocker — steps 1, 5–6.** `Server` targets `net10.0` but references `net10.0-windows` Infrastructure; additionally, `Host.CreateApplicationBuilder` is the generic host builder, not the existing web host, and does not support the stated `UseWindowsService()` combination. **Fix:** Target Server at `net10.0-windows`, retain `WebApplication.CreateBuilder`, and register `builder.Services.AddWindowsService()`. ([TFM documentation](https://learn.microsoft.com/en-in/dotnet/standard/frameworks), [service hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0))

2. **HTTPS is bypassable — security decision, step 24.** Binding HTTP on every interface and opening its firewall port lets clients bypass Nginx; `SameAsRequest` permits an insecure cookie when logging in directly. Proxy trust is also unspecified. **Fix:** Restrict Kestrel ingress to the proxy, explicitly configure trusted proxies and hosts, and require secure cookies. ([Proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0))

3. **Authentication is underspecified for SYSTEM-level access — security decision.** “Every page requires the cookie” does not define authorization for the interactive connection and other endpoints, login throttling, or session invalidation after password changes. **Fix:** Specify endpoint authorization, an anonymous login endpoint with throttling and antiforgery, and session expiry/revalidation covering existing circuits.

4. **Setup can block itself — step 10.** Redirecting *every request* includes `/setup`, login, scripts, and Blazor negotiation; even exempting `/setup` alone leaves its live console unusable. **Fix:** Explicitly allow authentication and setup dependencies, protect setup actions, and enforce readiness inside operational services too.

5. **Lifecycle operations race — steps 22–28.** Two tabs can start the same instance; Start can run during an update countdown; Backup can overlap Delete; separate multi-start jobs bypass the intended global stagger. **Fix:** Use per-instance operation serialization, a global launch scheduler, and an exclusive shared-install maintenance gate.

6. **Background jobs have no restart recovery — steps 18, 22, 27.** A service restart can lose the update restart set or launch another SteamCMD while the previous process still modifies the shared install; existence checks can accept a partial installation. **Fix:** Persist maintenance phase and intended restart set, reconcile surviving installer/game processes before readiness, and require verified installation completion before launching games.

7. **Process identification can select the wrong server — step 19.** Substring matching `AltSaveDirectoryName=foo` also matches `foobar`; the saved-PID branch does not verify executable identity, and “for each instance with a PID” misses a crash between launch and PID persistence. **Fix:** Reconcile every instance using exact parsed arguments, expected executable path and process creation time, and reject ambiguous matches.

8. **Stdout capture conflicts with reattachment — steps 11, 13, 19–20.** A replacement service cannot simply reacquire the old anonymous stdout pipe; backfilling a log provides history but no specified continuing stream. Pipe closure behavior is absent from Spike A. **Fix:** Extend the spike through service termination/restart and sustained output afterward; choose continuous log tailing if viable, otherwise explicitly design a durable capture mechanism.

9. **Log-derived status can disable backups indefinitely — steps 20, 26.** A reattached server may have startup markers outside the last 200 lines, so a healthy instance never reaches `Running`; stale markers can also misrepresent current readiness. **Fix:** Separate process liveness from readiness, probe reattached instances, and define bounded startup failure/degraded states without silently suppressing backup scheduling.

10. **Backups are not proven consistent — step 26.** `saveworld` response completion is not established as disk-save completion; files can change during ZIP creation, and other cluster members can mutate the shared cluster directory. **Fix:** Validate save completion and implement a coordinated quiescence/snapshot strategy for world and cluster data, using a stopped backup if live consistency cannot be guaranteed.

11. **Backup publication and retention can lose recovery points — step 26.** Manual and timer jobs can collide on second-resolution filenames; disk-full or interrupted ZIP writes have no defined incomplete state or pruning guard. **Fix:** Serialize backups, write uniquely named temporary archives, validate and atomically publish them, and prune only after recording a successful replacement.

12. **RCON failure can bypass the stop deadline — steps 21–22.** Authentication failure or a hung `saveworld` can prevent execution from ever reaching the timed process wait; RCON enablement and credential resolution are not specified. **Fix:** Validate effective RCON settings and apply bounded command timeouts within an overall stop deadline, with explicit fallback and verified exit before update/delete.

13. **RCON is unnecessarily exposed — step 24.** Only the local manager needs RCON, yet the plan opens inbound TCP access for every instance. Port edits also leave first-start firewall rules stale. **Fix:** Keep RCON local and reconcile managed firewall rules against current ports and allowed network scope.

14. **The INI source can overwrite itself — step 15.** Standalone source text has no separate canonical path; using generated `WindowsServer` files as the working copy allows manager overrides and game shutdown rewrites to become tomorrow’s source. File-plus-DB saves also lack failure recovery. **Fix:** Store canonical source INIs separately, use atomic file replacement and version checks, and define how failed mirror writes are surfaced and reconciled.

15. **Additional arguments can defeat managed invariants — step 16.** Duplicate ports, save-directory tokens or cluster paths can override values used for collision checks, process identity and backup targeting. Quoting individual OS arguments does not neutralize embedded ARK `?` parameters. **Fix:** Parse and validate free-text arguments, reject manager-owned options and delimiter injection, and launch directly without a shell.

16. **Singleton storage lifetime is unresolved — steps 8, 18–28.** A captured `DbContext` would be shared across timers, jobs and browser requests, producing concurrent-use failures and stale tracking. **Fix:** Require an `IDbContextFactory` context per operation, short transactions, and explicit concurrency handling. ([EF guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core?view=aspnetcore-10.0))

17. **“Copy one SQLite file” is not a safe live backup — storage decision.** An active transaction or uncheckpointed WAL can make that copy incomplete or inconsistent. **Fix:** Provide a SQLite online-backup export, or require a fully closed database before copying. ([SQLite documentation](https://www.sqlite.org/howtocorrupt.html))

18. **Retained worlds can be silently reused — steps 17, 28.** Deleting the DB row while retaining `Saved` releases the slug unless filesystem reservations also count; recreating the same name can attach an unrelated instance to old world data. **Fix:** Preserve a tombstone/reservation or move retained data to an explicitly identified archive before releasing the slug.

19. **The test exclusion misses the highest-risk behavior — step 31.** Pure-function tests cannot validate service restart survival, setup/auth routing, junction isolation, update exclusion, or recoverable backups. **Fix:** Add a small Windows integration suite covering those boundaries, including service interruption and a backup restoration smoke test.

VERDICT: REVISE
### Claude's response (round 1)

**Accepted as-is (plan changed):**
1. Build blocker — real. `net10.0` cannot reference `net10.0-windows`. `Server` → `net10.0-windows`; `WebApplication.CreateBuilder` + `AddWindowsService()`.
2. HTTPS bypass — real. Kestrel bind address now configurable (default loopback); cookie `SecurePolicy = Always` so a direct-HTTP login cannot produce a usable session; `KnownProxies` from `appsettings.json`; Kestrel firewall rule scoped to the proxy address, none when bound to loopback.
3. Auth underspecified — real. Added: fallback authorization policy, anonymous `/login` SSR form with antiforgery, fixed-delay + lockout throttle, sliding expiry, password-hash claim validated in `OnValidatePrincipal` so a password change invalidates existing sessions. Also caught the consequence: cookie encryption needs Data Protection keys persisted under `DataRoot\keys` (not for the API key — for the cookie).
4. Setup redirect self-block — real. Explicit allowlist (`/setup`, `/login`, `/_blazor`, `/_framework`, `/_content`, static files) and readiness enforced inside the process manager.
5. Lifecycle races — real. Per-instance operation lock, one global launch queue applying the stagger, shared-install maintenance gate (update exclusive, start shared).
6. No restart recovery — real. `MaintenanceState` row persists phase + intended restart set; startup reconciles surviving `steamcmd.exe`; install "complete" = `appmanifest_2430930.acf` `StateFlags 4`, never a directory-exists check.
7. Process identification — real. Parse command line into `?`-tokens, exact `AltSaveDirectoryName=<slug>`, executable name check, creation time ≥ persisted launch time, ambiguity → Unknown state. Reconcile all instances, not just ones with a PID.
8. Stdout vs re-attach — real, and it sharpened a decision: after a service restart the only possible source is the log, so stdout-primary is inherently dual-mode. Plan now prefers log tail; stdout only if the log is nonviable. Spike A extended through service restart.
9. Status on re-attach — real. Liveness (process) separated from readiness (RCON auth succeeds); re-attached alive + RCON OK = Running; bounded Starting with RCON probe.
11. Backup publication — real. Temp file + atomic rename, unique names (timestamp + sequence), serialized via the instance lock, prune only after a verified success.
12. RCON bypasses stop deadline — real. Per-command timeout inside an overall stop deadline; manager reads `ServerAdminPassword` from the generated INI and refuses start if RCON is not enabled/passworded.
13. RCON firewall exposure — real. No inbound RCON rule; firewall rules named by instance id and reconciled on every start.
14. INI self-overwrite — real. Canonical source now lives at `Clusters\<slug>\Config\` or `Instances\<slug>\Config\`; generated files under `Saved\Config\WindowsServer` are never the working copy; atomic writes; mirror failure surfaces as a UI warning.
15. Additional-args injection — real. Free text is tokenized, manager-owned keys rejected, `?`-delimiter payloads rejected, launched via `ArgumentList` without a shell.
16. DbContext lifetime — real. `IDbContextFactory`, context per operation.
17. SQLite file copy — real. "Export config backup" uses `SqliteConnection.BackupDatabase`; plan documents that a raw copy is only safe with the service stopped.
18. Slug reuse — real. Delete-with-retain moves `Saved` to `Archive\<slug>-<timestamp>` before releasing the slug.

**Accepted partially:**
10. Backup consistency — added a quiescence wait (save file mtimes stable for N seconds after `saveworld`) and "backup skipped" when quiescence is not reached. **Rejected** the coordinated cluster-wide snapshot / stopped-backup strategy: the cluster directory holds transfer data measured in KB, other members mutate it only on player transfer, and forcing a stop for a backup defeats the interval scheduler. Logged as an accepted risk.
19. Test scope — added a small `Tests.Windows` (`net10.0-windows`) suite for junction layout, INI generation on a real filesystem, backup zip/prune, and process reconciliation with a fake process. **Rejected** service-interruption and backup-restore integration tests: restart survival is what the extended Spike A proves, restore has no UI in MVP, and the owner explicitly bounded automated testing to unit scope.

## Round 2 — Codex

Several prior findings are addressed: project targets/hosting, setup routing, context lifetime, SQLite export, canonical INI separation, stdout restart testing, firewall reconciliation, and retained-world archiving. The remaining material problems are:

1. **Update exclusion still starts too late — steps 19, 29.** During countdown and stopping, launches remain allowed; an instance can start after the stop sweep but before SteamCMD acquires exclusivity.  
   **Fix:** Block new and queued launches before collecting the running set, drain in-flight launches, then stop and verify all managed processes before updating.

2. **Maintenance recovery remains unsafe — steps 11, 20, 29.** Startup resumes SteamCMD *before* reconciling surviving game processes. Step 20 clears maintenance after verification, while step 29 needs it through restart; clearing after merely enqueueing also loses pending restarts on a crash.  
   **Fix:** Reconcile processes first and persist distinct stopping/updating/restarting phases with per-instance completion, clearing maintenance only after restart work is resolved.

3. **The new creation-time check can reject the correct process — step 21.** `LastLaunchedAt` recorded after `Start` can be later than the process’s actual creation time, making `CreationDate >= LastLaunchedAt` false on reattachment.  
   **Fix:** Persist the actual process creation time and compare that identity exactly; use the validated token/path fallback for launches whose persistence was interrupted.

4. **Backup quiescence is still not consistency — step 28.** Old mtimes do not prove a writer is inactive, and a new write can begin after the check or during ZIP creation. Windows does not guarantee updated write timestamps until writing handles close. The accepted cluster-transfer risk does not cover torn world files.  
   **Fix:** Establish save completion and capture an immutable snapshot before compression; otherwise explicitly treat these as best-effort backups and reject captures where concurrent writes are detected. ([Windows file-time semantics](https://learn.microsoft.com/en-us/windows/win32/sysinfo/file-times))

5. **Opening a ZIP does not verify recoverability — steps 28, 34.** An archive can open while entry contents are truncated or required world files are absent; retention can then remove a good backup. Excluding restore tests because there is no restore *UI* misses the purpose of backups.  
   **Fix:** Read/extract every entry, validate expected contents, and add a restoration smoke test before relying on retention.

6. **Live-circuit authentication revalidation is asserted but not implemented — step 8.** `OnValidatePrincipal` validates cookie requests; it does not itself periodically revalidate an established Blazor circuit.  
   **Fix:** Specify a custom revalidating authentication-state provider, its interval, expiry checks, and how invalidation prevents further privileged actions. ([Blazor authentication documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0))

7. **`SecurePolicy = Always` is described as a server-side HTTP prohibition — step 8 and key decisions.** It sets a cookie attribute; it does not reject HTTP login submissions or manually supplied cookies. Network restrictions provide the actual protection.  
   **Fix:** Reject authentication and privileged requests whose effective scheme is not HTTPS after trusted-proxy processing, and retain proxy-only ingress. ([API behavior](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.cookiesecurepolicy?view=aspnetcore-9.0))

8. **Manager-owned settings remain overridable through another channel — steps 16–17.** Additional-argument validation does not stop `ExtraOverrides` from replacing `Port`, `RCONPort`, or `RCONEnabled` after typed values; typed map/session values also need delimiter validation.  
   **Fix:** Apply one reserved-key policy across every input channel and make manager-owned values authoritative in the final effective configuration.

9. **Reattached instances have no specified credential-loading path — steps 22–23.** Password loading happens “at launch,” but reattached instances must authenticate without a new launch; otherwise they remain unconfirmed and receive no scheduled backups.  
   **Fix:** Load effective RCON credentials during attachment as well, and surface persistent probe failures and missed backups explicitly.

10. **Delete still waits for browser input after changing the server — step 30.** Closing the tab at the confirmation dialog can leave an instance stopped, firewall rules removed, junctions removed, and its operation lock held.  
    **Fix:** Collect the keep/delete decision before starting the background job, then execute without further browser interaction.

11. **INI saves still lack concurrency control — step 16.** Atomic rename prevents partial files, but two tabs can overwrite each other and interleave mirror writes so SQLite contains an older revision than disk.  
    **Fix:** Serialize saves per document and require an expected revision/hash, including for mirror retries and database restoration.

12. **The revised tests still exclude the principal recovery risks — step 34.** Spike A tests game/output survival, not interrupted maintenance, launch exclusion, cookie/circuit authorization, or partial restart completion.  
    **Fix:** Add focused tests for these workflows, including crash injection at each persisted maintenance transition.

VERDICT: REVISE
### Claude's response (round 2)

**Accepted as-is (plan changed):**
1. Update exclusion — gate is now the update flow's first action; launch queue drained; exclusive acquire waits for in-flight launches.
2. Maintenance recovery — process reconciliation now precedes maintenance resume; phases `Stopping|Updating|Restarting` with per-instance `done` flags; update flow owns `MaintenanceState`, the SteamCMD runner only clears the `Installing` phase it set itself.
3. Creation-time check — persist the actual `Process.StartTime`, match PID + start time within 2 s, fall back to exact token/path when persistence was interrupted.
5. ZIP verification — every entry read to EOF, required entries checked; `Tests.Windows` gains a backup round-trip (extract + byte-compare) including a truncated-entry negative case.
6. Circuit revalidation — `RevalidatingServerAuthenticationStateProvider` (5 min) + forced navigation to `/login` tears the circuit down.
7. `SecurePolicy` — corrected: it is a cookie attribute. Added an explicit 403 for non-HTTPS effective requests (dev opt-out flag); loopback bind named as the real ingress control.
8. Reserved keys across channels — single `ReservedKeys` policy in `Core` applied to additional args, `ExtraOverrides`, and source INI text (manager values authoritative, editor warns); typed values validated for `?`, `=`, line breaks.
9. Re-attach credentials — loaded from the generated INI at attach as well; persistent probe failure → `Unreachable`; skipped backups recorded with reason.
10. Delete dialog — decision (incl. type-the-name confirmation) collected before enqueue; job runs without browser interaction; lock released in `finally`.
11. INI save concurrency — per-document lock + SHA-256 optimistic concurrency, hash stored in the mirror row and checked on retry/restore.

**Accepted partially:**
4. Backup consistency — accepted that mtime quiescence is not proof. Replaced with: snapshot-by-copy with `FileShare.Read` opens (sharing violation = writer active → retry/skip), post-copy size/mtime recheck, and the feature explicitly labeled **best-effort** in the UI. **Rejected** an immutable/VSS snapshot: it is disproportionate for MVP and the reference tools do not do it either; the residual torn-file window is documented as an accepted risk.
12. Tests — accepted the substance by moving the update flow into a pure state machine in `Core` and exhaustively unit-testing phase × recovery input, plus queue/gate ordering with fake clocks and hash-claim validation. **Rejected** OS-level crash injection and cookie/circuit browser tests: the pure state machine gives the same coverage without a service harness, and the owner bounded automated testing to no UI tests; circuit revalidation is verified manually during Spike A's service run.

## Round 3 — Codex

Most prior fixes are now present: early maintenance exclusion, process creation-time tracking, RCON credentials on attach, explicit HTTPS rejection, document concurrency, and confirmation before deletion. Best-effort backup consistency is now an explicit tradeoff. Material issues remain:

1. **Restart recovery still loses queued launches — steps 11, 29.** Marking an instance `done` when handed to the in-memory queue repeats the previous bug: a service crash before `Process.Start` permanently loses that restart.  
   **Fix:** Mark completion only after successful launch and identity persistence; on recovery, reconcile unfinished entries against live processes before launching.

2. **Startup recovery conflicts with readiness — step 11.** Recovery must enqueue pending restarts before reaching `Ready`, but the process manager refuses Start while not `Ready`. Waiting for actual restart completion would deadlock; rejecting those starts loses recovery work.  
   **Fix:** Give the recovery orchestrator an explicit internal launch path permitted after install verification, while public Start remains blocked.

3. **Maintenance needs an all-process safety invariant — steps 11, 22, 29.** “Running set” can exclude alive `StartingUnconfirmed`/`Unreachable` instances; `Unknown` explicitly prevents automatic action. None may safely coexist with SteamCMD modifying their shared binaries.  
   **Fix:** Require verified absence of every process using the managed installation before SteamCMD, include all alive states in stopping, and block maintenance on ambiguous identity.

4. **Live-circuit revocation still relies on UI behavior — step 8.** `AuthorizeView` and browser navigation are not authorization checks on privileged event handlers; a stale event can arrive around invalidation. Microsoft explicitly distinguishes visibility from handler security.  
   **Fix:** Check current server-side authorization at every privileged command entry point before enqueueing work. ([Blazor security guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0))

5. **ZIP verification assumes functionality absent from the selected runtime — step 28.** Reading entries to EOF does not automatically validate CRC32 in .NET 10; that behavior is introduced in .NET 11. A truncated-entry test alone does not catch silent corruption.  
   **Fix:** Explicitly compute and compare checksums, or compare extracted entry hashes against a snapshot manifest; test a readable archive with corrupted content. ([Version-specific documentation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/ziparchive-entry-crc32-validation))

6. **Backup file-set changes go undetected — step 28.** Rechecking copied files’ lengths and timestamps misses new profile/tribe files created during enumeration; requiring “at least one” permits omission of other required files.  
   **Fix:** Compare complete before/after inventories and verify every captured relative path, size, and checksum, retrying when the inventory changes.

7. **“Stop now” conflicts with operation serialization — steps 19, 24.** An active countdown holds the instance operation lock, so another Stop request is rejected as “operation in progress.”  
   **Fix:** Implement Stop Now as a signal to the existing stop job that cancels its countdown, rather than a second lifecycle operation.

8. **The test policy remains internally contradictory — step 34 versus Out of scope.** Step 34 includes a restoration integration test, but the final section excludes restore integration tests; state-machine tests also need to cover the enqueue-versus-launch crash gap above.  
   **Fix:** Remove the contradictory exclusion and explicitly test interruption before/after enqueue, process creation, identity persistence, and completion persistence.

VERDICT: REVISE
### Claude's response (round 3)

All eight accepted; each was a precise gap in a round-2 fix rather than a new area.
1. `done` is now set only after `Process.Start` succeeded and PID/start-time are persisted; recovery marks already-alive entries `done` without launching.
2. Readiness gains `Recovering`; the orchestrator uses an internal `EnqueueRecovery` path while public `Start` stays refused until `Ready`.
3. Update collects every alive state, refuses on any `Unknown`, and enforces a "no `ArkAscendedServer.exe` under `DataRoot`" WMI invariant immediately before SteamCMD.
4. Scoped command facades with `IAuthorizationGuard.EnsureAuthorizedAsync()` at every entry point; `AuthorizeView` is visibility only.
5. Explicit SHA-256 manifest written into the archive and verified by extracting every entry (acknowledging .NET 10 `ZipArchive` does not validate CRC on read); corrupted-content negative test added.
6. Complete before/after inventory comparison with retry; every manifest path must exist in the archive.
7. "Stop now" is a `SkipCountdown` cancellation on the running stop job, not a second operation.
8. Out-of-scope wording fixed: file-level restore round trip is in scope; only OS-level interruption and browser tests are excluded. State-machine tests enumerate interruption at every persisted transition.

## Round 4 — Codex

The eight findings from the last round are addressed at the plan level: restart completion persistence, recovery launches, process safety checks, command authorization, checksum verification, inventory comparison, Stop Now signaling, and test scope.

Three material lifecycle gaps remain:

1. **A second update can overwrite an unfinished update — steps 19, 29.** The maintenance gate is released during `Restarting`, while the first workflow still owns the single `MaintenanceState` row. Another Update can acquire the gate, cancel pending restarts, and replace that row.  
   **Fix:** Serialize the entire maintenance workflow through restart completion with a separate operation lock; reject another update whenever a maintenance operation remains unresolved.

2. **Recovery failures can trap the owner on Setup — steps 11, 29.** A pending restart can fail because its password is missing, its port conflicts, or process creation fails. It never becomes `done`, so startup never reaches `Ready`; the redirect blocks the configuration pages needed to repair it, and Setup only specifies an install console.  
   **Fix:** Define a persisted failed-recovery state with visible per-instance errors and authenticated repair/retry/skip actions available before `Ready`.

3. **The launch gate ends before launch registration finishes — steps 19, 21, 29.** Shared ownership lasts only through `Process.Start`, but identity persistence happens afterward. An update can collect its restart set during that gap; the final process enumeration may prevent unsafe updating, but it cannot repair the incomplete restart set.  
   **Fix:** Hold the shared gate until the process is registered in memory and its identity is persisted, reconciling any persistence failure before allowing maintenance to collect instances.

Add tests for these three interleavings to the existing state-machine and queue tests. No files were modified.

VERDICT: REVISE
### Claude's response (round 4)

All three accepted.
1. Added a maintenance operation lock spanning the whole update/install workflow through `MaintenanceState = None`; a second Update is rejected while it is held or while any maintenance state is unresolved. The maintenance *gate* still only covers the SteamCMD-dangerous window, so restarts do not block normal launches.
2. `Ready` no longer waits for restart completion — only for install verification, process reconciliation, and hand-off to the queue. Failed recovery launches persist a per-entry error; the Dashboard shows a recovery banner with Retry/Skip and every configuration page stays reachable.
3. The shared gate is held from before `Process.Start` through in-memory registration and identity persistence, with retry on persistence failure; the update's collection step therefore always sees a complete set.
Tests for all three interleavings added to step 33.

## Round 5 — Codex

The three prior findings are addressed: maintenance workflows cannot overlap, failed recovery launches no longer block configuration access, and launch registration completes before maintenance collects processes.

One new material problem comes from the registration fix:

1. **Persistent database failure can strand a live process behind an unfinished Start — step 19.** If identity persistence repeatedly fails—for example, because the disk is full—the launch retains the shared maintenance gate indefinitely. Under the per-instance serialization rule, it also prevents Stop/Delete from acquiring the instance lock. The owner cannot stop the process through the UI, and updates cannot proceed. Retrying forever turns a storage failure into a management outage.  
   **Fix:** Bound persistence retries, retain the in-memory process registration with an explicit error state, release the operation locks, and refuse maintenance while identity remains unresolved—but permit Stop and persistence Retry.

Add a test where persistence never succeeds: Start must terminate with a visible error, Stop must remain usable, and SteamCMD must remain blocked until reconciliation or verified process exit resolves the condition.

VERDICT: REVISE
### Claude's response (round 5)

Accepted. Persistence retries are now bounded (3 over ~10 s); on exhaustion the Start ends with a visible error, the process stays registered in memory as `IdentityUnpersisted`, the shared gate and instance lock are released, Stop/Delete/"Retry persist" are allowed, Start is not, and maintenance is refused until a retry, a token/path reconciliation, or a verified exit resolves it. The requested never-succeeds test is added to step 33.

## Resolution

`MAX_ROUNDS=5` reached without `VERDICT: APPROVED`. This is **not** a substantive deadlock: round 5 raised a single finding, Claude agreed with it in full, and the plan was amended — but Codex has not re-reviewed that final edit. Every other finding across five rounds (19 + 12 + 8 + 3 + 1 = 43) was either incorporated or rejected with a logged reason; the rejections were VSS/immutable snapshots for backups, OS-level crash-injection and browser/circuit integration tests, and a coordinated cluster-wide backup quiescence — all recorded above with rationale. Handed to the owner to sign off or request a sixth round.

## Round 6 — Codex (MAX_ROUNDS raised to 6 by the owner)

The remaining finding is addressed: step 19 bounds persistence retries, releases both locks, retains process tracking, permits Stop/Retry, and blocks maintenance until resolution. Step 33 explicitly tests permanent persistence failure.

Earlier fixes remain intact. I found no new material blockers at the planning level. The Windows spikes still need to validate the documented ASA assumptions; backup consistency remains an explicitly accepted best-effort limitation.

VERDICT: APPROVED
### Resolution

`VERDICT: APPROVED` on round 6. Codex confirmed the round-5 fix and found no new planning-level blockers; it restated the two known caveats (Windows spikes must validate the ASA assumptions; backups are explicitly best-effort). Plan handed to the owner for final sign-off.
