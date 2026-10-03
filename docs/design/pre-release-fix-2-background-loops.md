<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: background loops must not stop the host (solution-review finding 2, with warning 8)
_Round 1 revision by Claude, after Codex round 1_

Branch: `fix/pre-release-v1`. Source review: `docs/.untracked/code-review-ArkServerAdmin-202610020855.md`.

## Goal

The Windows service supervises the game servers. Three timer-driven `BackgroundService` loops
(`BackupScheduler`, `ModMetadataPoll`, `ScheduledActionRunner`) catch only a short list of exception
types. `HostOptions.BackgroundServiceExceptionBehavior` is not set anywhere, so the default (`StopHost`)
applies: any other exception escaping `ExecuteAsync` shuts the whole service down. Known ways to get
there: a query-time `SqliteException` (a `DbException`, not a `DbUpdateException`), a `JsonException`
from a malformed CurseForge response, and a `NullReferenceException` when CurseForge returns
`"logo": null`. `BackupScheduler` also runs before the startup pipeline is Ready, so after a failed
migration its first tick hits missing tables. After this change a failed tick is logged and retried on
the next tick, the backup timer waits for readiness, and a null logo is handled as "no thumbnail".

## Approach

1. **Shared readiness wait.** Add `ReadinessMonitorExtensions.WaitUntilReadyAsync(this IReadinessMonitor
   readiness, CancellationToken)` in `src/ArkAscendedServerAdmin.Core/Startup/` (next to
   `IReadinessMonitor` in `ReadinessState.cs`). The body is the existing private method, unchanged in
   behavior: return at once when `Current.IsReady`; otherwise subscribe to `Changed`, re-check
   `Current.IsReady` after subscribing, await a `TaskCompletionSource` with
   `RunContinuationsAsynchronously` via `WaitAsync(cancellationToken)`, and unsubscribe in `finally`.
   Delete the private copies in `ScheduledActionRunner` and `CrashPolicy` and call the extension.

2. **`BackupScheduler` waits for readiness.** Add an `IReadinessMonitor` constructor parameter and call
   `WaitUntilReadyAsync(stoppingToken)` once at the top of `ExecuteAsync`, before the loop, inside a
   try that returns on cancellation of the stopping token. Readiness that never arrives (failed
   startup) means the timer never ticks, which is the intended result. `RunDueBackupsAsync` itself is
   unchanged, so tests that drive it directly keep working.

3. **Last-resort catch in the three timer loops.** In `BackupScheduler.ExecuteAsync`,
   `ModMetadataPoll.ExecuteAsync` and the tick loop of `ScheduledActionRunner.ExecuteAsync`, keep the
   existing `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }`
   and replace the type-list filter with
   `catch (Exception ex) when (!stoppingToken.IsCancellationRequested)`, logging at Error with the
   same messages as today. The `Task.Delay` stays inside the try, at the top of each iteration, so a
   failing tick cannot spin: the next attempt is one interval later.

4. **Same widening for the four adjacent narrow filters that share the problem.**
   - `ScheduledActionRunner.CompleteAsync` (records the outcome of a detached run, currently
     `DbUpdateException or InvalidOperationException`): after its shutdown-cancellation catch, any other
     exception is logged with the existing "Could not record the outcome" message. Today a query-time
     `SqliteException` faults the detached task unlogged and the run stays `Started` until the next
     service start marks it Interrupted.
   - `ScheduledActionRunner.ExecuteAsync` start-of-service block (`WaitUntilReadyAsync` +
     `RecoverAsync`, currently `DbUpdateException or DbException or InvalidOperationException`): any
     non-cancellation exception is logged and the tick loop still starts.
   - `BackupScheduler.RunOneAsync` (detached per-instance backup task, currently a four-type list): any
     non-cancellation exception is logged. Today an unlisted exception faults a task nobody observes,
     so it is silent.
   - `ModMetadataRefresher.RefreshAsync`'s catch around `curseForge.GetModsAsync` gains `JsonException`
     so a malformed response is reported as a failed refresh (`CommandResult.Fail`) to both the poll and
     the interactive Refresh button, instead of throwing through the facade.

5. **Null logo (warning 8).** In `Core/CurseForge/Models/Mods/Mod.cs` change `Logo` to
   `ModAsset? Logo { get; set; }` (dropping the `= new()` default, which System.Text.Json overwrites
   with null anyway). Fix every dereference the compiler then flags:
   `ModMetadataRefresher.cs:59,63`, `ModCommands.cs:130,207`, `ModelExtensions.cs:23`, each becoming
   `mod.Logo?.ThumbnailUrl` (the existing `NullIfEmpty` already maps null to null). Check the
   Components project for any use of `Logo` and fix the same way.

6. **Not changed.** `HostOptions.BackgroundServiceExceptionBehavior` stays at the default.
   `StartupOrchestrator` and `CrashPolicy` keep their current exception handling (only `CrashPolicy`'s
   readiness wait moves to the shared helper).

