<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: Post-0.0.1 work — quick wins before going public, future enhancements, and nevers
_Locked via grill — by Claude + heloraptr, 2026-09-13. Revised after Codex rounds 1 to 5 (the round-5 change was not re-reviewed; MAX_ROUNDS reached). **Signed off by the owner 2026-09-14.** Planning only; no
code has been written. Input was `COMPETITOR-MATRIX.md` §0/§0b and `HANDOVER.md` §12.4. `RELEASE-PLAN.md`
(the 0.0.1 release itself) is locked and is not reopened here; this plan sequences after its code work._

## Goal

Decide, row by row from the competitor gap table, which features land in one "quick-win batch" before the
first public tag, which become planned future enhancements, and which are declared out of scope for good.
The bar for a quick win, set at the start of the grill: small or medium on the S/M/L scale used below (the
whole MVP took two weekends, so "large" means a sizeable part of a weekend), no new external service
or dependency (no bot, SMTP, cloud storage, router protocol, or third-party web API), and priority for
anything that needs a schema change, because migrations are still collapsed into a single `InitialCreate`
and the owner said there is no rush to release. After the public tag every schema change is a migration
forever, so the batch also front-loads tables that later features will need (the scheduled-actions table
is the clearest case). The deliverable of this plan is the batch itself (nine items plus two open-end fixes),
an ordered future list with the shape each item should take, and a documented never list. The
never list lives in the README ("What it does not do"), the future list in GitHub issues.

## Approach

### A. Sequencing

1. Land the locked `RELEASE-PLAN.md` code work first (password hash, DPAPI-protected keys, ACLs,
   `install.ps1`, CI, version in the UI, licenses). It is security work and the smoke-test protocol
   depends on it.
2. Then the quick-win batch below (B), in the order given. Each item is its own commit with tests; the
   owner reviews each live on the VM (`deploy.ps1`) before the next starts.
3. Then collapse migrations one last time into `InitialCreate`, run both suites, run the Appendix B smoke
   protocol, and hand over to the owner to tag. A quick win that slips does not delay the tag; it drops
   to 0.0.2 with its migration, and the plan says so.

### B. Quick-win batch (before the public tag)

Order chosen so that shared pieces (the lifecycle operations in B0, the scheduler, the verification spike)
exist before their dependents.

**Implementation status (2026-09-20).** Every item below is merged to `main` (squash) unless the row says otherwise; nothing after B0 has been deployed to the live service yet.

| Item | Status | Where |
|---|---|---|
| B0 | Merged 2026-09-18 | #38–#41 |
| B1 | Done 2026-09-19 on the live box | `SPIKE-RESULTS.md` § Spike B1 |
| B2 | Merged 2026-09-18 | #42, #43 |
| B3 | Merged 2026-09-19, revised to cron with a builder dialog and a Schedule tab | #44, #45 |
| B4 | Merged and deployed 2026-10-02 (not yet smoke-tested on a real server) | #60; plan and Codex log in `b4-recon/` |
| B5 | Dropped 2026-09-19 (one box-wide `BanList.txt`, see the note on B5 and section D) | #46 docs disclaimer, #9 closed |
| B6 | Kick only, merged 2026-09-20 | #52 |
| B7 | Merged 2026-09-20 | #48 |
| B8 | Merged 2026-09-20 | #53 |
| B9 | Merged 2026-09-20 | #51 |
| B10 | Merged 2026-09-20 | #49 |
| Riding along | #27 (kill at once when `doexit` is unacknowledged) #47; #29 (inline mod add) #50; #37 (disabled mod glyph) in #45; Players page polish (badge, copy id, tooltips) #52 | closed |

**B0. Shared plumbing (L; prerequisite for B2–B6, B10).**
- **Settings mutations.** `IAppSettingsStore.UpdateAsync(Func<AppSettings, AppSettings> mutate, ct)`:
  read, mutate, and save under the store's existing semaphore; a mutation that returns an equal snapshot
  writes nothing and does not bump the version. The mutation callback is pure (no file or network side
  effects; callers project to files afterwards, see B5). `SaveAsync` (whole snapshot, Settings page)
  carries the snapshot's `Version` (a new integer key in the `AppSettings` table, incremented on every
  actual write) and is Rejected with "settings changed, reload" when the version moved.
