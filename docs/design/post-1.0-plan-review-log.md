<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: Post-0.0.1 work (quick wins before going public, future enhancements, nevers)
Act 1 (grill) complete — plan locked with the user on 2026-09-13. MAX_ROUNDS=5.
Plan file: `docs/.untracked/RELEASE-PLAN-FUTURE.md`. Codex 0.153.4, read-only sandbox, skills disabled, stdin closed.

## Act 1 summary (grill answers, in order)
1. Quick-win bar: day-sized, no new external deps, schema-first (migrations still collapsible; no rush to release).
2. Sequencing: release-plan code first, then the batch, then one final migration collapse, then the owner tags.
3. World restore: quick win; world by default, cluster data opt-in with warning.
4. Tool self-update: update notice in 0.0.2 (already locked), in-app self-update never.
5. Crash restart: quick win, per-instance toggle default off, 3 in 10 minutes then Crashed.
6. Discord webhooks: future, on a notification layer; URL stored like the CurseForge key.
7. Localization: never.
8. Scheduled restarts: quick win, cluster-level too; then generalized (answer 11).
9. Auto-update on new build: future, manager-wide poll via app_info_print.
10. Telemetry: quick win, current values only.
11. Scheduled actions: one generic table and all three kinds (Restart, RconCommand, DinoWipe) now — supersedes 8.
12. Discord bot: never.
13. Ban/exclusive-join lists: quick win, manager-wide, verify on the VM first; cluster/instance level documented as a possible future enhancement.
14. Kick/ban buttons: quick win, bundled with the lists.
15. Mod update detection: quick win, badge only.
16. Off-site backup: future as copy-to-folder; email: future sink.
17. Reachability: quick win, local facts plus a manual public address.
18. UPnP: never.
19. Multi-user/roles/OAuth: future, viewer role first, OAuth later, not 0.0.2.
20. AsaApi: never manage; document the shared folder.
21. Remote agents, tribe tools, map rotation: never.
22. Light mode: future.
23. Restore scope: world by default, cluster opt-in with warning.
24. Open ends riding along: tracker reconcile at attach; prune skipped/failed backup records. Persisting the last update outcome: not included.

## Round 1 — Codex (thread `<thread-id>`)

The plan still has material problems. I reviewed `RELEASE-PLAN-FUTURE.md` against the implementation; I did not review the locked release plan or modify files.

1. **B2 — Hash verification is not safe extraction.** [BackupService.Verify](../../src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupService.cs) checks listed hashes but permits unlisted archive entries and does not validate extraction paths. Reusing it alone leaves traversal, Windows path aliases, and destination junctions unaddressed.
   **Fix:** Resolve the source through its instance-owned record, reject unsafe/duplicate/unlisted entries and reparse-point destinations, and verify and extract the same opened archive.

2. **B2 — The manifest can describe the wrong restore target.** `ContainsWorldFile` checks against the manifest’s own `MapKey`, not the instance’s map; `BackupManifest` also contains no cluster identity or distinction between absent and empty cluster data.
   **Fix:** Validate instance/map identity and explicitly record cluster identity and capture presence before allowing cluster restore.

3. **B2 — Overlay restore produces a mixed-time save, and overlay rollback is incomplete.** Leaving files absent from the archive preserves newer profiles, tribes, and cluster files; copying the safety directory back also leaves files newly introduced by a failed restore. `BackupInventory` excludes both `.arkrbf` and `_AntiCorruptionBackup.bak`, while the plan addresses only the former.
   **Fix:** Stage and replace the complete selected save set, define cleanup for both rollback-file types, and make rollback restore the exact previous file set.

4. **B2 — Checking stopped siblings does not keep them stopped.** Only the target instance is locked, so a sibling can start—or another sibling restore can begin—after the cluster-state check and during shared-directory replacement.
   **Fix:** Reserve cluster membership and acquire all affected instance locks in a deterministic order, then recheck stopped states and hold those reservations through recovery.

5. **B2 — “On any failure, copy back” does not cover interrupted recovery.** Caller cancellation, service termination, disk exhaustion, or rollback failure can leave a partially restored directory; reporting merely `Rejected` does not establish whether starting is safe.
   **Fix:** Use a durable restore journal, unique safety directories, cancellation-independent recovery, and a startup interlock until incomplete restores are recovered or explicitly resolved.

6. **B3 — One cluster row cannot store per-instance execution state.** A single `LastRunAt`/`LastOutcome` either lets the first member suppress the remaining members or allows competing completions to overwrite each other; mixed success and failure cannot be represented.
   **Fix:** Add execution records keyed by schedule, instance, and scheduled local date, with an atomic claim and individual outcomes.

7. **B3 — Simultaneous actions and countdown timing are undefined.** Two rows due for one instance collide with the per-instance in-flight guard; one may silently disappear. A 03:00 restart with ten warning minutes currently starts its countdown at 03:00, making the actual restart 03:10.
   **Fix:** Define whether the configured time means warning start or action time, and specify deterministic collision handling with persisted skipped outcomes.

