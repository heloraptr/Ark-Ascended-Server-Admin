<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Pre-release review: decisions

Source: `docs/.untracked/code-review-ArkServerAdmin-202610020855.md` (finding numbers match that
report). Branch: `fix/pre-release-v1`. Squash-merged to main as `c0e0aed` (PR #67) on 2026-10-02; not yet deployed. UI checked on 5080 (screenshots in `ui/`).

| # | Finding | Decision | State |
|---|---------|----------|-------|
| 1 | Scheduled-action tick can strand an instance lock | Fix now, abort the tick | Done, uncommitted; test added |
| 2 | Background loops can stop the host | Fix now, per-loop catch, shared readiness wait, Codex sign-off | Done, uncommitted; plan and Codex log in this folder |
| 3 | Service and game servers run as LocalSystem | Defer the code; SECURITY.md paragraph now; suppress in REVIEW.md | Paragraph and REVIEW.md entry done; issue #61 filed |
| 4 | `ProcessManager` size | Defer; suppress in REVIEW.md | REVIEW.md entry done; issue #62 filed |
| 5 | Backup zip and verify synchronous under the lock | A: zip, verify and restore pre-check hashing moved to the thread pool; B (release the lock early) deferred; C (compression level) dropped | A done, uncommitted, backup tests pass; issue #63 filed for B |
| 6 | Login lockout bypass with parallel requests | Fix now, both parts (per-address reservation, global hashing cap), Codex sign-off | Done, uncommitted; plan and Codex log in this folder; not checked in a browser |
| 7 | CurseForge search has no page cap | B: cap at 100, stop on an empty or short page, note when results were cut off | Done, uncommitted; not viewed in a browser |
| 8 | Null CurseForge logo throws | Folded into finding 2 | Done, uncommitted |
| 9 | Firewall rule names collide between installs | A: install tag in the rule name, tag file for the uninstaller, Codex sign-off | Done, uncommitted; elevated firewall tests ran and passed; manual checks below still to do |
| 10 | N+1 backup queries and un-split include | Defer, low priority | Issue #64 filed |
| 11 | Retention prune drops the row when the archive delete fails | Fix now: keep the row, warn, retry next prune | Done, uncommitted; test added |
| 12 | Pages bypass the guarded facades | Defer, with the ProcessManager split | Issue #65 filed |
| 13 | Session cancellation source never disposed | A: dispose the source after cancel; WriteMutex left alone | Done, uncommitted; existing tests pass |
| 14 | British spellings | Fix now, one pass over src, docs/guide and tests | Done, uncommitted; zero left in tracked files |
| 15 | Override edit does not check the instance | Fix now: match on instance, Fail instead of throw, validate the file enum | Done, uncommitted; test added |

## Deferred work

- **Finding 3 (issue #61, large):** run the game servers and SteamCMD under a low-privilege account; keep elevated rights
  only for firewall, WMI and junction work. Needs process launch under a different token, DataRoot
  ACLs, installer and uninstaller changes, and a real-server retest.
- **Finding 4 (issue #62, large):** split `ProcessManager` into collaborators; its own branch, no behavior
  change, real-server retest afterward.

- **Finding 5B (issue #63):** release the instance lock once the snapshot is copied; needs a design for
  restore, delete and prune running alongside.

## Open follow-ups from finished fixes

- Finding 2: a malformed CurseForge response is reported as "CurseForge could not be reached: …";
  wording left as is, owner has not said whether to change it.
- UI not yet viewed in a browser: the "server is busy" login message (finding 6) and the search cut-off
  note on the Mods page and the mod list editor (finding 7). Do one local run on 5080 for all UI changes.
- Finding 2: only the `BackupScheduler` loop tests were confirmed to fail against the old code.

## Manual checks the owner still has to run (finding 9, firewall rule names)

**One-time cleanup of old-style rules (this machine only, elevated PowerShell).** Matches only
`ArkAscendedServerAdmin-<digits>`; tagged names have a second dash and the web rule's display name is
different, so neither can match. Run it after the new build is deployed and the live instance has been
started once (so its tagged rules exist), or before the deploy and then restart the instance.

```powershell
Get-NetFirewallRule -DisplayName 'ArkAscendedServerAdmin-*' | Where-Object { $_.DisplayName -cmatch '^ArkAscendedServerAdmin-\d+$' } | Remove-NetFirewallRule
```

(Checked with `-WhatIf` on 2026-10-02: it selected exactly the two `ArkAscendedServerAdmin-14` rules.)

**Uninstall retry and upgrade, on a scratch install** (`-ServiceName` set to a scratch name, its own
InstallDir and DataRoot):
1. Install. Create a scratch instance and start it. Confirm `<InstallDir>\firewall.tag` exists and both
   UDP rules exist under `ArkAscendedServerAdmin-<tag>-<id>`:
   `Get-NetFirewallRule -DisplayName "ArkAscendedServerAdmin-$(Get-Content <InstallDir>\firewall.tag)-*"`.
   Without the rules the later checks prove nothing.
2. Upgrade with `install.ps1 -NoStart`. Confirm the new `<InstallDir>\firewall.tag` holds the same tag.
3. Run `uninstall.ps1 -ServiceName <scratch> -SimulateFirewallFailure`. Confirm it stops with an error,
   the service is still registered (stopped), and InstallDir still exists.
4. Run `uninstall.ps1 -ServiceName <scratch>` again. Confirm it completes, prints
   "2 instance firewall rule(s) removed (ArkAscendedServerAdmin-<tag>-*)", and no rules for that tag remain.

**Real machine:**
1. Start a dev instance (5080, dev DataRoot) whose id matches a live instance.
2. Confirm both sets of rules exist with different tags and the live rules still have their ports.
3. Delete the dev instance. Confirm only the dev-tagged rules went.
4. Before the next live deploy, confirm `firewall.tag` appears in `<DataRoot>\App` after the service starts.

## Findings 16–30 (decided 2026-10-02, coded as one batch from `BATCH-brief.md`)

| # | Finding | Decision | State |
|---|---------|----------|-------|
| 16 | Resume maintenance reports success when nothing resumed | Fix now: reject when an operation is in progress; register with DetachedJobs | Done, uncommitted |
| 17 | SQLite connection strings by interpolation | Fix now: SqliteConnectionStringBuilder; dispose the ClearPool connection | Done, uncommitted |
| 18 | Dead code from the original CurseForge import | Delete the four files; move ICurseForgeApi beside CurseForgeApi | Done, uncommitted; the dead `ModExtensionsTests` went with them |
| 19 | EOS id lookups cannot use the index | Defer, low priority | Issue #66 (shared with 27) |
| 20 | Comment defects (typo, HANDOVER reference) | Fix now | Done, uncommitted |
| 21 | Custom map key flows into filesystem paths | Fix now: `[A-Za-z0-9_]+` plus a stays-under-root guard in DataRootLayout | Done, uncommitted |
| 22 | Generated INI readable by every local user | Fix now, no Codex: protect `Instances` and `Clusters` in the installer ACLs | Done, uncommitted; elevated ACL test ran; scratch-install check below |
| 23 | Anonymous /Error page in the main layout | Fix now: login layout | Done, uncommitted; on the UI verification list |
| 24 | ClusterCommands downcasts IInstanceCommands | Fix now: shared IniSeeder | Done, uncommitted |
| 25 | Scheduled-action runs pruned only at start | Fix now: daily prune from the tick loop | Done, uncommitted |
| 26 | Stale INI editor recreates a deleted owner's folder | Fix now: owner existence check before the write | Done, uncommitted; slug-cache eviction on delete skipped (needs new injections); the existence check is the fix |
| 27 | DateTimeOffset workarounds | Defer, low priority | Issue #66 (shared with 19) |
| 28 | Whitelist validation differs | Fix now: one shared validator | Done, uncommitted |
| 29 | Port-conflict name skip | Fix now: drop it | Done, uncommitted |
| 30 | Firewall rules not scoped to the executable | Drop; suppressed in REVIEW.md | Done |

Additional manual check from finding 22 (scratch install): after `install.ps1`, confirm `icacls <DataRoot>\Instances`
and `icacls <DataRoot>\Clusters` show no `BUILTIN\Users` entry while `<DataRoot>` itself still does.

## Batch notes (2026-10-02)

- 16: `IUpdateService` gained `IsOperationInProgress`; a resume refused during shutdown says
  "The service is stopping; the resume was not started."
- 21: the `DataRootLayout` guard checks that a segment stays inside the folder it is joined to (stricter
  than "under Root", which `..\..\x` from `Saved\<slug>` would still satisfy).
- 22: not tested on a data root whose `Instances\<slug>` already holds junctions into `Server\`; the ACL
  push-down should not follow junctions, but confirm on the live box after the next install or upgrade.
- 24: `IniSeeder` is public (the facade constructors are public), registered scoped.
- 26: if the database cannot be read during the owner check, the save proceeds with a warning, so the
  "file wins over the database" rule holds.
- 29: two tests that relied on the name skip were rewritten to exclude by id.
- 20: whitelist path confirmed live on 2026-10-02 (the live server, `ShooterGame\Saved\AllowedCheaterAccountIDs.txt`);
  the comment now says so.
- Full run after the batch and the dead-code deletion: build clean, 827 unit, 448 integration.

## Close-out (2026-10-02)

- PR #67 squash-merged as `c0e0aed`; PR #68 (test error-box fix, found when the deploy's test run stalled
  behind the Windows hard-error box) merged as `a788f8f`. Deployed; all tests passed in the deploy run.
- `firewall.tag` confirmed in the deployed site. Old-style firewall rules removed after the live server's restart (step 1).
- Live `Instances`/`Clusters` ACL tightening skipped on purpose: single-user box, owner's call. It will
  happen on the next `install.ps1` upgrade anyway.
- Scratch-install check (upgrade keeps the tag, uninstall retry, icacls) to be done in the smoke VM.
- Dev-vs-live same-id firewall check not run; the elevated two-root test covers the same path.
