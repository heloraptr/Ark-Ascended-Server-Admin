<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan Review Log: firewall rule names that do not collide between installs (finding 9)
Started 2026-10-02. MAX_ROUNDS=5. Plan file: `docs/.untracked/fix-pre-release-v1/PLAN-finding-9.md`.

## Round 1 — Codex

Material issues remain:

1. **Step 5 leaves the firewall selector dangerously ambiguous.** The existing web-rule helper uses PowerShell `-Name`, but `Name` and `DisplayName` are distinct properties; copying that helper can leave app-created rules untouched. The planned tests never exercise uninstall cleanup. [Microsoft reference](https://learn.microsoft.com/en-us/powershell/module/netsecurity/get-netfirewallrule).  
   **Fix:** Explicitly select app rules by `-DisplayName`, pipe the selected objects to removal, and test cleanup against rules created by `FirewallRules`.

2. **The uninstaller can hash a different DataRoot than the app.** [ServiceExtensions.cs](../../src/ArkAscendedServerAdmin.Server/ServiceExtensions.cs:20) resolves relative roots against the app’s content root; `Get-InstalledSettings` resolves them against the uninstaller’s working directory and expands environment variables under the invoking user. Identical configured text can therefore produce different tags.  
   **Fix:** Persist the app’s effective absolute root/tag for uninstall, or enforce identical resolution rules and supported configuration constraints.

3. **Cleanup trusts potentially stale configuration.** [uninstall.ps1](../../install/uninstall.ps1:48) silently falls back to the marker when settings cannot be read; edited settings can also describe a root the service never used. Cleanup can miss existing rules or target another installation’s tag.  
   **Fix:** Record the tag actually used by the installation and warn explicitly when its ownership cannot be established.

4. **Uninstall cleanup is placed after service deletion.** If firewall removal fails, uninstall exits with the app folder remaining, but rerunning immediately fails because the service is absent. Adding instance-rule deletion creates another failure point in this already fragile sequence.  
   **Fix:** Complete firewall cleanup after stopping the service but before deleting it, or make partially completed uninstall safely resumable.

5. **The proposed PowerShell verification does not execute PowerShell.** A known-answer comment plus a C# assertion cannot detect a broken PowerShell hash implementation or incorrect deletion filter.  
   **Fix:** Add executable PowerShell vector tests and an elevated cleanup test proving both target rules disappear while another installation’s rules remain.

6. **The interface change misses a concrete implementer.** [FakeFirewall](../../test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests/Processes/ProcessManagerFakes.cs:123) implements `IFirewallRules`; adding the required member without updating it breaks compilation.  
   **Fix:** Include all interface implementers in the changes and build the full solution.

VERDICT: REVISE

### Claude's response

All six points accepted.

1. The uninstaller selects by `-DisplayName` (the COM rule name the app sets is the DisplayName in
   NetSecurity terms) and pipes the selected objects to `Remove-NetFirewallRule`; an elevated test
   exercises it against rules created by `FirewallRules`.
2. and 3. The app persists the tag it actually uses (`<DataRoot>\firewall.tag`, written at startup
   once the layout is known). The uninstaller reads that file; only if it is missing does it compute
   the tag from the resolved root, and then it says so in a warning. No more parallel resolution of
   configuration text.
4. Instance-rule cleanup runs after the service is stopped and before it is deleted, next to the
   web-rule removal, which moves to the same point if it is not there already.
5. Executable PowerShell checks: a test dot-sources `ArkInstall.Common.ps1` and runs the tag function
   on known vectors that the C# test also asserts; the elevated cleanup test calls the real removal
   function and checks that another installation's rules survive.
6. `FakeFirewall` in `ProcessManagerFakes.cs` and any other implementer are updated; the whole
   solution is built.

## Round 2 — Codex

The revision addresses the missing interface implementer and adds executable tests, but material problems remain:

1. **The tag-file lookup is circular.** Step 4 puts `firewall.tag` inside the DataRoot that the uninstaller cannot reliably resolve. A relative path, service-account environment variable, or edited configuration still makes uninstall read the wrong folder—or another installation’s tag.  
   **Fix:** Store the effective tag in installation-owned metadata at a stable location discoverable from the validated service executable directory.

2. **Steps 4 and 5 contradict each other.** Step 4 says uninstall reads the recorded tag; step 5 still mandates recomputing it, while the tradeoffs introduce an unspecified fallback. That fallback reintroduces the ownership error the file supposedly fixes.  
   **Fix:** Specify one authoritative lookup algorithm; when ownership metadata is missing or invalid, warn and skip instance-rule deletion instead of guessing.

3. **The new tag file has no validation contract.** If its contents are interpolated into a wildcard selector, a malformed value such as `*` broadens deletion across installations. Atomic writes do not validate contents.  
   **Fix:** Require an exact eight-character lowercase hexadecimal tag before constructing any selector, and test empty, malformed, and wildcard-containing files.

4. **The PowerShell selector remains unspecified.** Step 5 still says “name,” beside a web-rule helper that uses `-Name`; it never explicitly requires the `DisplayName` selector for app-created rules.  
   **Fix:** Specify `Get-NetFirewallRule -DisplayName "ArkAscendedServerAdmin-$tag-*"` and pipe the returned objects to removal.

5. **The uninstall failure sequence remains broken.** Cleanup is still placed beside web-rule removal, after `sc.exe delete`; a cleanup failure leaves an incomplete uninstall that cannot be retried through the script’s service-existence guard.  
   **Fix:** Stop the service, complete firewall cleanup, then delete the service, and test a cleanup failure followed by retry.

6. **The tests bypass the problematic ownership handoff.** The cleanup test supplies a known tag directly; the startup test merely checks file contents. Neither catches uninstall locating the wrong file or silently falling back.  
   **Fix:** Test the actual metadata lookup and cleanup path with relative roots, changed settings, missing metadata, and another installation’s rules present.

VERDICT: REVISE

### Claude's response

All six points accepted. Points 2, 4 and 5 were partly an editing failure on my side: the round 1
revision of step 5 never landed in the file, so Codex was reviewing the original step 5. Fixed now,
and the substance changed as well:

1. The tag file moves out of the DataRoot into `AppContext.BaseDirectory`, the service folder the
   uninstaller already validates.
2. One lookup only: read the file; if it is missing or invalid, warn and leave the instance rules in
   place. No derivation fallback in the uninstaller at all.
3. The tag is accepted only if it matches `^[0-9a-f]{8}$`, checked both when read and again inside the
   removal function; empty, malformed and wildcard files are tested.
4. The selector is `Get-NetFirewallRule -DisplayName "ArkAscendedServerAdmin-$tag-*"` piped to
   `Remove-NetFirewallRule`, stated explicitly.
5. Order: stop service, remove web and instance rules, delete service, delete InstallDir.
6. The tests now run the real read-then-remove path through a child PowerShell, including a second
   installation's rules that must survive, and the missing/malformed cases.

## Round 3 — Codex

The selector, tag validation, and service-deletion ordering are now addressed. Two gaps remain:

1. **Upgrades discard the ownership metadata before the replacement app starts.** [install.ps1](../../install/install.ps1:416) swaps the entire application directory and copies settings, but the plan adds no preservation of `firewall.tag`. After an upgrade with `-NoStart`, or a startup failure before writing the tag, uninstall skips existing rules and deletes the previous directory containing their ownership record. This is a supported lifecycle path, not merely a corrupt-file fallback.  
   **Fix:** Preserve the validated tag across upgrade directory swaps alongside settings, and test upgrade-without-start followed by uninstall cleanup.

2. **The claimed uninstall-retry verification is absent.** The tests section says the manual check covers retry, but that check only exercises instance isolation and verifies the tag file appears. It never causes cleanup to fail or reruns uninstall.  
   **Fix:** Add an explicit failure-and-retry procedure, or mock the uninstall operations to verify a cleanup failure prevents service deletion and a subsequent attempt succeeds.

VERDICT: REVISE

### Claude's response

Both accepted.

1. The installer's upgrade path copies a valid `firewall.tag` from the previous application folder into
   the new one, beside the settings it already carries over, so an upgrade with `-NoStart` or a failed
   first start keeps the ownership record. Verified by a manual upgrade-without-start followed by an
   uninstall on a scratch install.
2. `uninstall.ps1` gets a testing switch, `-SimulateFirewallFailure`, that throws inside the firewall
   step. The manual procedure runs the uninstall with it (service stopped, still registered, folder still
   present), then without it (completes). Recorded as a required step in `DECISIONS.md`.

## Round 4 — Codex

The material findings are addressed. Tag preservation covers upgrades without startup, and the revised uninstall order keeps firewall failures retryable.

One minor verification gap remains: starting the manager creates `firewall.tag` but does not necessarily create instance rules, so the manual cleanup check could pass vacuously. **Fix:** Create and start a scratch game instance, then confirm both UDP rules exist before upgrading and testing uninstall.

VERDICT: APPROVED

### Claude's response

Approved in round 4. The manual procedure now starts a scratch instance and confirms both UDP rules exist before the upgrade and uninstall steps.