8. **B3/B4 — Locking cannot simply wrap the existing lifecycle methods.** [InstanceLocks](../../src/ArkAscendedServerAdmin.Core/Processes/InstanceLocks.cs) is non-reentrant, and `StartAsync`/`StopAsync` acquire it themselves; holding the scheduler’s lock makes those calls reject, while merely checking availability leaves a race. `LaunchQueue` supplies pacing, not lifecycle ownership or deduplication.
   **Fix:** Add a process-manager operation that owns the reservation across countdown, verified stop, and queued start, with internal methods that reuse that ownership.

9. **B4 — The stop-countdown assumption is wrong.** [RunStopJobAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs) sets `StopRequested` **after** the countdown. An exit during a manual stop countdown therefore qualifies for auto-restart under this plan.
   **Fix:** Record stop intent when the stop operation is accepted and suppress recovery for that session throughout the operation.

10. **B4 — Auto-restart is being added to a path that also handles uncertain death.** `RunLivenessAsync` treats `HasExited` access exceptions as “gone”; automatically launching from that path can create a second process. `HandleExitAsync` also completes its exit signal only after cleanup, so awaiting recovery inside it can interfere with stop completion.
    **Fix:** Require confirmed death or successful reconciliation, finish exit cleanup/signaling first, then submit one session-scoped recovery request and record its actual outcome.

11. **B5/B6 — Union-on-save makes removal unreliable and does not prevent lost bans.** Removing an ID from settings and then unioning with the existing file immediately restores it; with no running instances, the proposed RCON unban cannot remove it. A game append between read and replacement can also disappear without any sharing violation.
    **Fix:** Use explicit add/remove operations with durable removal intent, serialize manager mutations, and defer whole-file replacement until game writers are stopped unless B1 proves safe live coordination.

12. **B6 — Updating a ban can overwrite unrelated settings.** [AppSettingsStore.SaveAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Data/AppSettingsStore.cs) writes an entire settings snapshot; its semaphore protects individual calls, not a caller’s read–modify–write sequence. Concurrent bans or a Settings-page save can erase another change.
    **Fix:** Add atomic field-level settings mutations or optimistic concurrency, and use them for moderation changes.

13. **B8 — The proposed hosted-service reuse crosses an authorization boundary.** [ModCommands.RefreshMetadataAsync](../../src/ArkAscendedServerAdmin.Server/Commands/ModCommands.cs) requires an authorized caller, and its upsert helper is private; a background poll has no interactive authenticated user.
    **Fix:** Extract metadata refresh into an application service shared by the authorized command facade and the hosted worker.

14. **B8 — The badge overstates what the code knows and omits map mods.** `LastLaunchedAt` is persisted immediately after process creation, before successful startup or confirmed mod installation; even a crashing launch clears the proposed badge. The launch builder includes `Map.ModId`, which the badge’s instance/cluster assignment rule misses.
    **Fix:** Include map mods and label this conservatively as “metadata changed since launch,” or track confirmed installed versions before claiming an update is pending or applied.

15. **B2/B10 — Restore events conflict with backup scheduling and retention semantics.** [BackupScheduler](../../src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupScheduler.cs) uses the newest record of **any** outcome, so restoring postpones the next backup. `Restored` rows escape both existing success-only archive retention and the proposed non-success cap, growing indefinitely.
    **Fix:** Separate restore audit events from backup attempts, or explicitly exclude them from backup cadence and give them independent bounded retention without archive deletion.

16. **B10 — Attach reconciliation needs session identity, not just `RuntimeChanged`.** That event also fires for every successful probe and will fire every two seconds with B7. A delayed `ListPlayers` result can arrive after exit or restart and mark players online for an obsolete process; `RecordListedAsync` has no session check.
    **Fix:** Reconcile once per process identity using the first successful existing probe response, and discard results whose session has ended.

VERDICT: REVISE

### Claude's response (round 1)

Accepted 16 of 16, most in the form Codex proposed; two accepted in a lighter form with the reason logged.

