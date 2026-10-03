<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: B4 crash detection and auto-restart
Started 2026-10-01 (evening, owner away; he delegated the plan decision to Claude and Codex). MAX_ROUNDS=5.
Plan file: `docs/.untracked/b4-recon/PLAN.md`.

## Round 1 — Codex (thread `<thread-id>`)

The plan has material concurrency problems. The pending marker does not establish the atomic lifecycle boundary it claims, and delayed requests can defeat both manual resets and disabling auto-restart.

1. **High — Exit cleanup can race a new launch and resurrect an obsolete marker.** Step 8 installs the marker immediately before posting, but [HandleExitAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:1159) removes the session, publishes `Stopped`, then awaits database cleanup. A manual Start can register during that await; its marker removal happens before the old handler installs the marker. The old database update can also clear the new process identity. This is an existing lifecycle gap that the proposed identity guarantee overlooks.  
   **Fix:** Keep the exiting session registered through identity cleanup and install its recovery generation before making the instance launchable; do not acquire the instance lease inside exit cleanup, because stop jobs hold it while awaiting `Exited`.

2. **High — `MarkCrashed` is not atomic across the three dictionaries.** Step 11’s `AddOrUpdate` protects only `_runtimes`, not `_pendingCrash` or `_sessions`. Its delegate can validate the marker, then manual Start removes that marker while the runtime is still `Stopped`; the delegate can subsequently commit `Crashed`. If Start is rejected before registration, the state remains `Crashed` despite the promised reset. Retrying the delegate when `Register` changes the runtime does not cover this case.  
   **Fix:** Serialize marker validation, invalidation, and terminal state changes under one synchronization mechanism, or put the recovery generation in the runtime record being compared atomically.

3. **High — Stale requests consume the new crash budget.** Step 15 calls `CrashLoopGuard.Decide` before checking request identity. A request queued before a manual Start can run after that Start resets the guard, increment the fresh counter, then receive `Stale`. Busy requests that never launch also consume attempts. Thread safety inside the guard does not make this ordering correct.  
   **Fix:** Validate the recovery generation and reserve an attempt atomically with launch acceptance; obsolete or dropped requests must not modify the current generation’s budget.

4. **High — Turning off auto-restart does not reliably cancel pending recovery.** The normal path reads `AutoRestart` once, waits five seconds, then launches; only the Busy path rereads the row. [SaveAsync](../../src/ArkAscendedServerAdmin.Server/Commands/InstanceCommands.cs:479) can save the setting during that interval, and the global launch queue can extend the interval substantially. Neither proposed manager API nor the existing launch callback checks the setting again.  
   **Fix:** Invalidate pending recovery when the toggle is disabled and revalidate eligibility in the automatic launch callback before creating a process.

5. **High — Two-minute Busy retries can outlive an operation that should suppress recovery.** There is no explicit delete ownership check. [InstanceDeleteService](../../src/ArkAscendedServerAdmin.Infrastructure/Maintenance/InstanceDeleteService.cs:123) sets stop intent only if its job still sees a live runtime; a crash before that check bypasses it. If deletion later fails after removing junctions, the row remains and the retry can launch it. Similarly, a successful restore can finish during the retry interval and be followed by an unsolicited restart of the restored world.  
   **Fix:** Invalidate pending recovery when delete or restore accepts ownership; retry only contention that does not supersede recovery.

6. **Medium — Maintenance classification uses the wrong point in time.** The spec excludes exits during maintenance, but the plan checks maintenance only when consuming the request. A delayed request can therefore restart after the update finishes. Conversely, a maintenance rejection can become `Crashed` if the gate releases before the policy checks it again; [LaunchQueue](../../src/ArkAscendedServerAdmin.Core/Processes/LaunchQueue.cs:82) explicitly completes drained requests with a rejection reason.  
   **Fix:** Record maintenance eligibility with the exit and return a typed maintenance rejection rather than interpreting a later gate snapshot.

7. **Medium — The dashboard count requirement is removed, not satisfied.** Key decision 8 replaces structured runtime count with prose and console lines. The dashboard uses `ShowHint="false"` in [Home.razor](../../src/ArkAscendedServerAdmin.Components/Pages/Home.razor:124), so it will display only “Crashed”; refusal details contain no count at all. The give-up message also says three unexpected exits, although the proposed sequence gives up on the fourth exit after three relaunches.  
   **Fix:** Expose the attempt count in `InstanceRuntime`, render it in the state cell, and distinguish exits from restart attempts in messages.

