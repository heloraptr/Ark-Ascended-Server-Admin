<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: login lockout under parallel requests and a cap on password hashing (finding 6)
Started 2026-10-02. MAX_ROUNDS=5. Plan file: `docs/.untracked/fix-pre-release-v1/PLAN-finding-6.md`.

## Round 1 — Codex

1. **High — success destroys other requests’ reservations.** Plan §1 removes the entry on success. With five attempts outstanding, one success erases the other four; five new attempts can then enter, leaving nine unresolved. Old completions can also decrement the replacement entry’s counters. “Does not throw or go negative” tests miss this.
   **Fix:** Clear failure history on success but decrement only that attempt’s reservation, retaining the entry while other attempts remain; test overlapping success, new admission, and old completions.

2. **High — real request cancellation never reaches the gate.** [Login.razor:66](../../src/ArkAscendedServerAdmin.Components/Pages/Login.razor:66) calls `LoginAsync` without a token. Consequently, disconnected requests can remain queued and subsequently consume hashes despite the plan’s claim that request aborts bound their lifetime.
   **Fix:** Link `HttpContext.RequestAborted` with the caller token inside `LoginService`, test that path, and retain the semaphore permit until any already-started synchronous hash actually finishes.

3. **High — the global queue remains an unbounded resource-exhaustion path.** Ten seconds limits residence time, not simultaneous requests, reservation entries, timers, or retained passwords. The repository configures no explicit Kestrel connection cap, and a connection cap would not itself bound concurrent login requests over multiplexed connections.
   **Fix:** Set a hard global admission/queue limit and immediately return `Busy` when full, releasing any per-address reservation.

4. **Medium — pruning becomes attacker-controlled work under the global lock.** Every proposed admission invokes the existing [full-dictionary scan](../../src/ArkAscendedServerAdmin.Core/Auth/LoginThrottle.cs:69). Distinct-address requests now create entries before hashing, and in-flight entries survive pruning; admitting N such requests can therefore require quadratic aggregate scanning while serializing all throttle operations.
   **Fix:** Expire the addressed entry locally and perform bounded, periodic global cleanup rather than scanning the dictionary on every attempt.

VERDICT: REVISE

### Claude's response

All four points accepted.

1. Attempts become handles bound to their own entry object and completed exactly once. Success clears the
   failure history and releases only that attempt's reservation; the entry stays while other attempts
   are outstanding. Tests cover overlapping success, new admission and late completions.
2. `LoginService` links `HttpContext.RequestAborted` with the caller's token. A hash that has started
   keeps its permit until it finishes, even if the request is canceled meanwhile.
3. The gate gets a hard cap on waiters (`MaxQueuedVerifies`); when full, the result is `Busy` at once and
   the per-address reservation is released.
4. Admission expires only the addressed entry. The full-dictionary sweep runs at most once per minute.

## Round 2 — Codex

The revision addresses the original success-accounting issue and adds useful queue limits, but material problems remain.

1. **High — disconnecting mid-hash bypasses failure accounting (§3.6).** `WaitAsync(linked)` ends the request and abandons its reservation while verification continues. Repeated disconnects therefore buy hashes without recording failures, and unresolved hashes disappear from the per-address count.
   **Fix:** Once verification starts, retain the attempt until its result is recorded, even after disconnect; simplest is to await the hash directly and abandon only canceled attempts that never started.

2. **High — queue admission describes a check-then-increment race (§2).** An `Interlocked` counter alone does not make “if below cap, increment” atomic: concurrent callers can all observe nine and enter, exceeding ten.
   **Fix:** Specify a compare-and-swap admission loop or a lock covering the check and increment, and test simultaneous admissions at the boundary.

3. **Medium — the overlapping-success test expects the wrong lockout (§5).** After one success clears history, four old failures produce four failures; the newly admitted attempt remains outstanding. Further admission should be refused, but a stored lockout requires that fifth attempt to fail.
   **Fix:** Assert no stored lockout after the four failures, then fail the new attempt and assert the real lockout.

4. **Medium — the recycled-entry test contradicts the lifecycle invariant (§5).** An outstanding handle prevents removal through `InFlight > 0`; an already-completed handle must do nothing. A first completion against a recycled entry should therefore be unreachable.
   **Fix:** Test that outstanding handles prevent recycling, then complete and remove the entry, recreate the key, and verify duplicate completion/disposal cannot affect it.

VERDICT: REVISE

### Claude's response

All four points accepted.

1. Once the hash starts, the request awaits it to the end and records the result on the attempt, even
   if the client has disconnected. Only attempts whose hash never started are abandoned.
2. Queue admission is a compare-and-swap loop on the count, with a test for simultaneous admissions at
   the boundary.
3. The overlapping-success test now asserts no stored lockout after the four old failures, then fails
   the new attempt and asserts the real lockout.
4. The recycled-entry test is replaced: outstanding handles prevent removal; after completion and
   removal, a recreated key is unaffected by duplicate completion or disposal of the old handles.

## Round 3 — Codex

**High — §3.6 still specifies the disconnect bypass.** It retains `WaitAsync(linked)` and explicitly abandons the attempt when the client disconnects mid-hash. That contradicts both the revised integration test and the decision that started hashes are always counted. Implementing §3.6 still lets repeated disconnects consume hashes without accumulating failures.

**Fix:** Await the started hash directly without cancellation, retain its attempt and permit through completion, record `Fail()` or `Succeed()`, then check cancellation before sign-in or the failure delay.

The other previously identified issues are addressed.

VERDICT: REVISE

### Claude's response

Accepted. This was an editing miss, not a disagreement: the round 2 revision updated the test and the decision but left the old text in step 3.6. Step 3.6 now says the started hash is awaited directly, its result is recorded, and cancellation is checked afterward.

## Round 4 — Codex

No remaining material blockers. Step 3.6 now retains reservations and records results after disconnects; atomic admission, bounded queuing, and the revised lifecycle tests address the earlier findings.

One test hardening suggestion: race semaphore release against timeout/cancellation, then verify both permits remain usable and the admission count returns to zero.

VERDICT: APPROVED

### Claude's response

Approved in round 4. The test suggestion is added to the plan as a required gate test.