- 1–5 (B2 restore): accepted. Archive resolved through the instance's own record; every entry must be listed and hashed, unsafe names refused, destinations checked for reparse points, verify and extract on the one opened archive. Manifest gains `ClusterSlug` and `ClusterCaptured`; slug and map key must match the instance. Whole selected set replaced (world files plus `.arkrbf` and `_AntiCorruptionBackup.bak`), rollback restores the exact previous set from a unique safety copy. All affected instance locks taken in id order before the state re-check. Destructive phase runs detached with `CancellationToken.None` plus a `restore.journal` that blocks launch until recovered; recovery is a button, not automatic (lighter than a full durable journal with automatic startup recovery — the owner is a single admin and an explicit "Recover" beats a silent one).
- 6–8 (B3/B4 scheduling and locks): accepted. `ScheduledActionRuns` table keyed (action, instance, local date) as the atomic claim; `TimeOfDay` is the action time and the countdown starts earlier; collisions are recorded as `Skipped` runs. New B0 adds `RestartWithCountdownAsync` on the process manager so no caller holds `InstanceLocks` around `StartAsync`/`StopAsync`.
- 9–10 (B4 stop intent and uncertain death): accepted, verified in `RunStopJobAsync` that `StopRequested` is set after the countdown. B0 adds `StopIntent` at acceptance; exit cleanup and signalling complete before a session-scoped recovery request is posted; an exception-inferred exit runs reconciliation first and only a negative result counts.
- 11–12 (B5/B6 ban list merge and settings store): accepted. Three-way merge against `BanListLastWritten` under one lock inside a new `IAppSettingsStore.UpdateAsync`; Settings-page saves carry a version and are rejected when stale. The millisecond append race is documented as residual; B1 decides whether the game appends or rewrites.
- 13–14 (B8): accepted. `ModMetadataRefresher` service shared by the facade and the poll; the map's `ModId` is included; badge reads "metadata changed since the last launch".
- 15 (restore vs scheduler/retention): accepted with a separate `RestoreRecords` table instead of a new `BackupOutcome` value.
- 16 (B10 attach reconcile): accepted with a stronger design — the existing 15-second `ListPlayers` health probe feeds the tracker, tagged with the session identity, and stale replies are dropped. Also addressed B7's effect on `RuntimeChanged` by giving telemetry its own throttled event.

## Round 2 — Codex

The revision resolves several findings, but restore recovery, crash handling, and ban synchronization still have material gaps. No files were modified.

Status of the original 16 findings:

| # | Finding | Assessment |
|---|---|---|
| 1 | Safe archive extraction | **Partial:** stronger validation, but manifest handling and Windows paths need correction. |
| 2 | Restore-target identity | **Partial:** fields added; explicit cluster-identity matching is missing. |
| 3 | Overlay restore/rollback | **Addressed:** exact-set replacement and rollback specified. |
| 4 | Cluster restore ownership | **Partial:** sibling locks added, but membership and recovery remain unprotected. |
| 5 | Interrupted restore | **Partial:** journal added, but its interlock covers only the initiating instance. |
| 6 | Per-instance schedule claims | **Addressed:** separate runs and uniqueness constraint. |
| 7 | Schedule collisions/timing | **Partial:** collisions defined; warning arithmetic and DST semantics still conflict. |
| 8 | Lifecycle lock ownership | **Addressed for restart:** B0 owns the sequence; an existing delete caller still violates the contract. |
| 9 | Stop intent | **Partial:** setting it inside the dispatched task is later than acceptance. |
| 10 | Confirmed exit/recovery ordering | **Partial:** ordering fixed; proposed reconciliation does not work as assumed. |
| 11 | Ban removals/concurrent writes | **Partial:** ordinary removal improved; write races remain and crash consistency is missing. |
| 12 | Concurrent settings changes | **Addressed in principle:** versioned saves and atomic mutations. |
| 13 | Background mod-refresh authorization | **Addressed:** service extraction specified. |
| 14 | Mod badge meaning/map mods | **Addressed:** conservative wording and map mods included. |
| 15 | Restore audit versus backup retention | **Addressed:** separate table and cap. |
| 16 | Player reconciliation identity | **Partial:** session tagging helps, but timing and dependency issues remain. |

Concrete changes still needed:

1. **B2’s “every entry must be listed” rule rejects existing backups.** [BackupService.cs:156](../../src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupService.cs:156) writes `manifest.json` into the archive, but that file is not itself in `manifest.Files`. Also, rejecting rooted paths and `..` does not cover Windows alternate data streams, reserved device names, or trailing-dot/space aliases.

   **Fix:** Exempt exactly one validated `manifest.json`, require a bijection between payload entries and manifest files, and allowlist canonical Windows-safe paths under the permitted inventory roots.

2. **Adding cluster identity is insufficient without comparing it.** B2 requires matching instance and map identifiers but only requires cluster identity to *exist*. It never explicitly requires `ClusterCaptured == true` and `ClusterSlug == currentCluster.Slug` before replacing the current cluster directory.

   **Fix:** Require an affirmative cluster capture and exact current-cluster identity match; reject standalone targets and mismatches.

3. **B2’s lock acquisition is internally inconsistent and does not freeze membership.** It first acquires the target lock, then acquires “every instance” in ascending order—including the target unless explicitly excluded. [InstanceLocks.cs](../../src/ArkAscendedServerAdmin.Core/Processes/InstanceLocks.cs) is non-reentrant. Separately, [InstanceCommands.cs:313](../../src/ArkAscendedServerAdmin.Server/Commands/InstanceCommands.cs:313) permits creating a new cluster member without participating in those locks; that new member could start during restore.

   **Fix:** Reserve the cluster against membership changes and launches, then acquire the distinct complete instance-lock set once in ascending order.