8. **Medium — Shutdown ownership of per-instance chains is unspecified.** A stopping token does not make `BackgroundService` await independently dispatched tasks. This matters because [LaunchQueue](../../src/ArkAscendedServerAdmin.Core/Processes/LaunchQueue.cs:14) stops honoring caller cancellation once its callback starts. The policy reader can finish while recovery tasks still hold leases or use dependencies.  
   **Fix:** Track active chains, remove completed entries, and explicitly await outstanding work during shutdown.

The tests need deterministic barriers for these interleavings: exit cleanup versus Start, `MarkCrashed` versus a rejected manual Start, stale requests after reset, disabling during delay/queueing, delete failure, restore completion, and shutdown during launch. The proposed sequential stale test and immediately advancing `FastTimeProvider` do not establish these guarantees. A process-start abstraction would also permit successful automatic relaunch tests without installing the game.

On the deliberate deviations:

- **Consecutive short-lived sessions:** A defensible alternative, but a different policy requiring a specification change. It stops four-minute crash loops that B4 permits; it also permits some bursts B4 would block after resetting on a long-lived session. Test both differences explicitly.
- **Separate hosted service, in-memory `Crashed`, broader reset scope, and one PR:** Reasonable, subject to the synchronization and shutdown fixes above.
- **Busy retries:** Reasonable for transient contention, but unsafe without operation-specific invalidation.
- **Refused launch becomes `Crashed`:** Useful for terminal failures; maintenance and shutdown cancellations need separate handling.
- **Five-second delay:** Reasonable backoff, but not a guarantee that ports are available.
- **Omitting the runtime count:** Not justified against the explicit dashboard requirement.

The schema approach is sound: the state column already uses a string conversion, and config export copies the SQLite database directly, so it needs no additional field mapping.

VERDICT: REVISE

### Claude's response (round 1)

Accepted all eight; the plan was restructured around one rule: recovery state lives in the `InstanceRuntime` record and every writer but the exit path holds the instance lease.

1. Exit cleanup vs. new launch: accepted. `HandleExitAsync` is reordered so the database cleanup runs while the session is still registered; the instance becomes launchable in the same atomic update that installs the marker.
2. `MarkCrashed` not atomic: accepted. The side dictionary and the lock-free `MarkCrashed` are gone; `PendingCrash` and `AutoRestarts` are fields of the runtime record and the Crashed transition happens inside `RecoverAsync` under the lease.
3. Stale requests consuming budget: accepted. The stateful guard is replaced by a pure `CrashLoopRule`; the counter only changes when `Register` runs for an automatic launch.
4. Toggle off during the delay: accepted. `RecoverAsync` re-reads the row under the lease and the launch callback re-checks it after the queue wait.
5. Busy retries outliving delete/restore: accepted. `DismissCrash` is called by delete and restore at ownership and again before releasing each lease.
6. Maintenance point in time: accepted. `RecoveryRequest.DuringMaintenance` is recorded at exit; a gate rejection at launch is `Skipped`, matched on the shared `MaintenanceGate.UpdateInProgress` constant rather than a new typed outcome (changing `OperationOutcome` for every caller is not worth it).
7. Dashboard count: accepted. `AutoRestarts` on the runtime, rendered next to "Crashed"; messages say "after 3 automatic restarts".
8. Shutdown ownership: accepted. Chains are tracked and awaited.
Tests: barriers added for the named interleavings; a launch seam (`IGameProcessStarter`) allows real relaunch tests.
Deviation 1 (consecutive short-lived sessions) kept, recorded as a spec change with both differences tested.

## Round 2 — Codex

The revision fixes the original counter race, adds the required dashboard count, and gives recovery tasks explicit shutdown ownership. Material lifecycle gaps remain.

