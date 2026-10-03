<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: background loops must not stop the host (finding 2, with warning 8)
Started 2026-10-02. MAX_ROUNDS=5. Plan file: `docs/.untracked/fix-pre-release-v1/PLAN-finding-2.md`.

## Round 1 — Codex

Two gaps need revision:

1. **Detached scheduled-action completion still loses database errors silently.** [`CompleteAsync`](../../src/ArkAscendedServerAdmin.Infrastructure/Scheduling/ScheduledActionRunner.cs:354) catches only `DbUpdateException` and `InvalidOperationException`. A query-time `SqliteException` escapes into the detached `RunOneAsync` task, bypasses the proposed tick catch, and leaves the run `Started` without logging the recording failure. This is the same observability problem step 4 addresses for detached backups.
   **Fix:** Widen `CompleteAsync`’s exception handling after its shutdown-cancellation catch and test that a completion-query failure is logged.

2. **The timer-test fallback cannot establish recovery.** [`FastTimeProvider.CreateTimer`](../../test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests/Fakes.cs:40) advances time and queues callbacks immediately; `Advance` only changes the clock. Continuous hosted loops therefore run as fast as callbacks and dependencies permit. Checking `ExecuteTask` state after advancing time can pass before the failing tick even executes, and does not prove a second attempt or interval spacing.
   **Fix:** Use a manually driven timer provider with explicit first-failure and second-attempt signals; assert no retry before the interval, successful retry afterward, and clean cancellation, without the task-state-only fallback.

The catch-per-loop approach and nullable-logo change otherwise fit the reported failures. Neither finding requires changing the finished `RunTickAsync`/`TryClaimAsync` fix.

VERDICT: REVISE

### Claude's response

Both points accepted.

1. `ScheduledActionRunner.CompleteAsync` joins step 4: its filter is widened after the shutdown-cancellation catch, with a test that a failing completion write is logged and does not fault the detached task.
2. The task-state-only fallback is removed. Loop tests use a manually driven time provider with explicit signals for the first failing tick and the second attempt, and assert no retry before the interval, a retry after it, and clean cancellation.

## Round 2 — Codex

The revision resolves both previous findings: `CompleteAsync` gains failure logging, and loop tests now require observed retries under manually controlled time. No remaining material blocker in the proposed scope.

One test detail needs care: an attempt signal can arrive before the loop registers its next timer, making clock advancement race with the continuation. **Fix:** Wait for timer registration before advancing time; for `ScheduledActionRunner`, assert retry at the next minute boundary rather than a full minute after failure.

VERDICT: APPROVED

### Claude's response

Approved in round 2. The test note is taken into the plan: `ManualTimeProvider` exposes a way to wait for the next timer registration, tests wait for it before advancing the clock, and the `ScheduledActionRunner` retry is asserted at the next minute boundary.