4. **An interrupted cluster restore still allows siblings to start against partially restored data.** The journal is under only the initiating instance, and only that instance’s launch is refused. After service restart, the in-memory sibling locks are gone. The recovery button also has no explicit requirement to reacquire sibling locks and verify stopped states.

   **Fix:** Persist an interlock for the affected cluster and require every member launch, recovery, and journal-discard operation to honor it; protect referenced safety copies from pruning or deletion.

5. **B0 records stop intent after acceptance, leaving the original race in a smaller window.** [StopAsync:565](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:565) dispatches `RunStopJobAsync` through `Task.Run`. The liveness loop can observe an exit between dispatch and the task setting `StopIntent`.

   **Fix:** Set intent synchronously while accepting the operation under the instance lock, before dispatching the job, and define how failed stop operations clear or retain that intent.

6. **B4 cannot reuse the existing reconciliation as described.** [ReconcileAsync:465](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:465) skips every instance already in `_sessions`; the session whose `HasExited` read failed is still registered. The method returns no positive/negative result, and enumeration failure can mark every instance `Unknown`.

   **Fix:** Add a targeted session-identity probe returning alive/dead/unknown; keep unknown sessions supervised without clearing identity or permitting recovery launches.

7. **The claim that existing lifecycle callers keep working overlooks delete.** [InstanceDeleteService.cs:63](../../src/ArkAscendedServerAdmin.Infrastructure/Maintenance/InstanceDeleteService.cs:63) acquires the instance lock, then its job calls `StopAsync`, which tries to acquire it again. Running-instance deletion therefore hits exactly the non-reentrancy problem B0 describes. The same service also uses `ApplicationStopping` inside its job, so it is not an example of an uncancellable destructive operation.

   **Fix:** Include delete in the ownership refactor and specify restore’s shutdown/job-tracking behavior independently of the existing delete implementation.

8. **B3 applies warning subtraction to commands whose warning field is “ignored.”** Its universal due-minute formula subtracts `WarningMinutes` even for `RconCommand`, whose default is ten. DST also breaks the promise of action time: a 02:05 action with ten warning minutes can begin at 01:55 and execute at 03:05 across a spring-forward transition.

   **Fix:** Derive effective warning minutes by kind, resolve each occurrence to an explicit timestamp with a documented DST policy, and count down to that deadline rather than adding elapsed minutes.

9. **Scheduled RCON commands repeat the authorization mistake fixed for mod refresh.** The existing [SendRconAsync:198](../../src/ArkAscendedServerAdmin.Server/Commands/InstanceCommands.cs:198) calls `guard.EnsureAuthorizedAsync`; the hosted runner has no interactive user. Runs interrupted by shutdown also remain `Started`, with no reconciliation specified.

   **Fix:** Extract a session-bound RCON application service for both callers and mark abandoned `Started` runs as interrupted on startup without replaying destructive commands.

10. **B5’s file replacement and database update are not atomic, even inside `UpdateAsync`.** Example: settings and merge base contain ban X; unban removes X from the file; the database save fails. On the next merge, the file’s missing X looks like a game-originated removal, so the failed operation is silently committed. A failed add similarly becomes an imported game ban. [AppSettingsStore.cs](../../src/ArkAscendedServerAdmin.Infrastructure/Data/AppSettingsStore.cs) provides a database write and cache update, not a transaction spanning the filesystem.

    **Fix:** Persist desired bans and pending synchronization intent first, then project to the file with recoverable completion tracking; keep filesystem side effects out of the settings mutation callback.

11. **B5 still accepts silent loss of a security decision, and its deferred-write fallback is incomplete.** Documenting an append lost between read and rename does not preserve the ban. When live writes are deferred, B6’s unban leaves the old file entry present; the next in-game ban may rewrite that stale entry, and line-based comparison can reinterpret a changed name as a new ban.

    **Fix:** Defer whole-file replacement while any game writer may exist, merge by normalized EOS ID, retain explicit removal intent, and distinguish desired state from applied state in the UI.

12. **B10 introduces a dependency cycle and does not establish observation ordering.** [PlayerTracker.cs](../../src/ArkAscendedServerAdmin.Infrastructure/Players/PlayerTracker.cs) already depends on `IProcessManager`; injecting the tracker back into the manager creates a constructor cycle. Its current `RecordListedAsync` stamps rows with processing-time `now`, while log events older than `LastSeenAt` are discarded. A delayed poll can therefore overwrite newer presence evidence—even from another instance during transfer. [PlayerCommands.cs](../../src/ArkAscendedServerAdmin.Server/Commands/PlayerCommands.cs) must capture identity before sending, not obtain “the current one” after receiving.

    **Fix:** Publish probe observations through an event/channel, capture session identity and observation timing at request execution, and apply snapshots only when they do not supersede newer player evidence.

