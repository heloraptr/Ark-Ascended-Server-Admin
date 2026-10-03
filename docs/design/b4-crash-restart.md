<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: B4 crash detection and auto-restart
_Final — revised after Codex round 5 (MAX_ROUNDS reached; the round-5 change is unreviewed)_

Spec: `docs/.untracked/RELEASE-PLAN-FUTURE.md` § B4 (lines 249–265) and the B0 bullets it relies on
(62–92, 113–115). Code survey of main at 66012a8: `docs/.untracked/b4-recon/backend-and-ui.md`
(verbatim excerpts with line numbers).

## Goal

An instance with the new `AutoRestart` setting on is relaunched by the manager after its game process
exits without a manager-initiated stop. A crash loop ends in a new `InstanceState.Crashed` ("gave up")
that the owner clears with a manual Start. Default off; an instance that leaves it off behaves exactly as
today (an unexpected exit ends in `Stopped`).

## What exists already (B0)

- `Session.StopIntent` set synchronously at stop acceptance; `HandleExitAsync` posts one
  `RecoveryRequest` to the `RecoveryRequests` channel after cleanup and after `Exited` is signalled, for
  every confirmed exit.
- The liveness loop already resolves a throwing `HasExited` through `ProbeSessionCoreAsync`; only Dead is
  an exit, Unknown keeps supervising and posts nothing. No change there.
- Nothing reads the channel. No `AutoRestart`, no `Crashed`, no public way to set a state.

## The synchronization rule (everything below follows from it)

All crash-recovery state lives **in the `InstanceRuntime` record** held in `_runtimes`, never in a side
dictionary, so one atomic `Update` reads and writes it together with `State`:

- `RecoveryRequest? PendingCrash` — the unexpected exit that has not been answered yet (the "recovery
  generation"; compared by reference).
- `int AutoRestarts` — consecutive automatic restarts whose session was short-lived (the guard counter
  and the number the UI shows).

Who may change them:

| Change | Where | Serialized by |
|---|---|---|
| install `PendingCrash` | `HandleExitAsync`, in the same `Update` that sets `Stopped` | happens while the instance is not launchable (see step 8) |
| answer it (launch, give up, refuse, disable, skip) | `RecoverAsync` | the instance lease |
| clear it and reset `AutoRestarts`, `Crashed` → `Stopped` | every non-automatic `StartAsync` at acceptance | the instance lease |
| clear it and bump `RecoveryEpoch` (which also voids an exit still in cleanup) | `DismissCrash`, called by delete and restore while they hold the lease | the instance lease; the epoch compare inside the exit's atomic update |
| set `AutoRestarts` on a launch | `Register` (0 for every launch or attach except the automatic one) | the instance lease (launch) / startup (reconcile) |

Every writer of the recovery fields other than `HandleExitAsync` holds the instance lease, and every
write, lease or not, is one compare-and-apply inside the atomic record update: `RecoverAsync`'s terminal
writes apply only while `PendingCrash` is still the request it was called with, so there is no
check-then-act across structures.

**Session-scoped writers** (the RCON probe callbacks, the stop job, `RetryPersistIdentityAsync`, telemetry)
do not hold the lease against the exit path today and can overwrite `Stopped` with a live state after the
exit handler ran. They are made generation-safe (step 8a): a write on behalf of a session applies only
while the record still carries that session's identity (`Pid` and `ProcessStartTime`), which the exit
handler clears in its one update.

## Approach

One branch `feat/b4-crash-restart`, one worktree, one PR (backend, migration, UI toggle, docs).

### 1. Schema and settings path
1. `Instance.AutoRestart` (bool, default false, no explicit EF configuration, like
   `OverridesClusterSchedule`). Migration `AutoRestart`: one `AddColumn<bool>` with `defaultValue: false`.
   No other migration branch is open. Run `has-pending-model-changes` before pushing.
2. `InstanceEdit` gains a positional `bool AutoRestart` (no default value, so every construction site
   must be touched): `InstancePage.razor:860`, `Players.razor:185` (passes the stored value through, or a
   whitelist append would reset it), the test sites. `InstanceCommands.SaveAsync` maps it. The wizard is
   not changed. Config export copies the SQLite file, so it needs nothing.

### 2. `InstanceState.Crashed`
3. Add `Crashed` (string-stored, no migration). Not in `HasLiveProcess`.
4. **In memory only.** `HandleExitAsync` already mirrored `Stopped` to the database; `Crashed` changes
   only the runtime record. `ReconcileAsync` resets unattached instances to `Stopped` at service start,
   the counter is in memory, and the UI reads the runtime; persisting it would only leave a value an older
   build cannot parse.
5. Consumers: `RestoreService.CheckReady` accepts `Stopped` or `Crashed`; `Presentation.Tone` →
   `st-bad`, `IsHollow` false, `Label` → "Crashed", `Hint` → `runtime.Detail`;
   `FakeProcessManager.Set` treats `Crashed` as not live. Everything keyed on `HasLiveProcess` or
   `== Running` already behaves (Start enabled, bulk Start includes it, scheduler skips, update does not
   relaunch it, delete proceeds without a stop).

### 3. Crash-loop rule (Core, pure, stateless)
6. `Core/Processes/CrashLoopRule`: `static CrashLoopDecision Next(int autoRestarts, TimeSpan uptime)`.
   If `uptime >= ShortLived (10 min)` the count is taken as 0. Count under `MaxRestarts (3)` →
   `Restart(count + 1)`; otherwise `GiveUp`. Uptime = `request.ExitedAt - request.ProcessStartTime`.
   Unit tests in the Core-only unit project, including the case where it differs from the spec's
   sliding window (decision 1).

### 4. Process manager
7. `RecoveryRequest` gains `bool DuringMaintenance` (the value of `_gate.IsHeldExclusively` read in
   `HandleExitAsync`). `InstanceRuntime` gains `PendingCrash` and `AutoRestarts` (defaulted parameters at
   the end, so existing positional constructions compile).
8. **`HandleExitAsync` reordered so an exiting instance is not launchable until its cleanup is done**
   (fixes an existing gap: today the session is removed and `Stopped` published before the awaited
   database write, so a Start can register in between and the late write clears the new process's
   identity). New order: read the exit code and build the request → `MirrorStateAsync(Stopped,
   clearIdentity)` while the session is still registered and the runtime still shows a live state (so
   `StartCoreAsync` rejects) → remove the session → one `Update` that sets `Stopped`, clears the identity,
   and sets `PendingCrash = request` when `!StopIntent && !DuringMaintenance` (else null) → telemetry
   removal → cancel, dispose, signal `Exited` → post the request. The exit path still takes no lease and
   awaits nothing after the signal. (Between "remove the session" and the `Update` the runtime still has a
   live state, so Start is still rejected; the instance becomes launchable in the same atomic step that
   installs the marker.)
8a. **Session generation safety** (closes races that exist today and that the reorder would widen):
    - `Session` gets a small lock object and an `Exiting` flag. `HandleExitAsync` begins with
      `lock { Exiting = true; stopIntent = StopIntent || StopRequested; epoch = GetRuntime(id).RecoveryEpoch; }`
      and builds the request from the captured intent. **The epoch (step 13) is captured inside this
      same critical section**, before the lock is released to `TryAcceptStop`: a delete whose stop is
      refused because of `Exiting` can only issue its closing dismissal after the capture, so that
      dismissal always voids the exit. Test: exit handling paused inside the critical section while a
      delete runs; the delete's stop blocks on the session lock until the capture is done, and the
      later publication installs no marker. `TryAcceptStop` sets `StopIntent` inside the same lock and rejects with "The
      instance is not running." when `Exiting` is already set. So a Stop is either accepted before the
      exit was observed (the request carries `StopIntent`, no recovery) or refused; none is accepted
      against a session in cleanup, and no stop job starts a countdown on a dead session.
    - `UpdateForSession(session, change)`: an atomic `Update` that applies `change` only when the
      record's `Pid` and `ProcessStartTime` equal the session's, and reports whether it applied. Every
      session-scoped state write goes through it: probe success and failure, the stop job's `Stopping`,
      `ExitRequested`, and "still alive after kill" detail writes, the identity-retry's state publish,
      telemetry. A write that did not apply skips its database mirror. A stop job that was accepted
      before the exit and resumes after it therefore cannot put a dead instance back into `Stopping`; it
      finds `session.Exited` completed and returns success. (The implementer checks that the countdown
      and the doexit/kill steps of `RunStopJobAsync` are skipped once `Exited` is complete.)
    - Database writes for one session are ordered by a per-session async mutex: **every** session-scoped
      mirror (the probe callbacks', the stop job's `Stopping` through `SetStateAsync`, the identity
      retry's) and the exit cleanup take it; all but the exit cleanup re-check `Exiting` after acquiring
      it and skip. The implementer greps every `SetStateAsync`/`MirrorStateAsync` call made on behalf of
      a session and routes it through one helper so none is missed. The exit cleanup sets `Exiting` first and then waits for the mutex, so an in-flight
      identity or state write always lands before the final `Stopped` and identity clear, never after.
9. `LaunchKind.AutoRestart` (treated like `User`: refused until Ready). `StartCoreAsync` → `LaunchAsync`
   → `Register` carry an `autoRestarts` value (0 by default); `Register` writes it into the fresh record,
   so every manual, scheduled, update, or attach registration resets the counter to 0 and clears
   `PendingCrash` by construction.
10. **Launch seam.** The one `Process.Start` call in `LaunchAsync` goes through a tiny
    `IGameProcessStarter` (default implementation calls `Process.Start`), so the real-manager test harness
    can launch a stand-in `cmd.exe` and exercise a successful relaunch. No behavior change.
11. `Task<CrashRecovery> RecoverAsync(RecoveryRequest request, CancellationToken ct)` on
    `IProcessManager`. `CrashRecovery(Status, Attempt, Reason)` with
    `Status { Launched, GaveUp, Refused, Disabled, Skipped, Stale, Busy }`:
    1. `TryAcquire` null → `Busy` (nothing changed).
    2. Under the lease: `runtime.PendingCrash` is not this request (by reference), or a session is
       registered, or the state is not `Stopped` → `Stale`.
    3. Re-read the instance row: missing → clear `PendingCrash`, `Stale`; `AutoRestart` off → clear it,
       `Disabled`.
    4. `CrashLoopRule.Next(runtime.AutoRestarts, uptime)`: `GiveUp` → one `Update` to `Crashed` with the
       detail, `PendingCrash = null` → `GaveUp`.
    5. `Restart(n)` → console Info "Restarting after unexpected exit (n/3)." →
       `StartCoreAsync(id, LaunchKind.AutoRestart, autoRestarts: n, ct)`. The launch callback re-checks
       `instance.AutoRestart` on the row it loads anyway (the queue wait can be long) and rejects with a
       dedicated constant when it is now off. **That row read at callback entry is the commitment point:**
       a toggle saved before it cancels the restart; one saved after it (during layout, projection, or
       config generation) does not stop that launch, which then runs like any launch and can be stopped
       with Stop. The guide says so in one sentence.
       - success → `Launched(n)` (`Register` wrote `AutoRestarts = n` and cleared the marker).
       - every rejection is answered by **one conditional update that applies only while `PendingCrash`
         is still this request**. `Register` clears the marker, so once a process was registered for this
         attempt (including the `IdentityUnpersisted` rejection, and including the case where that process
         has already exited and installed its own marker) the update does not apply, the state and the
         newer marker are left alone, and the result is `Refused(reason)` for the log only.
       - while the marker is still ours: the "auto-restart turned off" constant → clear, `Disabled`;
         `MaintenanceGate.UpdateInProgress` (the constant both `StartCoreAsync` and the queue drain use)
         or a stopping host → clear, `Skipped(reason)`, state stays `Stopped`; any other rejection →
         `Crashed` with "Automatic restart was refused: {reason}", `AutoRestarts` unchanged, marker
         cleared → `Refused(reason)`.
       The `GiveUp`, missing-row, and `Disabled` writes in 3 and 4 are the same kind of conditional update.
    The counter therefore only moves when a process is registered; stale, busy, disabled, and skipped
    requests never touch it.
12. **Manual start.** Public `StartAsync` with a kind other than `AutoRestart`, after taking the lease and
    before `StartCoreAsync`: one `Update` that clears `PendingCrash`, zeroes `AutoRestarts`, and turns
    `Crashed` into `Stopped` with no detail. It holds even when the start is then rejected.
    `RestartWithCountdownAsync` needs a live process, so it never meets `Crashed`; its relaunch registers
    with 0.
13. `void DismissCrash(int instanceId)`: one `Update` that clears `PendingCrash` **and increments
    `RecoveryEpoch`**, a third recovery field of the runtime record (int; `Register` carries it into the
    fresh record). `HandleExitAsync` captures the epoch from the record right after it sets `Exiting`
    (step 8a), and its publishing update installs the marker only when `!stopIntent && !DuringMaintenance`
    and the record's epoch still equals the captured one; the comparison is made inside the update
    delegate against the record being replaced. A dismissal at any point after the capture (session still
    registered, session already removed, or marker already published) therefore wins: it either bumps the
    epoch before publication, or clears the published marker. Because a dismissal always changes the
    record, a publishing delegate that computed its result from the pre-dismissal record fails its
    compare-and-swap and is re-run against the new epoch. A dismissal before the capture does not affect
    that exit, which is right: the delete's or restore's closing dismissal still follows it. The
    publishing step is a pure static function (`record, request, capturedEpoch, eligible → record`) so the
    epoch rule is unit-tested directly. `InstanceDeleteService`
    and `RestoreService` call it for every instance they own, when they take ownership and again in the
    `finally` just before each lease is released. A crash answered by neither a delete (even a failed
    one) nor a restore can then be relaunched later: the retrying request finds the marker gone → `Stale`.

### 5. `CrashPolicy : BackgroundService` (Infrastructure/Processes, registered in `AddArkProcesses`)
14. Waits for Ready (the `ScheduledActionRunner` pattern), then reads
    `RecoveryRequests.Reader.ReadAllAsync(stoppingToken)`.
15. Per request, on a per-instance task chain (requests for one instance run in order; another instance
    is never blocked):
    1. `StopIntent` or `DuringMaintenance` → ignore.
    2. Read the row: missing or `AutoRestart` off → ignore (cheap pre-filter; the authoritative check is
       in `RecoverAsync`).
    3. Wait 5 seconds (`TimeProvider`), then `RecoverAsync`.
    4. `Busy` → retry once a second for up to 2 minutes. Still busy → console Warning "Automatic restart
       skipped: another operation is using this instance." and stop; the marker stays, the state stays
       `Stopped`, nothing retries later.
    5. Console and log lines by result: `GaveUp` → Warning "The server exited unexpectedly again after 3
       automatic restarts; automatic restart gave up. Start it to try again." (also the `Detail`);
       `Refused` → Warning with the reason; `Skipped` → Info "Automatic restart skipped: {reason}.";
       `Disabled`, `Stale`, `Launched` → log only (the manager already wrote the "Restarting…" line).
16. **Shutdown.** The chains are tracked in a dictionary whose entries are removed on completion.
    `ExecuteAsync` ends by awaiting every outstanding chain (delays and retries observe the stopping
    token; a launch already inside the queue callback runs to its end, as any launch does today). The
    host's 90-second shutdown budget covers it. An exception in a chain is logged and never ends the
    reader.

### 6. UI and docs
17. Settings tab: a checkbox "Restart automatically after an unexpected exit" for every instance, hint
    "After three automatic restarts in a row that each end within 10 minutes, the manager stops trying
    and shows Crashed."
18. State cell: `InstanceStateView` (dashboard, instance header, cluster rows) shows "Crashed" and, when
    `AutoRestarts > 0`, the count next to it ("Crashed · 3 restarts"); the detail is the tooltip and the
    instance page's notice. Verified locally on the dev root (5080); `docs/images/instance-settings.png`
    retaken.
19. Docs: `docs/guide/README.md` state table, `guide/instances.md` (Settings row, notice list, the
    "nothing restarts it" paragraph), `guide/console-and-rcon.md` (line 168 and the new console lines),
    `guide/troubleshooting.md`, `guide/backups.md` (Crashed counts as stopped for restore), README "What
    it does". `RELEASE-PLAN-FUTURE.md` § B4 gets a "Revised 2026-10-01" bullet recording decisions 1, 4,
    and 5, and the status table is updated.

### 7. Tests
- Unit: `CrashLoopRule` (1, 2, 3, give up; long uptime resets; short sessions spread over 10+ minutes).
- Real manager harness with the launch seam starting a stand-in `cmd.exe`:
  - kill → `PendingCrash` set, state `Stopped`; requested stop → no marker; exit while the gate is held →
    no marker and `DuringMaintenance` true;
  - `RecoverAsync` → `Launched(1)`, runtime `AutoRestarts == 1`, a session registered; kill again three
    more times → `Launched(2)`, `Launched(3)`, `GaveUp`, state `Crashed`, count 3;
  - `Busy` while the lease is held and the counter unchanged; `Stale` for an old request after a manual
    start reset (the old request must not change the new counter); `Disabled` when the row's flag was
    turned off after the exit, and when it is turned off while the launch waits in the queue (a barrier
    before the callback's row read); turned off behind a starter barrier (after the row read) the
    committed launch proceeds → `Launched`; `Skipped` when the gate is taken before the launch;
    `Refused` → `Crashed` with the reason when the starter fails;
  - a manual `StartAsync` that is rejected still turns `Crashed` into `Stopped` and zeroes the count;
  - `DismissCrash` makes a later `RecoverAsync` `Stale`; delete of an instance whose process crashed just
    before the delete, with the delete forced to fail, leaves no marker; restore dismisses the marker;
  - exit ordering: with the database write blocked by a barrier, `StartAsync` is rejected until the exit
    cleanup finishes, and the new process's persisted identity survives;
  - with the exit cleanup blocked the same way, `StopAsync` is rejected as not running, the request
    carries `StopIntent == false`, and the state ends `Stopped`, never `Stopping`; a Stop accepted just
    before the kill yields a request with `StopIntent == true` and no marker;
  - with the exit cleanup blocked in its database write (session still registered), a delete runs
    through its rejection and releases the lease; after the cleanup is unblocked there is no marker and
    `RecoverAsync` is `Stale` (no launch). The same with the cleanup held **after the session was removed
    and before the publishing update** (a barrier on the telemetry/console fake called between the two,
    or a test-only internal hook if no fake sits there);
  - the pure publishing function: an epoch that moved since the capture yields no marker; plus a stress
    test racing `DismissCrash` against exit publication a few thousand times that never ends with a
    marker when the dismissal was issued after the capture;
  - database ordering, both ways and without deadlock: (a) a stop-job mirror (and an identity retry)
    blocked inside its write while holding the session mutex makes the exit cleanup wait; once released,
    the cleanup runs and the row ends `Stopped` with the identity cleared; (b) a writer held **before**
    it takes the mutex resumes after the cleanup, sees `Exiting`, and writes nothing;
  - a probe success held before its runtime update until after the exit cleanup does not change the
    state, and `RecoverAsync` still answers the request;
  - an automatic launch whose identity persist is forced to fail, with the stand-in killed before the
    rejection is handled: the new exit's marker and request survive and are answered by the next
    `RecoverAsync`.
  The barriers are test hooks on the fakes the harness already substitutes (enumerator, RCON client,
  starter) plus an EF command interceptor for the database write; no production hook is added.
- Policy with `FakeProcessManager` (scripted `RecoverAsync` results, `FastTimeProvider`): the filters;
  Busy retried then Launched; Busy for the whole window → the warning, no further calls; console lines per
  result; a throwing `RecoverAsync` does not end the reader; two instances do not block each other
  (barrier on the first); `StopAsync` waits for an in-flight chain (barrier), and a chain in its delay
  ends on the stopping token.
- One end-to-end: real manager + real policy + seam, kill the stand-in with `AutoRestart` on → a new
  session is `Starting` with `AutoRestarts == 1`.
- `RestoreService` accepts a Crashed target; `Presentation`/state view; `SaveAsync` round trip; whitelist
  append keeps `AutoRestart`.
- A real game server cannot be relaunched in CI; that goes on the owner's smoke list (turn it on, end
  `ArkAscendedServer.exe` in Task Manager four times).

## Key decisions & tradeoffs

1. **The guard counts consecutive short-lived sessions instead of the spec's sliding 10-minute window of
   restarts; this is a spec change and is recorded in the plan file.** A modded ASA server can take four
   or five minutes to load. With a 10-minute window and a limit of 3, a server that dies every four
   minutes never has three restarts inside the window and loops forever. The new rule gives the same
   answer for fast loops and also stops slow ones (short-lived sessions spread over more than 10
   minutes still exhaust the budget; that difference is tested). The owner delegated plan decisions for B4 on 2026-10-01.
2. **The policy is a thin hosted service; the decision and every state change sit in one manager method
   under the instance lease.**
3. **`Crashed` is not persisted.**
4. **Busy lock: retry for 2 minutes instead of the spec's immediate drop.** `RconHistoryStore` holds the
   lock for milliseconds on console writes and a backup holds it for its whole run; operations that
   supersede recovery (delete, restore, any manual start) dismiss the marker, so the retry can only ever
   outlast contention that does not.
5. **A refused relaunch becomes `Crashed` with the reason** (port conflict, incomplete restore journal,
   missing executable), so auto-restart never dies silently; maintenance, shutdown, and "turned off" are
   not refusals and leave `Stopped`.
6. **Every non-automatic registration resets the counter**, not only the Start button.
7. **5-second pause before the relaunch**: a plain backoff; the port check and launch validation still
   decide.
8. **One PR.**

## Risks / open questions

- The brief `Stopped` between the exit and the relaunch (5 s, longer while Busy) is visible; a manual
  Start in that gap wins and the request goes Stale.
- The counter and `Crashed` are lost on a service restart (accepted in the original plan).
- `HandleExitAsync`'s reorder keeps a dead session registered for the length of one database write; a
  Stop or Start pressed then is refused, and the probe loop may log one more RCON failure.
- Step 8a touches the stop job, the probe callbacks, and the identity retry: existing, tested code. The
  whole existing process-manager suite must stay green, and the change is behavior-neutral whenever the
  session is still the registered one.
- The seam adds one interface to the launch path purely for tests.

## Out of scope

- Notifications on crash, crash history or statistics, per-instance limits, exit-code heuristics, restart
  on hang (`Unreachable`), auto-restart in the creation wizard, persisting `Crashed`.
