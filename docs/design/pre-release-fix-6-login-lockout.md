<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: login lockout that holds under parallel requests, and a cap on password hashing (solution-review finding 6)
_Round 3 revision by Claude, after Codex round 3_

Branch: `fix/pre-release-v1`. Source review: `docs/.untracked/code-review-ArkServerAdmin-202610020855.md`.

## Goal

`LoginService.LoginAsync` checks the lockout (`LoginThrottle.GetLockoutEnd`), then runs the password
verify (synchronous PBKDF2-SHA256, 600,000 iterations, roughly 0.3 s), then records the failure
(`LoginThrottle.RecordFailure`). Requests from one address that arrive while earlier ones are still
verifying all pass the lockout check, so "5 failures then a 5-minute lockout" is really "5 plus
whatever was in flight". There is also no limit across addresses: every unauthenticated POST to
`/login` buys one PBKDF2 run on the machine that hosts the game servers. After this change an address
can never have more than 5 unresolved-or-failed attempts in the window, at most 2 password verifies
run at any moment for the whole app, and at most a fixed number of requests wait for one.

The app has one password and one owner. The login page is a static server-rendered form
(`Login.razor`) and may be internet-facing behind a reverse proxy; the client key is
`HttpContext.Connection.RemoteIpAddress` after forwarded-header processing from configured proxies.

## Approach

1. **`LoginThrottle` (Core, singleton) hands out attempt handles.**
   - `Entry` gains `int InFlight`.
   - New `LoginAttempt? TryBeginAttempt(string clientKey, out DateTimeOffset lockedUntil)`, one lock
     section: expire the addressed entry only (drop failures older than `Window`, clear a lockout that
     has ended); if the address is locked out, return null with the real lockout end; if
     `Failures.Count + InFlight >= MaxFailures`, return null with `now + LockoutDuration` (a display
     value only: no lockout is stored, because the in-flight attempts may still succeed); otherwise
     `InFlight++` and return a handle.
   - `LoginAttempt` is a sealed class holding the throttle, the client key and **the `Entry` object it
     reserved on**. It completes exactly once (an `Interlocked` flag); later calls are no-ops.
     - `Fail()` returns `DateTimeOffset?`: decrements that entry's `InFlight`, records a failure on that
       entry, and applies the lockout rule as `RecordFailure` does today.
     - `Succeed()`: decrements that entry's `InFlight` and clears that entry's failures and lockout. It
       does **not** discard other attempts' reservations.
     - `Dispose()` (abandon): decrements that entry's `InFlight` and records nothing, for an attempt
       that ended with neither answer (canceled request, exception, `Busy`).
   - Because a handle acts on the entry object it reserved on, a completion can never decrement a
     different entry that was created later under the same key. An entry is removed from the dictionary
     only when it has no failures, no lockout and `InFlight == 0`.
   - The existing public `GetLockoutEnd`, `RecordFailure` and `RecordSuccess` stay for the existing
     tests and behave as today, except that `RecordSuccess` clears failures and lockout and removes the
     entry only when `InFlight == 0`.
   - **Bounded cleanup.** The full-dictionary `Prune` no longer runs on every call. It runs at most
     once per `SweepInterval` (1 minute, tracked by a last-sweep timestamp under the same lock) and
     never removes an entry with `InFlight > 0`. Every call still expires the one entry it addresses.

2. **Global verify gate with a hard cap on waiters (Core, in `LoginThrottle`).**
   - `SemaphoreSlim(MaxConcurrentVerifies = 2)` plus a count of requests that are waiting or hashing,
     capped at `MaxConcurrentVerifies + MaxQueuedVerifies` (2 + 8).
   - `Task<VerifyPermit?> TryEnterVerifyAsync(CancellationToken)`: admission is a compare-and-swap loop
     (read the count; if it is at the cap return null immediately; otherwise
     `Interlocked.CompareExchange` to count + 1 and retry on a lost race), so the check and the
     increment are one atomic step and the cap cannot be overshot. Then wait for the semaphore up to `VerifyQueueTimeout`
     (10 s, through the injected `TimeProvider`). On timeout or cancellation, decrement and return null
     (timeout) or rethrow (cancellation). On success return a permit whose single `Dispose` releases
     the semaphore and decrements the count.
   - It lives on the throttle because the throttle is the singleton that already owns login rate state;
     `LoginService` is scoped.

3. **`LoginService.LoginAsync` order of operations.**
   1. Build one token linked from the caller's `cancellationToken` and `HttpContext.RequestAborted`,
      because `Login.razor` calls `LoginAsync` without a token; use it for every wait below.
   2. Not configured: unchanged (returns `NotConfigured`, no throttle interaction).
   3. `TryBeginAttempt`; null returns `LockedOut(lockedUntil)` with the existing warning log. No hash runs.
   4. `using` the attempt handle, so every exit that did not call `Fail` or `Succeed` abandons it.
   5. `TryEnterVerifyAsync(linked)`; null returns the new `LoginResult.Busy` with a warning log. The
      attempt is abandoned, not counted as a failure.
   6. With the permit, run `passwordSource.Verify(password)` through `Task.Run` **without** a
      cancellation token and await it directly (no `WaitAsync`), releasing the permit in a `finally`
      once it returns. From the moment the hash starts, the request stays until the hash finishes and
      its result is recorded on the attempt (`Fail` or `Succeed`), even if the client has disconnected:
      a disconnect must not buy a hash that is never counted. Only an attempt whose hash never started
      (refused, `Busy`, or canceled while queued) is abandoned. After recording, a canceled request
      stops (`ThrowIfCancellationRequested` on the linked token) before the failure delay or the
      sign-in.
   7. Wrong password: `attempt.Fail()`, log as today, then the existing 1 s delay (on the linked token,
      outside the gate), then `InvalidPassword` or `LockedOut`.
   8. Right password: `attempt.Succeed()`, then sign in exactly as today.