1. **High — A Stop accepted during exit cleanup is not safely handled.** Step 8 builds the recovery request before awaiting the database write, while deliberately leaving the session available to `TryAcceptStop`. Stop can then set `StopIntent` after the request captured `false`. Also, [RunStopJobAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:713) awaits settings before writing `Stopping`; that continuation can run after exit cleanup published `Stopped`. The result can be a dead instance stuck in `Stopping`, or a recovery request that ignores the accepted Stop. The claim that this Stop “completes at once” is incorrect: the countdown does not observe `session.Exited`.  
   **Fix:** Serialize stop acceptance with a session’s transition into exit cleanup; a Stop accepted during cleanup must suppress recovery and await `Exited` without starting countdown or writing `Stopping`.

2. **High — Existing probe and identity writers violate the synchronization argument.** The plan says every writer except the exit handler holds the lease, but [OnProbeSucceededAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:1039) and `OnProbeFailedAsync` do not. A probe can read `Starting`, pause, then overwrite the exit handler’s `Stopped` with `Running` or `StartingUnconfirmed`. `RecoverAsync` subsequently returns `Stale`, permanently losing recovery. Cancellation after publishing `Stopped` cannot retract a callback already executing. Separately, [RetryPersistIdentityAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:463) can persist the dead session’s identity and publish a live state after cleanup; its lease does not exclude the exit handler.  
   **Fix:** Make session callbacks conditional on the current session generation inside the atomic runtime update, and order in-flight identity/state persistence before final exit cleanup.

3. **High — A launch rejection can erase the next crash’s recovery request.** Step 11 distinguishes `IdentityUnpersisted` by whether a session is *currently* registered. That is insufficient. [LaunchAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:323) starts supervision before returning the persistence rejection. The newly launched process can exit, finish cleanup, and install its own `PendingCrash` before `RecoverAsync` handles that rejection. Finding no session, the proposed fallback writes `Crashed` and clears the **new** marker. The new exit request then becomes stale. Holding the lease does not prevent this because exit cleanup is lease-free.  
   **Fix:** Apply every post-launch rejection transition only if `PendingCrash` still references the original request; once registration consumed that request, leave subsequent session state and recovery markers untouched.

4. **Medium — Disable protection still has an undefined acceptance boundary.** Rechecking the row at callback entry closes the queue-wait gap, but [LaunchAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:245) subsequently awaits layout work, synchronization, projection reservation, and config generation. `SaveAsync` can persist `AutoRestart=false` during those waits, yet the process still starts from the earlier snapshot. This is more than a tiny instruction-level race. The proposed test using a starter barrier would expose this if disabling occurs after the row read.  
   **Fix:** Define an explicit automatic-launch commitment boundary and coordinate disabling with it; either invalidate recovery before that boundary or clearly document that an already committed launch proceeds.

5. **Medium — The second claimed difference from the sliding window is impossible under normal serialized operation.** Decision 1 says a session lasting at least ten minutes resets the new budget while the sliding window still contains recent restarts. Its own restart occurred at least ten minutes ago, and all previous restarts occurred earlier. Apart from boundary conventions or clock anomalies, the window is already empty. This was an error in my first review; the revision should not preserve it or require an impossible test.  
   **Fix:** Remove that claim and test the real difference: repeated short-lived sessions spread beyond the sliding window still exhaust the consecutive-session budget.

The new test plan should add deterministic barriers for points 1–3: Stop accepted during blocked exit cleanup; an old probe or identity retry completing after cleanup; and a persistence-rejected automatic launch whose process exits before its result is handled. The current blocked-cleanup test covers competing Start, not these writers.

The deliberate deviations are otherwise reasonable: the consecutive-session rule is an explicit specification change; bounded Busy retries now dismiss recovery around delete/restore; terminal refusal as `Crashed` is useful; shared rejection constants avoid an unnecessary outcome-type migration; and in-memory state, broader registration resets, five-second backoff, and one PR are acceptable. No new schema conflict emerged.

VERDICT: REVISE

### Claude's response (round 2)

1. Stop during exit cleanup: accepted. A per-session lock makes `Exiting` and `StopIntent` mutually ordered: a Stop is accepted before the exit is observed (request carries the intent) or refused as not running.
2. Probe / identity writers: accepted. `UpdateForSession` applies a session-scoped write only while the record still carries that session's identity; database writes for a session are ordered by a per-session mutex with an `Exiting` re-check.
3. Rejection erasing the next crash's marker: accepted. Every terminal write in `RecoverAsync` is conditional on `PendingCrash` still being the original request.
4. Disable boundary: accepted as documentation. The callback's row read is the commitment point; a later toggle does not stop that launch. Coordinating `SaveAsync` with the launch path is not worth a new lock for a launch the owner can stop.
5. Impossible second difference: removed.
Tests: barriers added for 1–3.