- **Owned lifecycle operations.** `InstanceLocks` is non-reentrant and `StartAsync`/`StopAsync` acquire
  the lock themselves, so no caller may hold the lock around them. Today `InstanceDeleteService` does
  exactly that (takes the lock, then calls `StopAsync` from its job), so deleting a running instance is
  Rejected with "an operation is in progress" — a latent bug the tests miss because the command tests use
  a fake process manager. B0 fixes it (issue #10): the process manager gains internal `StopCoreAsync(session, options,
  lease)` / `StartCoreAsync(instance, kind, lease)` that run under a lease the caller already holds, and
  public operations built on them: `StopAsync`, `StartAsync` (unchanged signatures),
  `RestartWithCountdownAsync(instanceId, DateTimeOffset deadline, ct)` (one lease across countdown,
  verified stop at the absolute deadline, and queued start), and `StopUnderLeaseAsync(lease, options, ct)`
  for delete and restore. `StopOptions` gains an optional `Deadline`; the countdown broadcasts the minutes
  remaining until it and sends doexit when it passes, so tick delays and slow replies never accumulate into
  countdown drift; an in-flight command or an unavailable transport can still delay the doexit itself, and
  the console logs how late it was. Manual requests (the Stop button, `RestartAsync`) derive the deadline from
  `PreStopBroadcastMinutes` at acceptance. The existing `RestartAsync` becomes a wrapper. An integration test deletes a running fake-live instance through the
  real lock path.
- **Stop intent is recorded synchronously at acceptance.** `StopAsync` sets `session.StopIntent` under
  the lock before dispatching the job through `Task.Run`, so the liveness loop can never observe an exit
  between acceptance and the job's first line without seeing the intent. `StopRequested` keeps its current
  meaning (doexit sent). If the stop job ends without a verified exit (RCON failure and kill failure), the
  runtime stays `Stopping` with the detail text exactly as today, the session stays registered and
  supervised, and `StopIntent` stays set for the life of that session: a later exit of a process the owner
  asked to stop is a requested exit, never a crash. Every launch path (`StartAsync`, the crash policy, the
  scheduler, bulk start) rejects while a session is registered for the instance, whatever the displayed
  state, instead of relying on `HasLiveProcess` alone.
- **Session probe.** `ProbeSessionAsync(session)` returns Alive / Dead / Unknown for one session. The
  WMI enumerator gains a targeted read for one pid that reports whether the row was read completely
  (pid, executable path, and creation date all present). Alive = the row is complete and the creation
  date matches the session's start time within the same tolerance `ProcessMatcher` uses; Dead = the
  enumeration completed and no row with that pid exists, or a complete row has a creation date outside the
  tolerance; Unknown = the enumeration threw, was incomplete, or the row's identity fields were unreadable.
  Unknown never leads to a launch. `ReconcileAsync` is untouched (it skips registered sessions by design).
- **Projection reservation (non-draining).** The existing `MaintenanceGate` drains the `LaunchQueue`
  when acquired exclusively (queued launches are completed as rejected), which is right for an update and
  wrong for a file projection that takes under a second. B0 adds `ProjectionReservation` to the queue
  itself: while held, the queue does not dequeue (launches wait, nothing is rejected), and `StartAsync`
  waits on it too, so a projection can never race a launch and a bulk start is never cancelled by one.
  Holding it requires a fresh, complete process enumeration (the WMI enumerator's completeness flag)
  showing no `ArkAscendedServer.exe` process at all, matched or not; an ambiguous or unattributable game
  process, an incomplete enumeration, or any registered session in any state means the reservation is
  refused and the projection is deferred to the next tick with a logged reason.
  **Handoff contract** (round 5): the queue removes an entry before its stagger delay, so pausing the
  dequeue alone leaves an already-dequeued launch free to run. Every launch therefore follows one fixed
  order after dequeue: (1) ask the synchronizer to run a cycle and await it, holding nothing (the cycle may
  take the exclusive reservation and project); (2) acquire the reservation in *shared* mode; (3) generate
  config (the two list files are the synchronizer's, never the config writer's), create the process,
  and register the session; (4) release. The projection takes the reservation in *exclusive* mode, which
  waits for in-flight shared holders and blocks new ones. Because the synchronization request in step 1
  completes before step 2, a launch never waits on a projection while holding the shared lease, so there
  is no deadlock; and because session registration happens inside the shared phase, an exclusive holder
  that finds no session and no process is guaranteed that none is being created.

- **Exit signalling before recovery.** `HandleExitAsync` completes its cleanup and the `Exited` signal
  first, then, only for a confirmed exit (B4), posts one session-scoped recovery request to a channel the
  crash policy consumes. Nothing awaits recovery inside the exit path.
- **Probe observations.** The 15-second `ListPlayers` health probe publishes
  `IProcessManager.ProbeObserved(ProbeObservation)` with the session identity (instance id, pid, start
  time), the timestamp taken before the command was sent, and the raw reply. The manager does not depend
  on the tracker (the tracker already depends on the manager; B10 subscribes).
- **Cluster reservations.** `IInstanceLocks` gains `TryReserveCluster(clusterId)` returning a lease.
  While held, `CommandSupport` rejects creating an instance in that cluster, moving an instance into or out
  of it, and deleting the cluster; `StartAsync` rejects launches of its members. Used by B2.
- **Detached jobs and shutdown.** A small `DetachedJobs` registry (Core) tracks restore and delete jobs.
  It is registered as its own `IHostedService` (`StartupOrchestrator` has no shutdown hook today and is
  not given one): its `StopAsync` first refuses new registrations (a new restore or delete during
  shutdown is Rejected with "the service is stopping"), then waits up to 60 seconds for the registered
  jobs; `HostOptions.ShutdownTimeout` is raised to 90 seconds so the wait fits inside the host's budget,
  and the Windows service host reports stop-pending for that long. Restore's destructive phase (B2) runs
  with `CancellationToken.None`, so the wait, plus the journal, is its whole shutdown story; delete keeps
  `ApplicationStopping` as today.

**B1. Verification spike for ban and exclusive-join files (S, no code shipped).**
On the VM, with one test instance running: send `BanPlayer <test-eos-id>` over RCON and locate the file
that appears (`ShooterGame\Binaries\Win64\BanList.txt` expected; check `Saved\` as well). Determine
whether the game appends a line or rewrites the whole file, whether it keeps the file open, and whether
`UnbanPlayer` edits the file. Place a `PlayersExclusiveJoinList.txt` and launch with `-exclusivejoin` to
confirm the read location. Test whether a local edit to `BanList.txt` (made while the server runs) applies
without a restart (join with a banned test id). Then repeat `BanPlayer` with two test instances running
at once (one ban on each) and inspect the file afterwards: the multi-server case decides whether the live
RCON path in B5 is exact or best effort. Record the findings in `SPIKE-RESULTS.md` (new section).
B5 and B6 depend on this; everything else in the batch does not.

**B2. World restore from a backup (L).**
- `IBackupService.RestoreAsync(instanceId, fileName, includeCluster, ct)`. The file is resolved through
  the instance's own `BackupRecord` (never a caller-supplied path); the record's outcome must be Success.
- **Archive validation, on the one opened archive that is then extracted.** The archive must contain
  exactly one `manifest.json` (exempt from the listing rule, parsed and validated first) plus payload
  entries in a bijection with `manifest.Files` (every payload entry listed with a matching hash and
  length, every listed file present, no duplicates). Entry names are allowlisted rather than blocklisted:
  `World/<file>` or `Cluster/<seg>/.../<file>` where every segment matches
  `^[A-Za-z0-9][A-Za-z0-9 ._-]*[A-Za-z0-9_-]$` or is a single such character (no leading or trailing
  dot or space, no `:` so no alternate data streams, no backslash, no `..`), no segment is a reserved device
  name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, with or without extension), and no two
  entries differ only by case. `World/` files must match the inventory's selection patterns, and the
  archive must contain exactly one `World/<currentMapKey>.ark` (the existing `ContainsWorldFile`
  precondition, now checked against the instance's current map key); a backup of profiles only can never
  start a destructive restore. The destination directories must not be reparse points.
- **Identity.** The manifest's `InstanceSlug` must equal the instance slug and its `MapKey` the instance's
  current map key. The manifest gains `ClusterSlug` (nullable) and `ClusterCaptured` (bool). Cluster
  restore requires `includeCluster`, `ClusterCaptured == true`, the instance currently in a cluster, and
  `ClusterSlug == cluster.Slug`; a standalone target, a mismatch, or a pre-field backup disables the
  checkbox with the reason.
- **Ownership.** World-only: the target's lock. With cluster: `TryReserveCluster` first (rejected if
  membership work is in flight), then the distinct set of every member's lock (target included, once) in
  ascending id order, all-or-nothing, Rejected naming the busy one. Only then are the states re-checked:
  every locked instance must be `Stopped` or `Crashed` (B4) or the restore is Rejected naming the running
  ones. The UI's "Stop and restore" runs the normal stop job first, then calls restore. Locks and the
  reservation are held through completion or rollback.
- **Replace the complete selected set, not an overlay.** The current world directory (and the cluster
  directory when included) is copied whole to `Backups\<slug>\_restore-safety\<yyyyMMdd-HHmmss>-<n>\`
  (unique per attempt). Then every file in the world directory matching the backup inventory's selection
  (`<MapKey>.ark`, `*.arkprofile`, `*.arktribe`) plus the game's own rollback copies (`*.arkrbf`,
  `*_AntiCorruptionBackup.bak`) is deleted, and the archive's `World/` entries are extracted. For the
  cluster directory the same applies to its whole content. Rollback restores the exact previous file set:
  the directory is cleared and the safety copy is copied back. The last three safety copies per instance are
  kept, older ones pruned, except any referenced by a live journal.
- **Interrupted recovery.** From the first delete onwards the operation runs on a detached, registered
  job with `CancellationToken.None`. Before the first delete one durable operation record is written
  **outside every directory that is backed up or replaced**: `DataRoot\Data\restore-journals\<opId>.json`
  (`DataRootLayout` gains the folder), holding the safety-copy path, the target instance id, the cluster id
  and every affected instance id when cluster data is included, and the phase. It is deleted on success or
  on completed rollback. While a record references an instance or a cluster, the following are Rejected
  with "incomplete restore": launching any referenced instance (all launch paths, see B0), another restore
  of any of them, deleting any of them or the cluster, and cluster membership changes; only "Recover from
  safety copy" (re-acquires the same reservation and lock set, re-checks stopped states, runs the rollback)
  and "Discard journal" (confirm dialog, for the case where the owner resolved it by hand) are allowed, both
  from the instance and cluster pages. Safety copies referenced by a record are exempt from pruning and
  from delete's backup-directory removal. `StartupOrchestrator` logs any records found at startup; nothing
  automatic.
- **Audit.** A new `RestoreRecords` table (`InstanceId`, `CreatedAt`, `SourceFileName`, `IncludedCluster`,
  `Outcome` (Success/Failed/RolledBack), `Reason`), capped at 50 rows per instance. It is separate from
  `BackupRecords` on purpose: `BackupScheduler` uses the newest backup record of any outcome as the cadence
  marker and retention counts success rows, and a restore must affect neither. `IBackupService` gains a
  `Restored` event for the Backups tab.
- UI: a Restore button per successful backup on the instance Backups tab; a dialog with the "also restore
  cluster data" checkbox (warning text names the sibling instances; disabled with a reason when the
  identity checks fail), and "Start after restore". Default is world only. Restores are listed under the
  backups on the same tab.

**B3. Scheduled actions (L).**
- New table `ScheduledActions`: `Id`, `InstanceId?` (FK, cascade), `ClusterId?` (FK, cascade), exactly one
  set (check constraint as `IniDocuments` does), `TimeOfDay` (minutes since local midnight, 0–1439),
  `Kind` (enum `Restart`, `RconCommand`, `DinoWipe`), `Command` (text, required for `RconCommand`, ignored
  otherwise), `WarningMinutes` (0–60, default 10), `Enabled`.
- **Effective warning by kind:** `Restart` and `DinoWipe` use `WarningMinutes`; `RconCommand` has an
  effective warning of 0 whatever the column says (the editor hides the field for that kind).
- **Occurrences are explicit timestamps.** For each row and each local calendar day, the deadline is
  `TimeZoneInfo.Local` applied to (`date`, `TimeOfDay`): if that local time does not exist that day
  (spring-forward gap) the occurrence is skipped for the day and a `Skipped` run row says why; if it is
  ambiguous (fall-back overlap) the earlier instant is used. Due instant = deadline − effective warning.
  The runner's one-minute tick fires a row whose due instant falls in the tick's minute; countdowns count
  down to the absolute deadline (broadcast "in N minute(s)" from the remaining time, doexit at the
  deadline), never by adding elapsed minutes. A missed tick (service down) is skipped, never caught up.
- New table `ScheduledActionRuns`: `ScheduledActionId` (FK, cascade), `InstanceId` (FK, cascade),
  `LocalDate`, `StartedAt`, `CompletedAt?`, `Outcome` (Started/Succeeded/Failed/Skipped/Interrupted),
  `Reason`. Unique index on (`ScheduledActionId`, `InstanceId`, `LocalDate`); inserting the row is the
  atomic claim, so one cluster row fans out to one run per member instance and mixed outcomes are
  representable. On startup every `Started` row is set to `Interrupted` and nothing is replayed. Rows older
  than 30 days are pruned by the runner.
- Inheritance: a cluster's enabled rows apply to every member instance. An instance may add its own rows
  and may set `OverridesClusterSchedule` (new bool column on `Instance`, default false) to ignore the
  cluster's rows entirely, mirroring the Default/Inherit idea of launch flags at the list level rather
  than per row. The instance page shows inherited rows locked (same treatment as cluster whitelist ids).
- **Collisions.** One action per instance at a time. When a row is due while another run for that instance
  is in flight, or the instance lock is busy for any other reason, or `MaintenanceGate` is held, or the
  instance is not `Running`, a run row is written with `Skipped` and the reason. Two rows due in the same
  minute for one instance run in `Id` order; the second is skipped with "another action in progress".
- **Execution without an interactive user.** `SendRconAsync` on the facade calls the authorization guard,
  so the runner does not use it. B0's process manager and a new `IRconOperations` application service
  (session-bound execute with console echo, used by both `InstanceCommands.SendRconAsync` and the runner)
  are the runner's entry points. `Restart` = `RestartWithCountdownAsync` with the occurrence's absolute
  deadline. `DinoWipe` = countdown broadcasts of "Wild dinos will be wiped in N minute(s)" counted down
  to the same absolute deadline, then `DestroyWildDinos`, under the instance lock for the duration. `RconCommand` = `IRconOperations.ExecuteAsync` with the command text. The run row's
  outcome and reason are written when the operation returns.
- UI: "Schedule" section on the instance Settings tab and the cluster page: a list editor (time picker,
  kind dropdown, command box shown only for RconCommand, warning minutes hidden for RconCommand, enabled
  toggle), and the last ten runs with outcome and reason. The dashboard row shows the next deadline.
- **Revised 2026-09-18** after the owner reviewed the first cut: each row carries a cron expression
  (`Cron`, a standard five-field expression parsed by Cronos and described by CronExpressionDescriptor)
  instead of a time of day, so a row may fire several times a day. The run row stores `ScheduledFor` (the
  occurrence's deadline instant) instead of `LocalDate`, and the unique index on (`ScheduledActionId`,
  `InstanceId`, `ScheduledFor`) makes the claim once per occurrence. Clock changes follow Cronos' own
  rules instead of the gap-skip above: an occurrence in the spring-forward gap fires once, right after the
  transition (a 02:30 job fires at 03:00); a fixed-time occurrence in the fall-back overlap fires once, at
  the earlier instant; an expression with an interval in its hour field fires in both repeated hours. No
  `Skipped` row is written for the gap any more. The editor moves to its own Schedule tab with a builder
  dialog per row and a taller, scrollable history.

**B4. Crash detection and auto-restart (M).**
- `Instance` gains `AutoRestart` (bool, default false). New `InstanceState.Crashed` for "gave up".
- **What counts as a crash:** the liveness loop observed `HasExited == true` for a session with no
  `StopIntent` (B0), `MaintenanceGate` not held, and no delete job owning the instance. When reading
  `HasExited` throws, the loop calls `ProbeSessionAsync`: Dead is treated as a confirmed exit; Alive
  continues supervising; Unknown keeps the session registered and supervised, leaves the identity in
  place, logs once, and never permits a recovery launch (the next successful read or probe decides). A
  launch that never produces a process is not an exit and is reported by the state machine as it is now.
- **Policy:** the crash policy consumes the recovery requests posted after exit cleanup (B0). When the
  instance's `AutoRestart` is on, it counts restarts in a sliding 10-minute window per instance (in memory);
  if fewer than 3, it calls `StartAsync` (which takes the lock itself; a busy lock means someone else is
  acting and the request is dropped with a console line) and logs "Restarting after unexpected exit (n/3)";
  otherwise it sets `Crashed`, logs, and stops trying. `Crashed` clears to `Stopped` on the next manual
  Start, which also resets the window. The count is shown from the manager's runtime snapshot, not stored.
- `RuntimeChanged` carries the new state; the dashboard shows Crashed with the count in the existing state
  cell. Restore (B2) treats `Crashed` like `Stopped`.
- UI: toggle on the instance Settings tab ("Restart automatically after an unexpected exit"), default off.
- **Revised 2026-10-01 (owner delegated the plan; five Codex rounds, see `b4-recon/PLAN.md` and `PLAN-REVIEW-LOG.md`).** The give-up rule counts consecutive automatic restarts whose session ended within 10 minutes (three, then `Crashed`) instead of restarts inside a sliding 10-minute window, because a slow-loading server that dies every four minutes would never trip the window. A busy lock is retried for 2 minutes instead of dropped. A refused relaunch also becomes `Crashed` with the reason; an update, a restore reservation, or an unfinished restore skips the restart and leaves `Stopped`. `Crashed` is in memory only. The recovery state lives in the `InstanceRuntime` record (`PendingCrash`, `AutoRestarts`, `RecoveryEpoch`).

**B5. Manager-wide ban list and exclusive-join list (L, after B1).** **Dropped 2026-09-19 after spike B1** (see SPIKE-RESULTS.md § Spike B1): the game keeps one `BanList.txt` per box in the shared `ShooterGame\Binaries\Win64`, rewrites it from its in-memory list on every ban and unban, and never re-reads it, so a manager-kept list can be neither scoped per instance or cluster nor trusted on disk while a server runs. Moved to section D with a guide disclaimer; the design below is kept for the record only.
- **Desired state in the database, applied state tracked separately.** `AppSettings` gains `BanList`
  (desired set, one EOS id per line, normalized lowercase 32-hex, same codec as `AdminWhitelist`),
  `ExclusiveJoinList` (same), `BanListApplied` (the id set the app last confirmed in the file),
  `BanListPendingRemovals` (ids removed in the editor and not yet confirmed absent from the file), and the
  same two tracking keys for the exclusive-join list. All move through `UpdateAsync` (pure mutation); the
  settings `Version` (B0) is the ordering key for everything below.
- **One synchronization worker owns every file access.** `ListSynchronizer` (hosted service, one
  single-reader channel, woken by settings changes and by a one-minute tick) is the only code that reads
  or writes the two files or sends ban-related RCON. Each cycle: snapshot the desired state and its
  `Version`; do the work; then, inside one `UpdateAsync`, re-check that `Version` is still the one the
  cycle started from before updating `BanListApplied` or clearing pending removals, otherwise discard the
  cycle's results and run again. Two editor saves can therefore never be projected out of order, and a
  stale file observation can never overwrite a newer desired state. `GeneratedConfigWriter` no longer
  touches these files; it asks the synchronizer to run a cycle before a launch proceeds.
- **Rules by writer situation.**
  - *No game process anywhere* (the `ProjectionReservation` of B0 was granted: a fresh complete
    enumeration shows no game process, matched or not, and no session is registered): the worker first
    reads the existing file and imports every id present that is not in the desired set, not in
    `BanListApplied`, and not in pending removals (an in-game ban issued after the last tick, or a
    pre-existing file on first use) into the desired set; only then does it project the resulting desired
    set to each file (temp-and-rename), read it back, and record it as applied; pending removals whose ids
    are now absent are cleared. Import-then-project means stopping the last server can never erase a ban
    the app had not yet observed.
 This is the only situation in which removals are confirmed from the file and the
    only situation in which the app writes either file.
  - *At least one game process running:* the app never writes either file. Desired adds not yet applied
    are sent as `BanPlayer <id>` to one running instance; pending removals as `UnbanPlayer <id>` to every
    running instance, both best effort and logged, and both qualified in the editor as "applied through the
    running server" until B1's multi-server test shows how several processes writing one file behave. The
    file is then read for confirmation of adds only.
- **Reading the live file.** A live read is trusted only when two consecutive reads (one second apart)
  are byte-identical; otherwise the observation is discarded and retried next tick. From a trusted live
  read the worker imports additions only: ids in the file, not in the desired set, not in
  `BanListApplied`, and not in pending removals are in-game bans and are added to the desired set (and to
  `BanListApplied`). Removals are never inferred from a live read; a missing id while a game runs is
  ignored until the no-writer projection. Pending removals take precedence over every import branch: an id
  in pending removals is never re-imported, whatever the file says, until the no-writer projection
  confirms its absence. An explicit re-ban in the editor of a pending-removal id clears the pending
  removal and treats the id as a desired add.
- **Failure handling.** A failed database save leaves `BanListApplied` and the pending set unchanged, so
  the next cycle repeats the work; nothing is ever inferred from a half-applied cycle. No-op cycles write
  nothing and do not bump the version.
- **Exclusive join.** `Instance` gains `ExclusiveJoin` (bool, default false) which emits `-exclusivejoin`
  in the launch builder. The game reads the list at launch, so changes apply "after all instances stop and
  synchronization completes"; the editor says exactly that. A launch of an instance with `ExclusiveJoin`
  on while the exclusive-join list has unapplied changes is Rejected with that reason (a stale allow-list
  is a security decision, so it is not a warning), and the launch preview shows the same text.
- Location and format follow B1: `<eosId>,<name-if-known>,0` for the ban list, bare ids for the
  exclusive-join list. Because `Binaries\Win64` is shared, the files are manager-wide by construction; a
  cluster- or instance-level list is a documented future enhancement (F8).
- UI: two `WhitelistEditor` instances on Settings ("Ban list", "Exclusive-join list") with an applied /
  pending marker per row and the reason for pending; the exclusive-join toggle on the instance Settings
  tab. Ban list rows show the name from `KnownPlayers` when present (display only; the id is the identity).
- **B1 findings that change this section:** if `UnbanPlayer` does not edit the file, removals stay
  pending until the no-writer projection and the editor says so. If ASA reads the list from `Saved\` per
  instance, B5 becomes per-instance with a manager-wide union and the plan is amended before B5 starts.

**B6. Kick and Ban buttons (S, with B5).** **Revised 2026-09-19:** Ban is dropped with B5; a Kick button on its own (`KickPlayer <eosId>` through `IRconOperations`, behind a confirm dialog) remains a small item.
On the instance Players tab, per online player: Kick (`KickPlayer <eosId>` through `IRconOperations`) and
Ban, each behind a confirm dialog. Ban adds the id to the desired set through `UpdateAsync`, then B5's
projection sends `BanPlayer <eosId>` on that instance (the game drops the player and writes the file) and
confirms the add from the file. Unban is removal in the ban list editor: the id moves to pending removals,
B5 sends `UnbanPlayer` to running instances best effort (shown as pending), and the removal is confirmed
only by the next no-writer projection, exactly as B5 states.

**B7. Resource telemetry, current values only (S).**
`ProcessManager.RunLivenessAsync` samples `Process.WorkingSet64` and computes CPU percent from
`TotalProcessorTime` deltas over the 2-second tick, divided by `Environment.ProcessorCount`. Published
through a separate `IProcessManager.TelemetryChanged` event at most every 5 seconds per instance.
`RuntimeChanged` already fires on every successful probe (it carries `LastRconSuccessAt`), so telemetry
stays off it to avoid doubling that rate and to keep the two consumers separable. Dashboard row and
instance header show "RAM 6.2 GB · CPU 14 %". Nothing stored.

**B8. Mod update badge (S).**
`ModLibraryEntry.DateModified` already exists and is refreshed by the manual Refresh. Extract the metadata
refresh from `ModCommands` into an application service (`ModMetadataRefresher`) used by the authorized
command facade and by a new daily hosted poll (runs only when a CurseForge key is set; the poll has no
interactive user and must not go through the guard). An instance is "changed since last launch" when any
mod it will load (instance mods, cluster mods, and the map's `ModId`) has `DateModified >
Instance.LastLaunchedAt`; the badge reads "Mod metadata changed since the last launch", shows on the mod
card, the instance row, and the instance header, and clears on the next launch. No new column: launching
is the acknowledgment. The wording is deliberately conservative: `LastLaunchedAt` is written when the
process is created, so the app knows a launch happened, not that the mod installed.

**B9. Connection section (S).**
`AppSettings` gains `PublicAddress` (free text, host or IP, may be empty). The instance page gets a
"Connection" card: the box's non-loopback IPv4 addresses, game port and RCON port, whether the firewall
rule `ArkAscendedServerAdmin-<id>` exists (query through `FirewallRules`), and copyable strings
`open <lan-ip>:<port>` and, when `PublicAddress` is set, `open <public>:<port>`. No outbound calls.

**B10. Open ends riding along (S).**
- **Player tracker fed by probe observations.** The tracker subscribes to `ProbeObserved` (B0) and
  applies each observation through its single-reader channel: it drops observations whose session is not
  the instance's current live session, and applies a listed-online / missing-offline snapshot only to
  players whose newest evidence (`LastSeenAt`, from log lines or a later snapshot, on any instance) is
  older than the observation's sent-at timestamp, so a delayed reply never overwrites newer presence
  evidence, including a transfer to another instance. `RecordListedAsync` takes an observation (session
  identity and sent-at) instead of a bare reply; `PlayerCommands` captures the session identity before it
  sends `ListPlayers`, not after. This reconciles at re-attach (the first probe after attach) and
  continuously afterwards.
- Prune skipped and failed `BackupRecord` rows: after each backup, keep at most 50 non-success rows per
  instance (oldest deleted). Restore records are a separate table (B2) and are capped there.

### C. Future enhancements (planned, in priority order; tracked as GitHub issues with the `enhancement` label and a milestone)

**Versioning (owner decision 2026-09-13):** the first public tag is `v1.0.0` and ships the quick-win batch;
fixes are `v1.0.x`; F1–F5 target milestone `v1.1.0`; F6–F9 target `v1.2.0`; rehearsal tags are
`v1.0.0-rc.N`. `RELEASE-PLAN.md` still says `v0.0.1` and is updated in a later pass, not here.

- **F1 (#1). Update-available notice** (already decided in `RELEASE-PLAN.md`): daily check of the GitHub
  releases API, banner with version and the `install.ps1` upgrade command. In-app self-update: never.
- **F2 (#2). Notification layer + Discord webhook.** `INotificationSink` fed by the existing events
  (`RuntimeChanged`, `Recorded`, `Restored`, `IUpdateService.Changed`, `IPlayerTracker.Changed`,
  scheduled-action run outcomes, crash restarts). Per-sink event selection, a rate limiter for chatty
  events (joins), a masked URL field. The webhook URL is stored like the CurseForge key (plaintext in the
  database, per the release plan's decision that restore on another machine must keep working); if that
  decision is reopened it is reopened for both. Discord webhook first, SMTP email later.
- **F3 (#3). Auto-update on a new game build, manager-wide.** Settings: check interval and warning window.
  Detection by SteamCMD `app_info_print 2430930` (local, slow, no third-party site). On a new build, warn
  every running instance, then run the existing `UpdateService`, which already restarts the instances
  that were running. Documented as stopping every instance on the box because the install is shared.
- **F4 (#4). Off-site backup as copy-to-folder.** One setting (a second folder, UNC allowed); each verified zip
  is copied there after the atomic move; failures are recorded on the `BackupRecord` reason. No SFTP or
  S3 code, ever.
- **F5 (#5). Light mode.** Global setting; second token set on `[data-theme="light"]`; Radzen light theme swap;
  dark stays the default. After the batch has settled the UI.
- **F6 (#6). Multi-user, viewer role first, OAuth later.** Local accounts with Admin and Viewer roles, per-user
  hashes, permission checks in the command facades, then OAuth (Discord or GitHub) as a login method.
  Needs its own grill. Not 0.0.2.
- **F7 (#7). Telemetry history** (ring buffer, sparklines) if F2 or the dashboard asks for it.
- **F8 (#9). Cluster- or instance-level ban and exclusive-join lists** if a use case appears; requires F9.
- **F9. Finer-grained `Binaries\Win64` junction** (real `Win64` directory, junctioned contents) as the
  enabler for F8 and per-instance AsaApi; a layout migration for existing instances.

### D. Never (documented as out of scope in the README's "what it does not do" list)

Localization (English only; reconsider only if a contributor offers a full translation). Discord bot.
In-app self-update. UPnP. AsaApi plugin management (README notes the shared `Binaries` folder means a
hand-installed loader lands on every instance; untested). Remote agents across machines. Tribe and player
data tools. Map rotation. SFTP/S3 backup targets. A third-party WAN or reachability probe. Ban and exclusive-join list management (spike B1, 2026-09-19: one `BanList.txt` per box in the shared `ShooterGame\Binaries\Win64`, rewritten from memory on every ban or unban and never re-read, so a ban sent from any instance console applies to every server on the box; the guide says so and the manager leaves the file alone).

## Key decisions & tradeoffs

- **Schema-first, because there is no rush.** The owner chose to front-load schema-bearing features so the
  first public database is as complete as the near-term roadmap needs. The clearest consequence is B3: a
  generic `ScheduledActions` table with a per-instance runs table now, rather than restart columns on
  `Instance` that a 0.0.2 table would have to migrate into.
- **Lifecycle ownership lives in the process manager** (B0). The scheduler, the crash policy, delete, and
  restore never hold an instance lock around the public `StartAsync`/`StopAsync`; they use operations that
  own or accept a lease. Simpler than making the locks reentrant, and it fixes the delete-while-running bug.
- **Scheduled actions are generic and cluster-aware from day one.** Three kinds now; later kinds are new
  enum values, not schema. Cluster rows inherit into instances with a per-instance opt-out flag rather
  than per-row Default/Inherit, to keep the editor simple. Every due row leaves a run row, including skips
  and interruptions; deadlines are absolute instants with a stated DST policy.
- **Crash = confirmed unrequested exit, never the exit code** (`doexit` returns -1 every time). Stop intent
  is recorded at acceptance so a death during a countdown is not a crash; an uncertain death is probed by
  identity and never restarted while unknown. Bounded at 3 in 10 minutes; default off, because a config
  that crashes at boot would otherwise relaunch a 12 GB game three times before stopping.
- **Ban and exclusive-join lists are manager-wide** because `Binaries\Win64` is shared and a home cluster
  wants one list; the junction is not reworked. The database holds the desired set, one synchronizer owns
  every file access, the app writes the files only when no game process exists (under the launch gate)
  and applies changes live through RCON otherwise, removals are never inferred from a live read, and
  applied versus pending is tracked and shown, so no ban is lost to a write race and no failed change is
  silently committed.
- **Restore replaces the whole selected save set** (never an overlay), is world-only by default with
  cluster data opt-in behind identity checks, a cluster reservation, and sibling locks, runs its
  destructive phase uncancellable with an operation record kept outside every replaced directory that blocks
  every affected launch, delete, restore, and membership change until resolved, and records to its own table so backup cadence and retention are untouched.
- **Mod-update acknowledgment is the next launch**, not a button, and the badge says "metadata changed",
  because the app knows a launch happened, not that a mod installed.
- **Telemetry is two live numbers on their own event**, no history.
- **Reachability is local facts plus a manual public address**, because a WAN lookup needs an outside
  endpoint and a real UDP reachability test needs a third party nobody offers for free.
- **Notifications wait for a layer** rather than shipping four ad-hoc webhook subscriptions the layer
  would replace.
- **Multi-user stays after 0.0.2**; the single-password model is the documented design.

## Risks / open questions

- B1 may find that ASA reads the ban list from `Saved\` per instance, which would make a per-instance ban
  list possible without junction work. If so, B5 becomes per-instance with a manager-wide union (like the
  admin whitelist) and the plan is amended before B5 starts; the schema for that case is one text column
  on `Instance` and `Cluster` each, added in the same batch.
- B1 may find that `UnbanPlayer` does not edit the file, or that local edits are read only at connection
  time or only at restart; the editor's pending markers and help text follow the finding.
- B2's handling of `*.arkrbf` and `*_AntiCorruptionBackup.bak`: deleted on restore so the game cannot
  prefer a newer rollback copy. Whether the game ever loads an `.arkrbf` automatically is checked on the
  VM during B2; if it never does, the deletion is harmless anyway.
- B2's journals and launch refusal add states the smoke test must cover (kill the service mid-restore,
  restart, recover; a cluster restore interrupted with siblings present).
- B3's DST policy is tested at both transitions with a fake clock; the runs table is the once-per-day
  guard; `Interrupted` marking at startup is tested with a seeded `Started` row.
- B4's window counter is in memory, so a service restart resets it; acceptable.
- B5 depends on B1 more than any other item; its section is the one most likely to be amended. Its live
  path (RCON to running servers) is best effort until B1 tests how several processes writing one shared
  file behave; the no-writer projection is the only path that is exact.
- B10's "newest evidence wins" rule means a probe snapshot cannot mark a player offline if a log join line
  on any instance is newer than the snapshot's sent-at time; that is the intended behavior for transfers.
- The quick-win batch is four large items (B0, B2, B3, B5), one medium (B4), and six small; the owner
  accepted that there is no rush. Any item can
  be dropped to 0.0.2 by the owner without changing the others, except B6 (needs B5), and B2–B4 and B10
  (need B0).

## Out of scope

- Everything in section D, and every future item in section C, for the quick-win batch.
- Reopening any `RELEASE-PLAN.md` decision (password hash, key protection, ACLs, installer, CI, single
  password, plaintext CurseForge key).
- Per-instance ban or exclusive-join lists, junction layout changes, AsaApi, UPnP, external IP lookups.
- Automatic restarts on mod updates (the badge only; a "restart when mods update" action is a possible
  later scheduled-action kind).
- Telemetry storage, charts, or alerts.
- Creating or pushing any git tag; the owner tags.