7. **Tests** (xunit v3; run with `dotnet run --project <test csproj>`).
   - Unit, Core: `WaitUntilReadyAsync` returns at once when already Ready; completes when `Changed`
     reports Ready; throws `OperationCanceledException` on cancellation; leaves no subscriber behind in
     all three cases.
   - Unit, Core: deserializing a CurseForge mod payload with `"logo": null` yields `Logo == null`
     without throwing.
   - Integration: `ModMetadataRefresher.RefreshAsync` with a fake `ICurseForgeApi` returning a mod whose
     `Logo` is null saves the other fields and stores a null thumbnail; with a fake that throws
     `JsonException` it returns `Fail` and writes nothing.
   - A new test fake, `ManualTimeProvider`, whose timers fire only when the test advances the clock
     past their due time. The existing `FastTimeProvider` fires every timer at once, so a hosted loop
     under it free-runs and cannot show interval spacing.
   - Integration: `BackupScheduler` started through `StartAsync` under `ManualTimeProvider` does not
     tick while readiness is not Ready, even after the clock passes a tick, and ticks once readiness
     reports Ready and the clock passes the next tick.
   - Integration: for each of the three loops, started through `StartAsync` under
     `ManualTimeProvider`, with a dependency that throws an unlisted exception type on the first tick
     (for example `NotSupportedException`) and signals each attempt through a `TaskCompletionSource`:
     the first attempt is observed and logged as an Error; no second attempt happens before the clock
     advances a full interval; the second attempt is observed after it does; `StopAsync` then completes
     without throwing. Assertions wait on the attempt signals, never on `ExecuteTask` state alone.
   - Integration: a scheduled action whose completion write fails with a non-listed exception logs the
     "Could not record the outcome" error and the detached task completes without faulting
     (`WhenIdleAsync` does not throw).
   - Existing tests that construct `BackupScheduler` get the new constructor argument.

## Key decisions & tradeoffs

- **Per-loop catch-all instead of `BackgroundServiceExceptionBehavior = Ignore`.** With `Ignore` a loop
  that throws stays dead while the service looks healthy (no more backups, no more scheduled restarts,
  nobody notices). A per-loop catch keeps the loop alive and logs every failure. Cost: a persistent
  fault logs one Error per minute (backup, schedules) or per day (mod poll).
- **Catch-all filter is `!stoppingToken.IsCancellationRequested`, not `ex is not OperationCanceledException`.**
  An `OperationCanceledException` that is not the stopping token (an HTTP timeout surfacing as
  `TaskCanceledException`) is a tick failure and must be logged and retried, not treated as shutdown.
  During shutdown, a non-cancellation exception is left unhandled and takes the default path, which is
  harmless because the host is already stopping.
- **Fatal exceptions are not special-cased.** `OutOfMemoryException` and similar would also be caught
  and logged. The alternative (an explicit deny-list) adds code for cases where the process is unlikely
  to survive anyway.
- **`StartupOrchestrator` and `CrashPolicy` left alone.** The orchestrator has its own failure reporting
  through the readiness state; `CrashPolicy` reads a channel rather than ticking, and its work is done
  in detached per-instance chains. Widening them is a separate question, not part of this finding.
- **Extension method in Core rather than a base class.** The three services already inherit
  `BackgroundService` and take `IReadinessMonitor`; an extension avoids a new base type.
- **`Logo` becomes nullable instead of enabling `RespectNullableAnnotations`.** Turning that option on
  would make a null logo a deserialization failure for the whole response, which is worse than treating
  it as "no thumbnail". `Links` is already handled with `?.` at its call sites.

## Risks / open questions

- A persistent database fault now produces an Error log line every minute from two loops instead of
  stopping the service. Accepted: the service staying up is the point, and the log is the signal.
- `BackupScheduler` gains a constructor dependency; DI registration resolves it by type, but hand-built
  instances in tests need updating.
- The loop tests need a new manually driven time provider in the test project; it is test-only code
  and no production package is added.
- Making the completion write fail in a test needs a seam: the runner takes
  `IDbContextFactory<AppDbContext>`, so the test wraps the factory and throws on the call that follows
  the action's execution.
- If the only existing `Mod.Logo` consumer in `ModelExtensions.cs` is dead code (review finding 18,
  not yet decided), it is still patched here so the build stays green; removing it is a separate item.

## Out of scope

- Review findings other than 2 and 8 (N+1 queries in `RunDueBackupsAsync`, backup zip under the lease,
  CurseForge search paging, dead mod-service types, and the rest).
- Any change to `Program.cs` host options, `StartupOrchestrator`, or `CrashPolicy` exception handling.
- Database schema or migrations (none are needed).

## Test timing note (from Codex round 2, approved)

An attempt signal can arrive before the loop registers its next timer. `ManualTimeProvider` therefore
exposes a way to wait until a timer has been registered, and each loop test waits for that before
advancing the clock. `ScheduledActionRunner` delays to the next minute boundary, not a full minute
after the failure, so its retry is asserted at that boundary.