## Round 3 — Codex

The generation checks and conditional rejection updates address the main round-2 races. One material race remains, plus two smaller gaps.

1. **High — `DismissCrash` cannot dismiss an exit whose marker has not been published yet.** Steps 8 and 13 allow this sequence:
   - Exit cleanup sets `Exiting` and blocks on its database write; `PendingCrash` is still null.
   - Delete acquires the instance lease and clears that null marker.
   - The runtime still appears live, so [InstanceDeleteService](../../src/ArkAscendedServerAdmin.Infrastructure/Maintenance/InstanceDeleteService.cs:123) calls `StopUnderLeaseAsync`.
   - The new `Exiting` check rejects Stop. Delete aborts, clears the still-null marker in `finally`, and releases the lease.
   - Exit cleanup resumes, installs the crash marker, and automatic recovery restarts the instance.

   Both dismissal calls ran, yet neither suppressed recovery. This contradicts the promise that even failed deletion supersedes recovery.  
   **Fix:** Make dismissal invalidate the exiting session’s recovery generation as well as any published marker, and check that invalidation atomically when installing `PendingCrash`.

   **Required test:** Block exit cleanup before marker publication, run deletion through its rejection and lease release, then unblock cleanup and verify that no recovery launch occurs.

2. **Medium — The database-ordering list omits the stop job’s mirror.** Step 8a names probe mirrors, identity retries, and exit cleanup as mutex participants. But [RunStopJobAsync](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:724) also mirrors `Stopping` through `SetStateAsync`. A successful generation-checked runtime update can precede cleanup while its database mirror follows cleanup, leaving the database at `Stopping` with no process. Checking whether the runtime update applied does not order the subsequent asynchronous write.  
   **Fix:** Explicitly route every session-scoped database mirror, including the stop job’s, through the same mutex and `Exiting` check.

3. **Low — The disable test still mixes opposite sides of the commitment boundary.** The plan permits a barrier “in the starter or the queue’s stagger” for a test expecting `Disabled`. A starter barrier is after the callback’s row read, so under the newly documented contract that launch must proceed.  
   **Fix:** Split this into a pre-read queue barrier expecting `Disabled` and a post-read starter barrier expecting the committed launch to proceed.

The deliberate deviations are now adequately justified: consecutive-session counting is an explicit spec change; in-memory `Crashed`, broader resets, terminal-refusal handling, bounded retries, backoff, and one PR are reasonable. The documented toggle commitment boundary is also coherent. Busy retries remain contingent on fixing dismissal of an exit still undergoing cleanup.

VERDICT: REVISE

### Claude's response (round 3)

1. Dismissal of an exit still in cleanup: accepted. `DismissCrash` flags an `Exiting` session (`RecoveryDismissed`, under the session lock) before clearing the marker, and the exit handler's publishing update reads the flag; the required test is added.
2. Stop job mirror: accepted. Every session-scoped mirror goes through the mutex and the `Exiting` check via one helper.
3. Disable test: split into a pre-read barrier (Disabled) and a post-read barrier (launch proceeds).

## Round 4 — Codex

The mirror ordering and toggle tests are corrected. The new dismissal flag still does not make dismissal atomic with exit publication.

1. **High — There remains a gap where neither the session nor its crash marker is discoverable.** [Step 8](../../docs/.untracked/b4-recon/PLAN.md:96) removes the session before publishing `Stopped` and `PendingCrash`. If the exit handler pauses between those operations, `DismissCrash` finds no session to flag and no marker to clear. Delete can acquire its lease, fail its stop because the session is absent, run both dismissal calls, and release the lease. Exit publication then installs an undismissed marker and recovery launches.

   Reading `RecoveryDismissed` inside the update delegate also does not make that flag atomic with publication. The existing [Update helper](../../src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:1316) uses `ConcurrentDictionary.AddOrUpdate`: its delegate can calculate a marker-bearing result before dismissal sets the flag. Clearing an already-null marker need not change the runtime’s value, so publication need not retry its delegate.

   **Fix:** Replace the separate dismissal flag with a recovery epoch in `InstanceRuntime`; increment it on dismissal, capture it when exit handling begins, and condition marker installation on that epoch still matching inside the atomic update.

   **Required tests:** Dismiss after session removal but before marker publication; also dismiss after the publishing delegate reads eligibility but before its update commits. The database-cleanup barrier currently planned covers neither gap.

