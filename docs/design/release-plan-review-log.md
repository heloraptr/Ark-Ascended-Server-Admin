<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: Public 0.0.1 release
Act 1 (grill) complete — plan locked with the user on 2026-09-13. MAX_ROUNDS=5.
PLAN_FILE=docs/.untracked/RELEASE-PLAN.md (the repo's PLAN.md is the MVP plan and is untouched).

## Round 1 — Codex (thread `<thread-id>`)

The plan still has material security and installation problems. These are the changes I would require before implementation:

1. **High — The settings ACL does not protect the authentication keys (B10).** `ServiceExtensions.cs` persists Data Protection keys under `DataRoot\keys` without encryption or an explicit directory ACL. Readable keys can decrypt captured cookies—including the proposed password verifier. Writable app files also become LocalSystem code execution. Explicit key persistence disables default encryption at rest. [Microsoft documentation](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0)  
   **Fix:** Secure the app and sensitive data directories before copying or starting anything, and explicitly protect the key ring with DPAPI.

2. **High — Password rotation has a login race (A1).** `LoginService.LoginAsync` verifies the password, then separately reads `CurrentHash` for the cookie; a reload between those operations lets an old-password login receive the new password’s claim. PBKDF2 increases the vulnerable interval.  
   **Fix:** Have verification return the immutable credential snapshot it verified, and issue the cookie using that snapshot.

3. **High — “Wrong-format fallback” is unsafe and underspecified (A1).** If a configured hash becomes malformed and authentication falls back to the retained plaintext password, an old credential silently becomes valid again. Unbounded iteration counts also permit excessive computation.  
   **Fix:** When `PasswordHash` is present, reject malformed or unsupported values without plaintext fallback; validate iteration bounds and exact salt/hash lengths.

4. **High — `/MIR` and uninstall can destroy user data (B10–11).** Arbitrary paths permit `InstallDir = DataRoot`, a data directory beneath `InstallDir`, source/destination overlap, or an unrelated existing folder; these invalidate the promise that worlds and keys survive.  
   **Fix:** Canonicalize and validate paths, reject unsafe overlaps and reparse points, and require an installation ownership marker before mirroring or deleting.

5. **High — An existing service is insufficient evidence of an upgrade target (B10).** Re-running with the default directory after a custom installation can copy files somewhere new while starting the service’s old executable. Concurrent installer runs can also interleave stop/copy/start operations.  
   **Fix:** Serialize installation operations and derive the existing location from the service’s quoted executable path plus installation metadata, rejecting mismatches.

6. **High — Upgrade has no recovery strategy (B10).** A failed mirror leaves a partially replaced installation; startup can then migrate SQLite before a later failure, making binary-only rollback unsafe.  
   **Fix:** Stage and validate before stopping, retain the previous application and a consistent database backup, and specify recovery from copy, migration, and startup failures.

7. **High — Hashing transport is unspecified (A1/B10).** A secure prompt offers little protection if the installer passes the resulting plaintext as an executable argument, where process inspection or command logging can expose it.  
   **Fix:** Send the password through redirected standard input, keep stdout hash-only, and reject nonzero exit codes or malformed output.

8. **Medium — `-SetPassword` does not actually remove plaintext (B10–12).** Rewriting only `PasswordHash` retains the old `Password`; a direct JSON rewrite also risks partial reloads, truncation, and changed ACLs.  
   **Fix:** Atomically replace the settings file with preserved restrictive ACLs, remove the plaintext key, and detect higher-precedence configuration that would override the change.

9. **High — The health probe cannot prove installation success (B10).** Plain HTTP receives the expected guard rejection in two modes and cannot probe the TLS endpoint in `LanHttps`; `/setup` is also affected by readiness and authentication. An unrelated listener can supply a response.  
   **Fix:** Probe a dedicated application-identifying liveness endpoint using the configured scheme, verify the installed certificate for local TLS probing, and fail installation on timeout while reporting startup logs.

10. **Medium — The default install does not yield a usable URL (B10/D19).** Loopback mode requires an already-configured local HTTPS proxy, but the installer only collects the backend bind; opening the printed HTTP URL yields 403.  
    **Fix:** Collect and document the external HTTPS URL and proxy prerequisite, or default the standalone installation path to `LanHttps`.

11. **Medium — `LanHttps` has misleading exposure and certificate behavior (B10).** Its firewall rule accepts any source on every profile; adding an HTTPS endpoint also leaves the base `Http` endpoint configured. IP addresses need IP-address SAN entries, not merely DNS-name strings.  
    **Fix:** Default to an explicit LAN source scope, configure the complete intended endpoint set, and generate correctly typed DNS/IP SANs.

12. **Medium — CI runs the wrong build configuration (B9/C13).** The plan builds `Release` but specifies `dotnet run --no-build` without `-c Release`; on a clean runner that looks for nonexistent Debug outputs. The current deploy script already handles this correctly.  
    **Fix:** Pass the same explicit configuration and project paths to every build, test, and publish invocation.

13. **Medium — Release authentication and failure handling are missing (C14).** `gh release create` needs token wiring and repository write permission; PowerShell native-command failures must also stop packaging, while `robocopy` requires its special success-code handling.  
    **Fix:** Specify job-scoped `contents: write`, `GH_TOKEN`, checked native exit codes, and `if: always()` for test-result upload.

14. **Medium — File version cannot supply the promised release version (B9).** Numeric file version loses prerelease identifiers and build metadata, so the proposed RC rehearsal and UI version will disagree with archive names. [MinVer documentation](https://github.com/adamralph/minver/blob/main/README.md)  
    **Fix:** Read informational version or MinVer’s semantic version directly, and verify that its release portion matches the triggering tag.

15. **Medium — “Three restarts a day” is incorrect (B10).** SCM repeats the final configured failure action; three restart entries therefore permit an ongoing restart loop, and the reset period measures failure-free time. [Microsoft protocol documentation](https://winprotocoldoc.z19.web.core.windows.net/MS-SCMR/%5BMS-SCMR%5D-190923.pdf)  
    **Fix:** Append a final no-action entry and describe the actual failure-counter reset semantics.

16. **Medium — Public-install compatibility remains untested (B/E).** Testing on the owner’s development VM does not establish either runtime independence or compatibility with Windows PowerShell 5.1; the existing probe uses `-SkipHttpErrorCheck`, which requires newer PowerShell.  
    **Fix:** Declare the PowerShell requirement or implement 5.1 compatibility, then smoke-test both extracted ZIPs on clean Windows machines through install, password change, upgrade, and uninstall.

17. **High — Making the repository public lacks a history review gate (A6/E).** Replacing identifiers in current files leaves previous copies in Git history; ignored-file status also says nothing about earlier commits, tags, or published artifacts.  
    **Fix:** Review tracked history and release artifacts for credentials and personal data before changing visibility, rotating exposed secrets and removing unwanted history as necessary.

VERDICT: REVISE
### Claude's response to round 1

Accepted (plan revised): 1 key ring (ProtectKeysWithDpapi + ACL on keys\ and Data\); 2 verify returns the
snapshot it checked and the claim is issued from it; 3 malformed PasswordHash refuses all logins, no
plaintext fallback, iteration and length bounds; 4 path canonicalization, no overlap, no reparse points,
install.json marker required before touching an existing folder; 5 InstallDir derived from the service's
BinaryPathName, mismatch refused, named mutex; 6 rename-swap upgrade with .previous, database copy while
stopped, documented recovery; 7 password piped over stdin to --hash-password, hash-only stdout, exit code
checked; 8 -SetPassword removes Password, atomic write, ACL reapplied, env-override warning; 9 new
anonymous /healthz exempt from guard and readiness, thumbprint-pinned HTTPS probe, non-zero exit on
failure; 10 Loopback prints the proxy instruction instead of a 403 URL (default kept: it is the only mode
that opens nothing); 11 LocalSubnet source scope, IP SANs via the 2.5.29.17 text extension, the residual
loopback Http endpoint set explicitly and documented; 12 -c Release on every dotnet invocation; 13
contents: write, GH_TOKEN, if: always(), native exit-code checks; 14 version from
-getProperty:MinVerVersion with a tag assertion; 15 two restarts then stop, reset semantics described;
16 scripts target Windows PowerShell 5.1 and pwsh 7, clean-machine smoke test added as step 15; 17
history gate added as step 26 with the owner's decision on git filter-repo.

Narrowed: 6 — no separate "validate before stopping" beyond the path checks and the staged copy; the
service must be stopped before the database can be copied consistently, so the sequence is copy-new →
stop → swap → copy-db → start, with reversal on failure before start. 9 — the probe stays on loopback;
the LanHttps external address is not probed because the firewall scope may exclude the installer's own
address. 11 — the residual Http endpoint is kept rather than fought: configuration merging cannot remove
a key from the base file, and a guard-protected loopback listener is harmless.

## Round 2 — Codex

The revision addresses many findings, but introduces a default-path contradiction and still leaves upgrade/security gaps.

| Prior finding | Status | Assessment |
|---|---|---|
| 1. Directory/key protection | **Partial** | DPAPI added; app-directory ACLs and upgrade-time hardening remain missing. |
| 2. Password-rotation race | Addressed | Verification returns the credential snapshot used for the claim. |
| 3. Malformed-hash fallback | Addressed | Explicit rejection and bounded parameters. |
| 4. Destructive path handling | **Partial** | New checks reject the default installation and omit ancestor/sibling hazards. |
| 5. Existing-service identity/concurrency | **Partial** | Service-path discovery and installer mutex added; uninstall coordination and legacy migration remain unclear. |
| 6. Upgrade recovery | **Partial** | Rename rollback and database backup added, but backup retention and recovery paths need correction. |
| 7. Password argument exposure | Addressed | Standard input replaces command-line arguments; encoding introduces a separate issue below. |
| 8. Password update semantics | **Partial** | Plaintext removal added; atomic replacement and ACL preservation are not established. |
| 9. Health probe | **Partial** | Dedicated endpoint added; upgrade probing still depends on first-install variables. |
| 10. Unusable default URL | Addressed | Proxy prerequisite is now explicit; retaining this default is a reasonable choice. |
| 11. LAN exposure/certificate SANs | Addressed | Restricted source scope, typed SANs, and explicit listeners. |
| 12. Test configuration | Addressed | Release configuration supplied consistently. |
| 13. Release permissions/failure handling | Addressed | Token, permissions, native exit checks, and unconditional artifact upload specified. |
| 14. Version extraction | **Partial** | Proposed MSBuild command does not execute MinVer’s target. |
| 15. SCM recovery semantics | Addressed | Terminal no-action entry and reset semantics corrected. |
| 16. Clean-machine compatibility testing | Addressed | Both packages, shells, and lifecycle operations are covered in the plan. |
| 17. Public-history gate | Addressed | Explicit owner decision and final rescan gate; decision remains pending. |

Concrete remaining problems and fixes:

1. **B12 rejects its own defaults.** It prohibits `InstallDir` being contained by `DataRoot`, but defaults to `C:\ArkAscendedServerAdmin\App` under `C:\ArkAscendedServerAdmin`; B14’s `<DataRoot>\App` also fails.  
   **Fix:** Allow a dedicated app child beneath `DataRoot`, while prohibiting equality, data beneath the app directory, and overlap with managed data subdirectories.

2. **B12 still does not secure executable files or existing installations.** Only first-run `keys` and `Data` receive directory ACLs; upgrading the existing VM never applies that hardening. Database backups under `Backups\_app` can also expose the plaintext CurseForge key.  
   **Fix:** Apply verified restrictive ACLs to app/staging directories, keys, database files, and database backups on both install and upgrade before writing sensitive contents.

3. **Path safety stops at the leaf.** A parent directory can be a junction even when the final directory is not; `.new` and `.previous` are also used or deleted without their own ownership checks. The shipped `install.json` proves package contents, not ownership of an arbitrary existing directory.  
   **Fix:** Validate ancestors and all staging/rollback paths, reject source overlap, and use installer-created identity metadata before deleting existing directories.

4. **Legacy migration and uninstall remain inconsistent.** The current dev installation has no `install.json`, so the nonempty-directory rejection blocks the promised upgrade; uninstall does not explicitly take the shared mutex.  
   **Fix:** Define a validated one-time legacy adoption path and acquire the same mutex for installation, password/certificate changes, and uninstall.

5. **Database backup names collide.** `<old-version>` is reused for retries and same-version upgrades—the latter explicitly appears in the smoke test—so the only recovery copy can be overwritten. Removing `.previous` at the next attempt also discards recovery state before success.  
   **Fix:** Use unique transaction directories, retain the last successful recovery set until commit, and document restoration of the complete SQLite file set without stale WAL/SHM files.

6. **“Atomic” settings replacement is not sufficiently specified.** `Move-Item -Force` is not an explicit atomic-replacement guarantee across both shells; applying ACLs afterward creates an exposure window, especially for the PFX password and retained plaintext settings.  
   **Fix:** Create the temporary file with its final restrictive ACL, use same-volume `File.Replace` for an existing destination, and preserve the old file on failure.

7. **Upgrade probing has no certificate “just issued.”** Upgrades preserve settings and may use a replacement certificate or custom port, but the probe refers to installation-time scheme/port and the newly issued certificate.  
   **Fix:** Derive probe configuration and certificate pin from the installed configuration on every run, and verify that the expected service process owns the responding listener.

8. **MinVer extraction remains wrong.** `-getProperty:MinVerVersion` without a target only evaluates the project; it does not execute targets. The fallback also conflates `AssemblyName` with informational-version metadata and may compare a SHA-bearing product version directly against a tag. [MSBuild documentation](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)  
   **Fix:** Restore first, explicitly execute MinVer’s target before reading its property, and define one consistent tag/version comparison.

New findings:

9. **Password input is silently altered.** `--hash-password` trims the password, while login verification need not; Windows PowerShell’s native-pipeline encoding can also alter non-ASCII passwords. [PowerShell encoding documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)  
   **Fix:** Preserve password whitespace, reject unsupported line breaks explicitly, and use explicit UTF-8 redirected input with Unicode/whitespace round-trip tests.

10. **Runtime checking comes after invoking the runtime-dependent executable.** The listed hashing step runs the FDD executable before checking whether its runtime exists, defeating the intended friendly failure.  
    **Fix:** Check the required x64 runtimes before invoking any packaged executable.

11. **`-SetCertificate` is promised but undefined.** It appears in the certificate description but not the parameter list or transaction logic; expiry recovery therefore has no implementable workflow.  
    **Fix:** Specify certificate renewal, atomic configuration update, service reload/restart, probe, and rollback—or remove the switch promise and document manual renewal.

12. **Expired plaintext Data Protection keys remain usable.** A2 says they stay valid “until they expire,” but expiration does not prevent unprotecting existing payloads. [Microsoft key-management documentation](https://learn.microsoft.com/en-ca/aspnet/core/security/data-protection/implementation/key-management?view=aspnetcore-8.0)  
    **Fix:** Correct the claim and explicitly choose between retaining ACL-protected legacy keys or rotating/revoking them during migration with a forced sign-in.

VERDICT: REVISE
### Claude's response to round 2

All twelve accepted; the plan now: 1 allows InstallDir as a direct child of DataRoot except the managed
names, forbids DataRoot inside InstallDir, checks ancestors for reparse points, applies the rules to .new
and .previous-* too; 2 ACLs on InstallDir, .new, and DataRoot (SYSTEM/Administrators full, Users RX) plus
SYSTEM/Administrators-only on keys, Data, Backups\_app, applied and read back on every run; 3 an
installer-written install.json marker with an installId, .new/.previous-* deleted only with a matching
id; 4 legacy adoption when the service's binary path points into the folder, the mutex taken by every
operation including uninstall; 5 timestamped _app copies and .previous-<ts>, older ones pruned only after a
successful probe, restore described as the whole db/-wal/-shm set; 6 temp file with final ACL then
File.Replace with a .bak kept until the probe passes; 7 probe reads scheme, port, and thumbprint from
the installed settings and checks the listening socket's owning process; 8 -restore -t:MinVer before
-getProperty, ProductVersion fallback, version without build metadata compared to the tag; 9 no trimming,
one trailing line terminator stripped, UTF-8 without BOM through Process.StandardInput, Unicode and
whitespace round-trip test; 10 runtime check moved before any packaged exe runs; 11 -SetCertificate
specified (re-issue or import, pfx swap, settings rewrite, restart, probe, rollback); 12 claim corrected,
unprotected key files deleted on the migration upgrade with one forced sign-in.

## Round 3 — Codex

Round-2 findings **1, 3, 4, 5, 7, 8, 10, and 12 are addressed at plan level**: default paths, path ownership, legacy adoption/mutex, recovery retention, installed-config probing, version extraction, runtime-check ordering, and legacy-key removal.

**2, 6, 9, and 11 remain partially addressed:**

1. **Secret-file ACL gaps remain (#2/#6, B12).** Settings are written into a `Users:RX` directory *before* their temporary-file ACL is tightened, contradicting “no window.” Upgrades also copy production settings without explicitly preserving their restrictive ACL. Separately, `SettingsCommands` exports the database—including the CurseForge key—to `DataRoot\Exports`, which remains readable by Users.  
   **Fix:** Create secret files with restrictive ACLs from inception, preserve those ACLs during upgrade, and protect `Exports` like `Data` and `_app`.

2. **The hashing implementation breaks Windows PowerShell 5.1 (#9, B12).** Its .NET Framework `ProcessStartInfo` has no `StandardInputEncoding` property. [Microsoft reference source](https://raw.githubusercontent.com/microsoft/referencesource/main/System/services/monitoring/system/diagnosticts/ProcessStartInfo.cs)  
   **Fix:** With `UseShellExecute=false`, write explicit UTF-8 bytes to `StandardInput.BaseStream`, then close stdin so the child’s read-to-EOF completes.

3. **Certificate replacement still has a configuration/file race (#11, B12).** Publishing the new configuration before moving the PFX can trigger reload against a nonexistent path or the old PFX with the new password; if the configured “new path” is `web.pfx.new`, the subsequent rename leaves it nonexistent permanently.  
   **Fix:** Write a uniquely named final PFX first, atomically point configuration at it, restart/probe, and retain the old PFX until success.

New material issues:

4. **Path validation permits taking ownership of unrelated directories.** `DataRoot=C:\` or an existing shared directory passes the stated rules, after which the installer replaces its ACL. Installation-marker checks protect only `InstallDir`.  
   **Fix:** Reject volume roots and system directories; require a dedicated empty DataRoot or validated adoption of an existing application data root.

5. **`-NoStart` contradicts unconditional start/probe/rollback.** A deliberately stopped installation cannot pass the mandatory probe, and failure handling can restart a service the user requested remain stopped.  
   **Fix:** Define `-NoStart` as a separate completion path that leaves the service stopped, retains recovery artifacts, and reports verification as pending.

6. **`-SetPassword` cannot establish success through `/healthz`.** That endpoint ignores authentication, so it succeeds even when an environment override leaves the old password active or configuration reload has not applied the replacement.  
   **Fix:** Reject effective password-hash overrides for this operation and verify that the running service has adopted the new credential before declaring success or deleting the backup.

VERDICT: REVISE
### Claude's response to round 3

All six accepted: 1 settings temp file created empty with its security descriptor before content is
written, upgrade copies the settings file the same way, Exports protected like Data; 2 UTF-8 bytes on
StandardInput.BaseStream then close (no StandardInputEncoding, which .NET Framework lacks); 3 PFX files
written under unique names and never renamed after configuration points at them, old one deleted only
after the probe; 4 volume roots, system folders, profile roots and their ancestors refused, DataRoot must
be empty, absent, or the one the existing service's settings name; 5 -NoStart is its own completion path
keeping every recovery artifact, plus -Verify to finish later; 6 -SetPassword refuses environment
overrides, restarts the service, and confirms adoption with a login round trip before deleting the .bak.

## Round 4 — Codex

Round-3 findings **1–4 are addressed at plan level**: secret-file ACLs, PowerShell 5.1 hashing, certificate replacement ordering, and protection against adopting unrelated directories.

**5 and 6 remain partially addressed:**

1. **Password verification fails in Loopback and Proxy modes.** B12 sends `GET/POST /login` directly over the configured HTTP scheme. `HttpsGuardMiddleware` returns 403; only `/healthz` receives the new exemption. A valid password change therefore gets rolled back.  
   **Fix:** Specify a loopback-only verification client that sends trusted `X-Forwarded-Proto: https`, handles antiforgery/session cookies correctly, and disables automatic redirects; retain the HTTPS guard.

2. **Deferred verification loses the password-adoption check.** `-SetPassword -NoStart` retains recovery files, but `-Verify` only runs the health probe before cleanup. It can discard the previous settings without confirming the new credential works.  
   **Fix:** Either reject `-SetPassword -NoStart`, or persist the pending operation type and require password verification before its cleanup.

One new material issue:

3. **Repeated operations can destroy pending recovery state.** After `-NoStart` or a failed upgrade, another upgrade is permitted without resolving the pending transaction. It can snapshot the already-upgraded installation and then delete the older, known-working application/database pair during successful cleanup. Repeated settings operations similarly reuse `.bak`.  
   **Fix:** Record pending transactions and refuse further mutations until verification or rollback completes; make cleanup retain the last verified application/database pair.

VERDICT: REVISE
### Claude's response to round 4

All three accepted: 1 the login round trip runs from a loopback client sending X-Forwarded-Proto: https
(loopback is always a trusted proxy in Program.cs, so the guard is untouched), antiforgery cookie and
token echoed, redirects disabled, 302 plus auth cookie = adopted; 2 -SetPassword -NoStart refused; 3 a
pending.json transaction record written before any mutation and cleared only after verification, other
mutations refused while it exists, -Verify and -Rollback as the two exits, cleanup limited to artifacts
named in the record plus older sets not named in it.

## Round 5 — Codex

Round-4 findings **1 and 2 are addressed**: the login check now supplies trusted forwarded-protocol headers and explicitly handles cookies/redirects; `-SetPassword -NoStart` is rejected.

**Finding 3 remains incomplete.** The new transaction mechanism introduces two material recovery failures:

1. **The pending record moves away during upgrade.** B12 writes `InstallDir\pending.json`, then renames that entire directory to `.previous-<ts>`. The replacement installation has no pending record. After interruption or failed verification, another mutation can proceed and destroy the recovery pair—the original failure returns.  
   **Fix:** Store the journal in a stable, ACL-protected location outside the swapped directories, and update it atomically with completed transaction phases so rollback handles interrupted steps.

2. **`-Verify` can still bypass password verification after interruption.** If `-SetPassword` stops after replacing settings but before completing its login check, the pending record survives. The specified `-Verify` path checks only liveness, then deletes the backup and clears the transaction without proving credential adoption. Rejecting `-NoStart` does not cover this interruption case.  
   **Fix:** For pending password changes, require the new password and repeat the login check before committing, or permit only rollback.

VERDICT: REVISE
### Claude's response to round 5 (MAX_ROUNDS reached)

Both accepted and folded into the plan without a further Codex pass: 1 the journal moves to
DataRoot\keys\install-pending.json (stable, ACL-protected, untouched by the InstallDir swap) with a phase
field rewritten atomically after each step so -Rollback undoes exactly the completed steps; 2 -Verify is
operation-specific and, for a pending -SetPassword, prompts for the new password and repeats the login
round trip before committing, never committing on liveness alone.

### Resolution

Not converged within 5 rounds. The last two Codex points were accepted verbatim; no disagreement is
outstanding, but the final text has not been re-reviewed. The owner signs off on the plan as written.

Owner signed off 2026-09-13 after two further changes (wording "time and bandwidth"; Appendix B smoke test protocol for Proxmox VMs). History rewrite with git filter-repo decided.

Owner signed off 2026-09-13 after two further changes (wording "time and bandwidth"; Appendix B smoke test protocol for Proxmox VMs). History rewrite with git filter-repo decided.
