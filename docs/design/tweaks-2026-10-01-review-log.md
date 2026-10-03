<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: October tweaks — live SteamCMD output, editor/console keys, CurseForge links
Started 2026-10-01. MAX_ROUNDS=5.

Note: implementation agents were already running on local branches when this review began (nothing
pushed). Accepted critiques are forwarded to those agents as corrections.

## Round 1 — Codex (thread `<thread-id>`)

The plan still has material correctness gaps.

1. **High — “True order” is stronger than ConPTY can guarantee.** Branch 1 merges terminal output, but cannot reconstruct application writes already buffered before reaching the terminal. An echo-before-pause test does not prove SteamCMD bootstrap ordering.
   **Fix:** Define acceptance as observed terminal order and require timestamped real-SteamCMD captures covering bootstrap, self-update, and installation.

2. **High — Stripping VT sequences is not terminal interpretation.** Removing cursor movement and erase commands can concatenate unrelated text or retain overwritten progress; bare-CR updates may produce no live callbacks until a newline. A 512-column buffer does not establish correctness.
   **Fix:** Use captured SteamCMD output to specify and test cursor/erase handling, live CR progress, wrapping, split UTF-8, and final unterminated text.

3. **High — ConPTY shutdown is underspecified on failure paths.** “Keep draining while closing” covers normal exit, but cancellation or a reader/callback exception can stop draining and deadlock cleanup; retained pipe ends can also prevent EOF. Microsoft explicitly documents these lifetime requirements. [ConPTY lifecycle documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
   **Fix:** Specify handle ownership and teardown for every exit path, keep draining independently of cancellation, and test cancellation under heavy output plus reader failure.

4. **High — The production environment remains outside the validation gate.** The plan acknowledges unresolved session-0 behavior and stdin semantics, while fallback only handles creation failure; a successfully created but stalled session will never fall back.
   **Fix:** Require a disposable LocalSystem service smoke test covering unattended completion and cancellation before making ConPTY the default.

5. **High — Ctrl+S inherits an actual lost-edit race.** In `IniEditor.SaveAsync`, the write uses `_text` before an await, but completion copies the then-current `_text` into `_document` and clears `_dirty`; typing during the save can mark unwritten edits as saved.
   **Fix:** Snapshot owner, file, text, and hash before saving; apply completion only to that document and recompute dirty state against the saved snapshot.

6. **High — History persistence can change command execution semantics.** Appending before sending can block an operational command on disk failure; appending afterward can report failure after successful execution and encourage a dangerous retry.
   **Fix:** Isolate history errors from RCON results, record failed sends explicitly, and show a separate persistence warning without resending.

7. **High — A per-instance lock alone does not prevent stale history overwrites.** Two browser circuits can each hold an old history snapshot; serializing writes still loses commands if either writes its snapshot.
   **Fix:** Use one shared store that rereads, appends, deduplicates, caps, and atomically replaces under the same application-wide per-instance lock.

8. **High — History deletion can race with an outstanding send.** `InstanceDeleteService` uses `IInstanceLocks`; the proposed history lock is separate, allowing a late append to recreate history after deletion.
   **Fix:** Define a `DataRootLayout` history path and coordinate append/existence checks with deletion using a documented lock order and explicit cleanup.

9. **Medium — Instance switching can mix histories.** `InstancePage` performs asynchronous operations using mutable `Id`, and `ConsolePanel` currently retains history position and command text across channel changes.
   **Fix:** Capture the instance ID per operation, discard stale load completions, and reset history navigation and suggestions when the channel changes.

10. **Medium — The history format has undefined destructive edge cases.** Embedded CR/LF creates extra entries; silently truncating commands beyond 512 characters makes recalled commands differ from what was sent; an enormous hand-edited file can defeat the intended cap.
    **Fix:** Specify bounded reads, reject multiline history entries, and skip oversized commands with feedback instead of truncating them.

11. **Medium — Suggestion navigation has no defined entry action.** Arrows navigate suggestions only after navigation has started, but Tab accepts and arrows otherwise browse history; the transition is circular.
    **Fix:** Specify an explicit activation gesture, preserve ordinary Tab behavior when no suggestion is selected, and test the complete keyboard state machine.

12. **Medium — “Manual mods stay unlinked” contradicts the refresher.** `ModMetadataRefresher` fetches every library entry; the schema has no manual-origin discriminator, so a matching manual ID will acquire a URL.
    **Fix:** Either allow API-resolved manual entries to gain links or explicitly add and honor provenance.

13. **Medium — URL-only backfill can cause perpetual writes.** Testing “URL missing” independently treats entries whose API URL is absent or rejected as changed every day.
    **Fix:** Compare the stored URL with the normalized nullable API URL and update only when those values differ.

VERDICT: REVISE

### Claude's response
Context: the branch 1 agent finished its first pass (commit 2faf4f8) while this round ran. Measured with a
throwaway SteamCMD: pipes burst everything at 15.4 s; under ConPTY lines stream and the bootstrap lines
arrive first (0.04–0.5 s), before the banner. A 15.5 GB download streamed progress every ~2 s.

- 1 (true order) — accepted. Acceptance is observed terminal order, backed by timestamped real-SteamCMD
  captures (done: app 1007 and a 15.5 GB app, including a cancelled and resumed run).
- 2 (VT stripping) — partly accepted. Tests use a captured real SteamCMD frame, chunk sizes 1–7, split
  UTF-8, bare CR, cursor-forward, flush at end. Rejected: emitting partial lines for bare-CR progress;
  SteamCMD's progress lines end in newlines (seen in the captures), so there is nothing to show early.
- 3 (teardown) — accepted. Sent to the agent: handle ownership and close order documented, draining
  independent of cancellation and of callback exceptions, tests for cancel under heavy output and a
  throwing callback.
- 4 (session 0) — partly accepted. Running the harness as SYSTEM was blocked by the permission
  classifier and stays with the owner. Added instead: fallback to the pipe launcher when CreateProcessW
  fails, and a configuration switch that forces the pipe launcher without a redeploy. The alpha service
  on the owner's box is the smoke environment; the owner watches one update after deploy.
- 5 (IniEditor lost-edit race) — accepted; real bug on main. Snapshot before await, recompute dirty.
- 6 (history vs. send semantics) — accepted. Send first, record afterwards, history errors isolated.
- 7 (stale snapshot overwrite) — accepted. Store rereads and appends under an app-wide per-instance lock.
- 8 (delete race) — accepted. Path from the layout type; never recreate the instance folder.
- 9 (instance switching) — accepted.
- 10 (format edge cases) — accepted. No CR/LF, skip oversized, bounded read.
- 11 (keyboard state machine) — accepted; explicit rules sent to the agent.
- 12, 13 (manual mods, perpetual writes) — pending. The owner questioned the migration; the likely
  outcome is linking by mod id with no stored URL, which removes both issues. Awaiting his decision
  before round 2.

## Round 2 — Codex

Round 1 resolves much of the original review, but these material problems remain:

1. **High — The backfill backup is unsafe on the live database.** The revision says “copies the database file first,” but `DatabaseInitializer` enables WAL. Copying only `.db` can omit committed changes, including the new migration; the repo’s `SqliteConfigBackupExporter` already documents and solves this. [SQLite WAL documentation](https://www.sqlite.org/wal.html)  
   **Fix:** Use SQLite’s online backup API and verify the backup before making updates.

2. **High — Text/hash snapshots do not fully fix the editor race.** The override omits document identity: `IniEditor.OnParametersSetAsync` can load another owner while the old save awaits, after which that save can overwrite the new document’s text/hash and dirty state.  
   **Fix:** Capture owner, file, and a load-generation token alongside text/hash, and discard completion effects when the generation changes.

3. **High — “Never creates the instance folder” does not serialize history with deletion.** `InstanceLayoutService.RemoveAllExceptSaved` enumerates and deletes entries before removing the directory; an append can create a temp/history file between enumeration and final directory removal, causing deletion to fail after partial teardown.  
   **Fix:** Make coordination with deletion mandatory, using a shared lease and an existence check inside it; test append versus delete.

4. **High — The new process-start fallback can hang during cleanup.** In [PseudoConsoleSteamCmdLauncher.cs](../../src/ArkAscendedServerAdmin.Infrastructure/Install/PseudoConsoleSteamCmdLauncher.cs:77), failed startup calls `console.Dispose()` before any reader exists; disposal closes ConPTY before closing the output pipe. That violates the documented drain-or-close requirement on affected Windows versions. [Microsoft teardown guidance](https://learn.microsoft.com/en-us/windows/console/closepseudoconsole)  
   **Fix:** Close the output reader before ConPTY when no drain thread exists, and test injected startup failure with a bounded completion deadline.

5. **Medium — Reader failures are still disguised as normal EOF.** `Drain` catches every `IOException` without logging, and `RunAsync` waits only for process exit before observing the reader; unexpected output failure can silently remove live diagnostics for the rest of an update.  
   **Fix:** Distinguish expected pipe closure from read failures, observe reader failure while the child runs, and report output loss immediately.

6. **Medium — The backfill transaction’s timing is unspecified.** If it wraps API calls as well as updates, a write transaction can block application writes for network-duration periods; a deferred read transaction can instead fail when promoted after another writer commits. Testing only against an idle copy misses both cases.  
   **Fix:** Fetch and validate API results before opening a short write transaction, then perform conditional `WHERE WebsiteUrl IS NULL` updates with bounded busy handling.

The owner-run session-0 smoke test and pipe switch are a reasonable accepted deployment tradeoff. The remaining findings above are separate correctness issues.

VERDICT: REVISE

### Claude's response
Owner decision recorded before this round: keep the migration, back-fill out-of-band by script.
All six points accepted and sent to the agents.
- 1 — backup via SQLite online backup API, verified (integrity_check, column present, row count) before writing.
- 2 — load-generation token captured with owner/file/text/hash; stale completions apply nothing.
- 3 — history append takes the same per-instance lease as deletion and checks the folder inside it; append-vs-delete test.
- 4 — dispose order fixed when no reader exists (close output read end before the pseudo console); injected start-failure test with a deadline.
- 5 — unexpected read failures are logged and announced as one console line; SteamCMD keeps running; exit code preserved.
- 6 — API calls first, then one short BEGIN IMMEDIATE transaction with conditional updates and a busy timeout; tested against a held write lock.

## Round 3 — Codex

Round 2 addresses the remaining material findings: WAL-safe backup, short backfill transactions, stale-save protection, history/delete synchronization, startup-failure teardown, and visible reader failures.

No further material plan flaws found. This approves the revised plan; implementation still needs the specified regression tests and owner-run session-0 smoke test.

VERDICT: APPROVED

Converged after 3 rounds. Conditions carried forward: the regression tests named in the plan, and the owner-run session-0 smoke test after deploy.