2. **Medium — The database barrier tests can deadlock under the intended mutex design.** The test at [line 238](../../docs/.untracked/b4-recon/PLAN.md:238) holds the stop mirror until *after* exit cleanup. If the EF interceptor blocks that write while it owns the session mutex, exit cleanup cannot finish until the barrier releases. The identity-retry test has the same issue if its barrier is inside the database write.

   **Fix:** Test both orderings separately: an in-flight writer blocks cleanup until released, after which cleanup leaves `Stopped`; a writer delayed before mutex acquisition resumes after cleanup and skips its write.

The deliberate deviations remain reasonable and sufficiently explicit. No additional schema or UI issue emerged. The remaining blocker is the dismissal/publication protocol; keeping its version in the runtime record would also restore the plan’s stated single-record synchronization rule.

VERDICT: REVISE

### Claude's response (round 4)

1. Dismissal gap: accepted, with Codex's fix. The session flag is gone; `RecoveryEpoch` lives in the runtime record, `DismissCrash` bumps it, the exit captures it and installs the marker only if it still matches inside the atomic update. Tests: dismissal after session removal and before publication; the pure publishing function; a stress race (a barrier inside `AddOrUpdate`'s delegate would need a production hook, so the mid-delegate case rests on the compare-and-swap plus the stress test).
2. Barrier deadlock: accepted. Both orderings are tested separately.

## Round 5 — Codex

The epoch fixes publication-versus-dismissal races **after the epoch is captured**, and the database tests now have valid ordering. One material synchronization gap remains.

**High — Epoch capture is not explicitly atomic with making `Exiting` visible.** [Step 8a](../../docs/.untracked/b4-recon/PLAN.md:104) specifies a lock block that sets `Exiting` and captures stop intent; [step 13](../../docs/.untracked/b4-recon/PLAN.md:168) separately captures the epoch “right after.” That permits:

1. Exit handling sets `Exiting`, releases the session lock, and pauses before reading the epoch.
2. Delete acquires the instance lease and increments the epoch.
3. Its stop is rejected because `Exiting` is true. [Deletion aborts](../../src/ArkAscendedServerAdmin.Infrastructure/Maintenance/InstanceDeleteService.cs:111), performs its closing dismissal, and releases the lease.
4. Exit handling captures the **already-incremented** epoch, publishes a matching crash marker, and recovery launches.

The assertion that “the delete’s or restore’s closing dismissal still follows” the capture is therefore not guaranteed. The planned tests all dismiss after capture and miss this ordering.

**Fix:** Capture `RecoveryEpoch` inside the same session-lock critical section that sets `Exiting` and captures stop intent, before releasing that lock to `TryAcceptStop`.

**Required test:** Pause exit handling at the epoch-capture boundary while deletion runs; verify that deletion cannot finish its rejected-stop path and closing dismissal before capture completes, and that subsequent publication produces no marker.

If capture inside that critical section was intended, state it explicitly in the pseudocode; the epoch design otherwise works.

The other reviewed problems are resolved at the planning level. The documented spec deviations remain reasonable, and I found no additional material schema or UI issue. This verdict concerns the remaining capture-order guarantee, not the overall approach.

VERDICT: REVISE

### Claude's response (round 5) and resolution

Accepted: the epoch is captured inside the session-lock critical section that sets `Exiting` (step 8a now says so, with the test).

MAX_ROUNDS=5 reached without `VERDICT: APPROVED`. There is no open disagreement: every point Codex raised in five rounds was accepted, and its last verdict says the remaining issue "concerns the remaining capture-order guarantee, not the overall approach" and gives the fix that was applied. The round-5 change itself is unreviewed by Codex. The owner delegated the plan decision on 2026-10-01 ("I will leave it to you and codex to decide the best plan to implement, no input from me is required"), so implementation proceeds on `feat/b4-crash-restart`; he reviews the PR.