Two smaller corrections: B7’s claim that `RuntimeChanged` means only state/identity changes is already false—[OnProbeSucceededAsync:835](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:835) publishes `LastRconSuccessAt` updates—and B5’s minute-by-minute merge should avoid no-op writes/version increments, otherwise an unchanged ban file can repeatedly invalidate an open Settings form.

VERDICT: REVISE

### Claude's response (round 2)

Verified two of Codex's code claims before acting: `InstanceDeleteService` takes the instance lock and its job then calls `StopAsync`, which `TryAcquire`s again, so deleting a running instance is Rejected today (a real latent bug; the command tests use a fake process manager). `ReconcileAsync` skips registered sessions. `PlayerTracker` already injects `IProcessManager`.

Accepted 12 of 12 plus both smaller corrections:

- 1–2: allowlisted entry names (segment regex, reserved device names, no `:`/ADS, no case-only duplicates), one exempt validated `manifest.json`, bijection with the manifest; cluster restore requires `ClusterCaptured` and an exact `ClusterSlug` match with the instance's current cluster.
- 3–4: B0 adds a cluster reservation on `IInstanceLocks` that blocks membership changes and member launches; the distinct lock set is taken once in id order. Journals are written in the instance directory and, for cluster restores, the cluster directory; `StartAsync` refuses any member while either exists; recovery re-acquires the full set; referenced safety copies are exempt from pruning.
- 5: `StopIntent` set synchronously under the lock before `Task.Run`; cleared when the stop job ends without an exit.
- 6: new `ProbeSessionAsync` (Alive/Dead/Unknown by pid + start time through the WMI enumerator); Unknown keeps supervising with identity intact and no recovery.
- 7: B0 now includes delete in the lease-based refactor (`StopUnderLeaseAsync`), a `DetachedJobs` registry with a 60-second shutdown wait, and an integration test for deleting a running instance through the real lock path.
- 8: effective warning by kind (0 for RconCommand); occurrences resolved to explicit instants with a DST policy (gap: skip with a Skipped row; overlap: earlier instant); countdown to the absolute deadline.
- 9: `IRconOperations` application service shared by the facade and the runner; `Started` runs marked `Interrupted` at startup, never replayed.
- 10–11: redesigned B5 around desired state (database) vs applied state: the app never writes the file while a game process runs; adds/removes apply via RCON to running instances and are confirmed from the file; pending removals are tracked; import compares by normalized EOS id only; a failed save leaves `BanListApplied` untouched so nothing is silently committed; no-op ticks do not bump the version. This removes the residual race entirely instead of documenting it.
- 12: manager publishes `ProbeObserved` (session identity, sent-at, reply); the tracker subscribes (no cycle) and applies snapshots only over older evidence; `PlayerCommands` captures identity before sending.
- Smaller: B7 corrected (`RuntimeChanged` already fires per probe); B5 no-op writes avoided.

## Round 3 — Codex

The revision addresses much of round 2, but it introduces a journal-deletion bug and still has material gaps in process detection and ban synchronization. No files were modified.

| Round-2 point | Assessment |
|---|---|
| 1. Archive validation | **Addressed**, but the world-file precondition was accidentally removed. |
| 2. Cluster identity | **Addressed.** |
| 3. Cluster ownership | **Addressed in design:** reservation plus distinct, ordered locks. |
| 4. Cluster recovery interlock | **Partial:** cluster journal added, but placed inside the directory restore clears. |
| 5. Stop intent at acceptance | **Addressed**, but the newly specified failure state is unsafe. |
| 6. Targeted session probe | **Partial:** existing enumerator cannot reliably distinguish missing from unreadable. |
| 7. Delete ownership/shutdown | **Addressed in design:** lease operations and job registry; shutdown implementation must be added explicitly. |
| 8. Schedule timing | **Partial:** deadline semantics fixed, but the lifecycle API still accepts only warning minutes. |
| 9. Background RCON/interrupted runs | **Addressed.** |
| 10. Database/file consistency | **Partial:** persist-first is correct; projection ordering remains unspecified. |
| 11. Concurrent ban writers/removals | **Partial:** safer direction, but stopped-state checks and import rules still allow races. |
| 12. Probe dependency/ordering | **Addressed:** event publication, session identity, and observation-time comparisons. |

Remaining findings and one-line fixes:

1. **B2 deletes its own cluster recovery journal.** The journal now lives inside the cluster directory, whose “whole content” is cleared during restore and again during rollback. A service termination after that clear leaves siblings without the intended launch interlock. Conversely, [BackupInventory.cs](../../src/ArkAscendedServerAdmin.Core/Backups/BackupInventory.cs) includes arbitrary cluster files, so a journal left after failure can enter a sibling’s backup.

   **Fix:** Store recovery metadata outside every backed-up/replaced directory, using one durable operation record referenced by all affected instance and cluster interlocks.