4. **New outcome.** `LoginOutcome.Busy` and `LoginResult.Busy`, message
   "The server is busy. Try again in a moment." `Login.razor` renders `LoginResult.Message` for
   non-success outcomes; confirm it has no exhaustive switch that needs a new arm.

5. **Tests.**
   - Unit (`LoginThrottleTests`, extend):
     - begin succeeds up to `MaxFailures` in flight and the next is refused with no lockout stored;
     - failures plus in-flight share the limit; `Fail` and abandon each free exactly one slot; an
       abandoned attempt records no failure; a handle completed twice changes nothing the second time;
     - **overlapping success:** five attempts in flight, one succeeds: exactly one new attempt is
       admitted, not five, and a further one is refused; the four old handles then fail: four failures
       are recorded and **no** lockout is stored; the new attempt then fails and the real lockout is
       stored;
     - **entry lifecycle:** an entry with outstanding handles is never removed (by success, sweep or
       expiry); once every handle has completed and the entry is removed, a new entry created under the
       same key is unaffected by a second completion or disposal of the old handles;
     - an entry with an attempt in flight survives a sweep; an idle entry is removed by the sweep; the
       sweep does not run more than once per `SweepInterval`;
     - a locked-out address reports its real lockout end.
   - Unit (gate): admits 2; a third waits and is admitted on release; with 2 hashing and 8 waiting the
     next call returns null at once; a waiter returns null after `VerifyQueueTimeout` on a manually
     driven clock and the count returns to its prior value; a canceled waiter leaves the count
     unchanged. Many simultaneous callers at the boundary (2 hashing, 7 waiting) admit exactly one.

   - Integration (new `LoginServiceTests`; the Server project is already referenced by the integration
     tests):
     - 20 parallel wrong-password logins from one address run the hash at most `MaxFailures` times and
       the rest return `LockedOut`; afterward the address is locked out;
     - parallel logins from many addresses never exceed 2 concurrent hashes; with the gate full, further
       ones return `Busy` and leave no in-flight count behind;
     - `HttpContext.RequestAborted` firing while queued ends the request, runs no hash, and leaves no
       in-flight count; firing mid-hash with a wrong password still records the failure once the hash
       finishes, the permit is held until then, and no sign-in or failure delay follows;
     - a correct password still signs in and clears the address's failures.
   - To observe "hash ran N times" and "at most 2 at once" without timing guesses, `LoginService` takes
     the verify step through a small internal seam (an `internal` constructor overload taking a
     `Func<string, string?>`) that the tests replace with a counting, signal-controlled fake.
     Production wiring is unchanged.

## Key decisions & tradeoffs

- **Reserve-then-verify instead of one big lock around check, verify and record.** A single lock would
  serialize all logins app-wide behind 0.3 s hashes and hold a lock across CPU work. The reservation
  keeps the lock sections tiny and makes the per-address limit exact.
- **Handles bound to the entry object, completed once.** This is what makes success, failure and
  abandonment safe when attempts from one address overlap or outlive the entry.
- **A refused in-flight overflow reports `LockedOut` but stores no lockout.** If the five in-flight
  attempts are the owner double-clicking with the right password, nothing should be locked afterward.
  If they fail, their own `Fail` calls trigger the real lockout.
- **Success clears failures but not other reservations.** A correct password from an address proves
  nothing about its other outstanding attempts.
- **Global cap of 2 hashing plus 8 waiting, bounded wait, instead of ASP.NET Core rate limiting
  middleware.** No new middleware or configuration surface; the cap sits exactly on the expensive call.
  Cost: during a flood the owner's login can wait up to 10 s or get `Busy`. Accepted by the owner.
- **`Busy` does not count as a failure.** Otherwise a flood from other addresses could lock the owner
  out without the owner ever submitting a wrong password.
- **Order: per-address reservation before the global gate.** A flood from one address is refused
  cheaply before it can queue on the gate; only distinct addresses can occupy the queue, and the queue
  is capped.
- **A started hash is never canceled, keeps its permit, and is always counted.** PBKDF2 here is
  synchronous and cannot be interrupted; releasing the permit early would let more than 2 run at once,
  and dropping the result on disconnect would let an attacker guess without failures being recorded.
  Cost: a disconnected request lingers for the rest of its hash, about 0.3 s.

## Risks / open questions

- During a sustained flood from many addresses the owner mostly sees `Busy`. The hard cap trades
  availability of the login page for bounded resource use; the game servers and existing sessions are
  unaffected.
- The throttle dictionary can hold one entry per distinct address seen in the last window; entries are
  small and are swept once a minute. No hard cap on entry count is added.
- Addresses behind one NAT or one reverse proxy without forwarded headers share a key and therefore a
  limit. Unchanged from today.
- `RemoteIpAddress` null maps to the shared key "unknown". Unchanged from today.

## Out of scope

- Changing `MaxFailures`, `Window`, `LockoutDuration`, the 1 s failure delay, or the PBKDF2 cost.
- Rate limiting any endpoint other than the login post.
- CAPTCHA, IP allowlists, or persistent lockout state across service restarts.

## Added gate test (from Codex round 4, approved)

Race a semaphore release against a waiter's timeout and against its cancellation, repeatedly. After
each race both permits must still be obtainable and the admission count must be back at zero: a
waiter that is granted the semaphore at the same moment it times out or is canceled must either keep
the permit and return it, or release it, never leak it.