2. **B2 dropped `ContainsWorldFile` entirely.** Identity matching and a payload/manifest bijection do not require `<MapKey>.ark` to exist. A manifest containing only profiles—or no payload—can satisfy the revised rules, after which restore deletes the current world. The existing [BackupService.Verify](../../src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupService.cs:321) explicitly checks this.

   **Fix:** Reinstate the requirement for exactly one verified `World/<currentMapKey>.ark` before creating a destructive restore operation.

3. **Journal protection still does not cover deletion or a second restore after failure.** A failed rollback releases its locks but leaves a journal. The plan blocks launches and safety-copy pruning, yet does not reject another restore or instance deletion. [InstanceDeleteService.cs:96](../../src/ArkAscendedServerAdmin.Infrastructure/Maintenance/InstanceDeleteService.cs:96) retires the instance directory and can delete its entire backup directory, destroying the journal or its recovery source.

   **Fix:** While a recovery operation remains unresolved, reject ordinary restore, delete, and affected membership changes; allow only recovery or explicit resolution under the same reservation.

4. **B0’s failed-stop state is neither “as today” nor safe with current launch checks.** Today an unverified kill leaves the runtime `Stopping` and updates its detail ([ProcessManager.cs:622](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:622)). The revision changes it to `Unknown`, but [`HasLiveProcess`](../../src/ArkAscendedServerAdmin.Core/Processes/ProcessContracts.cs) excludes `Unknown`, and `StartAsync` uses that property to reject duplicate starts. A failed kill could therefore be followed by another launch.

   **Fix:** Keep an explicitly live/unverified state, and make every launch path reject an existing session or unresolved process identity regardless of the display state.

5. **B0’s enumerator can return incomplete results without throwing.** [WmiGameProcessEnumerator.Read](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/WmiGameProcessEnumerator.cs) skips unreadable rows and substitutes `DateTimeOffset.MinValue` for missing creation timestamps. The proposed probe interprets those cases as absent PID or different start time—therefore `Dead`. Exact timestamp comparison also differs from [ProcessMatcher.cs:41](../../src/ArkAscendedServerAdmin.Core/Processes/ProcessMatcher.cs:41), which allows precision drift.

   **Fix:** Give the targeted PID probe explicit completeness and identity-validity results; unreadable/missing identity fields mean `Unknown`, and timestamp comparison must account for representation precision without assuming PID reuse.

6. **B5’s “no concurrent writers” promise is still only a check.** The minute tick can observe no live instances, then a queued launch can start before replacement. The current process manager also has periods where runtime data is incomplete, and `Unknown` is not counted by `HasLiveProcess`. Moreover, sending RCON to several running servers still permits several game processes to write the shared file; the manager no longer writing does not establish a single writer.

   **Fix:** Serialize offline projection with all launches under a manager-wide reservation, require completed reconciliation with no unresolved/shared-install processes, and qualify live RCON synchronization as best effort until multi-server file behavior is tested.

7. **B5’s import rule can resurrect a pending removal.** Suppose an in-game ban X is observed and stored in desired state, but saving the applied baseline fails. The owner removes X, creating a pending removal; X remains in the file and absent from `BanListApplied`. The next tick classifies it as a new in-game ban and adds it back—the pending-removal exception currently applies only to the *missing-from-file* branch.

   **Fix:** Give explicit pending removals precedence over every file-to-database import branch until absence is confirmed, and define how an explicit re-ban supersedes that intent.

8. **Persist-first does not order concurrent projections or protect newer intent.** Two callers can save desired versions V1 and V2, then project V2 followed by V1 because projection runs outside `UpdateAsync`. A tick can also read an old file snapshot and apply its import after a newer editor change. No projection lock or version recheck is specified.

   **Fix:** Use one synchronization worker that projects the latest desired version, serializes read/apply/confirm work, and rejects stale observations or completions before updating the baseline.

9. **B5 treats a file read as authoritative without defining a complete read.** With a running game that rewrites the file, the tick may observe a truncated or partially written list and interpret missing IDs as intentional unbans. B1 explicitly allows whole-file rewrite behavior, but the revision addresses only the app’s writes.

   **Fix:** Do not infer removals from an unverified live-file snapshot; require a stable complete observation or defer removal import until all writers are stopped.

10. **B3’s absolute deadline cannot be expressed by B0’s restart signature.** `RestartWithCountdownAsync(instanceId, warningMinutes, ct)` has no deadline parameter. A tick arriving partway through the due minute cannot pass the specified action instant, and the existing countdown advances by elapsed delays.

    **Fix:** Pass an absolute deadline into scheduled restart/stop operations, deriving it from warning minutes only for manual requests.

Two smaller specification corrections:

- **Exclusive-join changes cannot reliably mean “next launch.”** If server A remains running, B5 forbids updating the shared file before server B launches, so B reads the old list. Say “after all instances stop and synchronization completes,” or reject a launch that requires unapplied exclusive-join changes.
- **`StartupOrchestrator` has no existing shutdown override.** [StartupOrchestrator.cs](../../src/ArkAscendedServerAdmin.Infrastructure/Startup/StartupOrchestrator.cs) currently implements startup execution only; specify the new shutdown hook, stop accepting new detached jobs before draining, and align the host shutdown budget with the promised wait.

VERDICT: REVISE

### Claude's response (round 3)

Accepted 10 of 10 plus both smaller corrections. Finding 1 was a regression I introduced in round 2 (the cluster journal placed inside the directory restore clears); finding 2 was a precondition I dropped while rewriting validation.

- 1, 3: one durable operation record under `DataRoot\Data\restore-journals\` (outside every backed-up or replaced directory) referencing the target, the cluster, and every affected instance; while it exists, launches, another restore, delete, and membership changes for all of them are Rejected; only Recover and Discard are allowed; referenced safety copies are exempt from pruning and from delete's backup-directory removal.
- 2: `ContainsWorldFile` reinstated, checked against the instance's current map key.
- 4: a failed stop keeps `Stopping` and the detail text as today, the session stays registered, `StopIntent` stays set for the session's life; every launch path rejects while a session is registered regardless of displayed state.
- 5: the targeted WMI read reports completeness; unreadable identity fields mean Unknown; timestamp tolerance matches `ProcessMatcher`.
- 6: offline projection holds the existing `MaintenanceGate`, requires no registered session in any state and a completed reconciliation; the live RCON path is qualified as best effort until B1's multi-server test.
- 7–9: B5 rewritten around one `ListSynchronizer` worker that owns all file access and ban RCON, snapshots the settings `Version` per cycle and rechecks it before recording applied state; pending removals take precedence over every import branch and an explicit re-ban clears them; live reads are trusted only when two consecutive reads are identical and import additions only; removals are confirmed only by the no-writer projection.
- 10: `RestartWithCountdownAsync` and `StopOptions` take an absolute deadline; manual requests derive it at acceptance; countdowns count down to the deadline.
- Smaller: exclusive-join changes apply "after all instances stop and synchronization completes", and launching an exclusive-join instance with unapplied list changes is Rejected (a stale allow-list is a security decision); `DetachedJobs` is its own hosted service that refuses new jobs on shutdown and drains for 60 s inside a 90 s `HostOptions.ShutdownTimeout`.

## Round 4 — Codex

Most round-3 findings are now addressed. Three material issues remain in B0/B5; the restore and scheduling revisions are sound enough to implement.

| Round-3 item | Assessment |
|---|---|
| 1. Journal inside replaced directory | **Addressed:** external operation record. |
| 2. Missing world-file requirement | **Addressed:** exact current-map world file required. |
| 3. Delete/second restore during recovery | **Addressed:** explicit interlocks and safety-copy protection. |
| 4. Failed-stop state | **Addressed:** retained session, `Stopping`, and persistent stop intent. |
| 5. Incomplete WMI observations | **Addressed:** targeted probe with completeness and timestamp tolerance. |
| 6. Offline projection versus launches | **Partial:** gate behavior and process-presence assumptions remain wrong. |
| 7. Pending-removal resurrection | **Addressed:** removal intent wins over imports. |
| 8. Concurrent projections | **Addressed:** single worker and version recheck. |
| 9. Partial live reads interpreted as unbans | **Addressed:** additions-only live import is proportionate here. |
| 10. Absolute deadline API | **Addressed.** |
| Smaller: exclusive-join activation wording | **Addressed:** synchronization requirement and launch rejection. |
| Smaller: detached-job shutdown hook | **Addressed in plan:** dedicated hosted service and shutdown budget. |

**Material issues**

1. **The existing maintenance gate rejects queued launches; it does not make them wait.** B0 explicitly promises waiting, but [MaintenanceGate.cs](../../src/ArkAscendedServerAdmin.Core/Processes/MaintenanceGate.cs) invokes its drain callbacks when acquiring exclusively, and [LaunchQueue.cs](../../src/ArkAscendedServerAdmin.Core/Processes/LaunchQueue.cs) completes drained entries as rejected. Consequently, synchronizing before the first launch can cancel the remaining launches in a bulk-start queue. Taking a shared lease instead would not exclude launches.

   **One-line fix:** Add an explicitly non-draining projection reservation coordinated with launch execution, while retaining the existing draining behavior for updates.

2. **“No registered sessions and reconciliation completed” does not establish “no game process anywhere.”** [ProcessManager.ReconcileAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:465) can complete with an ambiguous instance marked `Unknown` **without registering a session**. [ProcessMatcher](../../src/ArkAscendedServerAdmin.Core/Processes/ProcessMatcher.cs) also filters out processes whose path or command line cannot establish ownership. Thus B5 can rewrite a shared file while unresolved game processes still exist. The new targeted session probe cannot cover processes that never received sessions.

   **One-line fix:** Require a fresh, complete process enumeration under the projection reservation and refuse offline writes if any potentially relevant game process or unresolved identity remains.

3. **The offline branch overwrites in-game bans that have not yet been imported.** Example: an admin issues an in-game ban just after the minute tick, then stops the last server before the next tick. B5 now takes the offline branch and projects the database’s desired set directly over the file, erasing that ban. The same problem occurs on first use with a pre-existing ban file. Single-worker ordering and version checks do not prevent this—the missing step is importing existing additions before replacement.

   **One-line fix:** Under the offline reservation, read the existing file and persist previously unobserved additions, respecting pending removals, before projecting the resulting desired set.

**Nonblocking corrections**

- B6 still says live unbans are confirmed from the file, contradicting B5’s offline-only removal confirmation; align B6 with B5.
- B5 references “B1’s multi-server test,” but B1 still specifies one test instance; explicitly add the multi-server case.
- “Delays in RCON never push the action later” overstates absolute deadlines: an in-flight command or unavailable transport can still delay `doexit`; say deadlines prevent cumulative countdown drift and log lateness.

No files were modified, and the locked release plan remained out of scope.

VERDICT: REVISE

### Claude's response (round 4)

Accepted 3 of 3 material points and all three nonblocking corrections. Codex confirmed the restore and scheduling sections as sound enough to implement.

- 1: `MaintenanceGate` drains the launch queue (verified: drain callbacks complete queued entries as rejected), so it is not reused. B0 adds a non-draining `ProjectionReservation` on the `LaunchQueue`: while held, nothing dequeues and `StartAsync` waits; nothing is rejected.
- 2: the reservation is granted only on a fresh, complete enumeration showing no `ArkAscendedServer.exe` process at all (matched or not) and no registered session; otherwise the projection is deferred with a logged reason.
- 3: the offline branch now imports unobserved additions from the existing file (respecting pending removals) before projecting, so stopping the last server or a pre-existing file on first use never loses a ban.
- Nonblocking: B6 aligned with B5 (live unbans are pending until the no-writer projection); B1 gains the two-instance simultaneous `BanPlayer` test; the deadline wording now says deadlines prevent cumulative drift and lateness is logged.

## Round 5 — Codex

Five of the six round-4 changes are addressed: complete process enumeration, offline import-before-projection, B6’s unban wording, B1’s two-instance test, and deadline wording. The non-draining reservation fixes queue cancellation, but leaves one material race.

**B0 pauses dequeueing, not launches already dequeued.** [LaunchQueue.cs](../../src/ArkAscendedServerAdmin.Core/Processes/LaunchQueue.cs) removes an entry before awaiting its stagger delay and invoking the launch callback. During that interval, enumeration can show no process and no registered session, allowing projection to start; the already-dequeued launch can then proceed concurrently. Making `StartAsync` wait does not protect an invocation that already passed that check.

The fix must also accommodate [ProcessManager.cs](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs), whose launch callback generates configuration—and therefore, under B5, requests synchronization. Simply holding a launch exclusion across that entire callback could deadlock its synchronization request.

**One-line fix:** Define an atomic handoff between synchronization and process creation: already-dequeued launches must participate in the reservation, synchronization must run before entering the protected launch phase, and that phase must remain exclusive with projection through session registration.

I found no other material regressions. The remaining plan is proportionate for the intended home tool; this reservation contract needs clarification before implementation. No files were modified.

VERDICT: REVISE

### Claude's response (round 5)

Accepted the one remaining point (a dequeued launch sitting in its stagger delay is invisible to both the enumeration and the session registry, so pausing the dequeue alone is not enough). B0 now states a handoff contract: after dequeue a launch first awaits a synchronizer cycle holding nothing, then takes the reservation in shared mode for config generation, process creation, and session registration; the projection takes it exclusively. Ordering rules out the deadlock Codex anticipated.

## Resolution

MAX_ROUNDS=5 reached without `VERDICT: APPROVED`. There is no open disagreement: every Codex finding across the five rounds was accepted (58 findings and corrections in total), and the round-5 change is folded into the plan but has not been re-reviewed by Codex. Codex's own round-5 statement: "I found no other material regressions. The remaining plan is proportionate for the intended home tool." Owner sign-off is the final gate; no code is written until then.

Verified during the review, worth fixing whatever the owner decides: deleting a running instance is Rejected today because `InstanceDeleteService` holds the instance lock and then calls `StopAsync`, which tries to take it again (the command tests use a fake process manager and miss it). B0 covers the fix.
