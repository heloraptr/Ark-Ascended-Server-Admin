<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: firewall rule names that do not collide between installs (solution-review finding 9)
_Round 3 revision by Claude, after Codex round 3_

Branch: `fix/pre-release-v1`. Source review: `docs/.untracked/code-review-ArkServerAdmin-202610020855.md`.

## Goal

`FirewallRules` (Infrastructure) names an instance's two inbound UDP rules `ArkAscendedServerAdmin-<instanceId>`.
`EnsureInstanceRules` deletes same-named rules whose port does not match, `RemoveInstanceRules` deletes
every same-named rule, and `InstanceRulesExist` looks the name up. Instance ids start at 1 in every
installation, so two installs on one machine (the owner's dev DataRoot beside the live service, or a
second install made with the installer's `-ServiceName`) overwrite and delete each other's rules.
The class comment also promises a description that names the port, and none is set. The uninstaller
removes only the web rule, so instance rules are left behind on uninstall.

After this change each installation's rules carry a tag derived from its DataRoot, two installs never
touch each other's rules, each rule has a description, and the uninstaller removes the rules of the
installation it is removing. There is no public installed base yet, so the old names only exist on
the owner's own machine.

## Approach

1. **An installation tag from the DataRoot.** New `FirewallRuleNames` (Core, `Firewall/`), pure and
   unit-testable:
   - `InstallTag(string dataRoot)`: `Path.GetFullPath`, trim trailing directory separators,
     `ToUpperInvariant` (NTFS paths are case-insensitive), UTF-8, SHA-256, first 8 hex characters,
     lowercase.
   - `RuleName(string tag, int instanceId)` = `ArkAscendedServerAdmin-<tag>-<instanceId>`.
   - `Description(int instanceId, int port, string dataRoot)` =
     `ArkAscendedServerAdmin: UDP <port> for instance <instanceId> (<dataRoot>)`.
   - `NamePrefix` stays `ArkAscendedServerAdmin-` for the documented
     `Get-NetFirewallRule -DisplayName 'ArkAscendedServerAdmin*'` search.

2. **`IFirewallRules` gains `string RuleName(int instanceId)`** so callers stop depending on the
   static. `InstanceCommands.GetConnectionAsync` (`InstanceCommands.cs:154`) uses it for the
   Connection card. The interface doc comment is updated to the new name shape.

3. **`FirewallRules` takes `DataRootLayout`**, computes the tag once in its constructor, and uses the
   tagged name in `EnsureInstanceRules`, `RemoveInstanceRules` and `InstanceRulesExist`. When creating
   a rule it sets the description if `WindowsFirewallHelper`'s rule type exposes one (check
   `IFirewallRule` and the concrete rule returned by `CreatePortRule`; if neither has a settable
   description, drop the promise from the class comment instead of inventing a workaround). Nothing
   else in the reconcile logic changes.

4. **The app records the tag it uses, next to its own binaries.** At startup, once `DataRootLayout`
   exists, the app writes the tag to `firewall.tag` in `AppContext.BaseDirectory` (the installed
   service's folder, which the uninstaller already validates and owns; for a dev console run that is a
   `bin` folder nobody reads), using the existing atomic file writer, on every start, so the file always
   names the tag of the rules that exist. A write failure is logged as a warning and does not stop
   startup. The file holds exactly the 8 lowercase hex characters and a newline.

4a. **Old-style names are left alone by the app.** The app never reads, matches or deletes
   `ArkAscendedServerAdmin-<id>` rules. Removing them from the app would be the cross-install deletion
   this change exists to stop. On the owner's machine the old rules are removed once by hand (a
   PowerShell line recorded in `DECISIONS.md`); no one else has any.

5. **Uninstaller removes this installation's instance rules by the recorded tag, or not at all.**
   - `Read-InstanceFirewallTag([string]$installDir)` in `ArkInstall.Common.ps1` reads
     `$installDir\firewall.tag`, trims it, and returns it only if it matches `^[0-9a-f]{8}$`. A missing,
     empty, malformed or wildcard-containing file returns `$null` with a warning that names the file and
     says the instance firewall rules were left in place. There is no derivation fallback: the
     uninstaller never computes a tag from configuration, because it cannot resolve `DataRoot` the way the
     service account did and guessing could select another installation's rules.
   - `Remove-InstanceFirewallRules([string]$tag)` requires a validated tag (it re-checks the regex and
     throws otherwise), selects with `Get-NetFirewallRule -DisplayName "ArkAscendedServerAdmin-$tag-*"`
     (the name the app sets through the COM API is the DisplayName in NetSecurity terms; `-Name` is the
     rule id and would match nothing) and pipes the returned objects to `Remove-NetFirewallRule`. It
     prints how many rules it removed.
   - `uninstall.ps1` order becomes: stop the service; remove the web rule and the instance rules; delete
     the service; delete `InstallDir`. A firewall failure then leaves a stopped but still registered
     service, and rerunning the script passes its service-existence guard and finishes the job.
   - `uninstall.ps1` gets a testing switch, `-SimulateFirewallFailure`, documented beside the existing
     testing parameter, that throws inside the firewall step so the stop-then-retry path can be exercised
     on a scratch install.

5a. **Upgrades keep the tag.** `install.ps1` swaps the whole application folder on upgrade and copies
   settings across (`install.ps1` near line 416). It also copies `firewall.tag` from the previous folder
   when the file exists and passes the same `^[0-9a-f]{8}$` check, so an upgrade with `-NoStart`, or a
   first start that fails before the app rewrites the file, still leaves the ownership record in place
   for a later uninstall. An invalid file is not copied, and a warning says so.

6. **Docs.** Update the four places that spell the name: `docs/instance-creation.md:131`,
   `docs/guide/first-run.md:82`, `docs/guide/instances.md:62` and `:198`. Say the middle part is a
   short tag that identifies the installation, so two installs on one machine keep separate rules,
   and that the Connection card shows the exact name. Keep `docs/exposing-servers.md:101` as is (the
   prefix search still works).

7. **Tests.**
   - Unit (new `FirewallRuleNamesTests`): same tag for the same root spelled with different case and
     with or without a trailing backslash; different roots give different tags; the tag is 8 lowercase
     hex characters; the rule name and description have the documented shape.
   - Integration (`FirewallRulesTests`, elevated-only, already skips otherwise): use the instance's
     `RuleName` instead of the static; add a case where two `FirewallRules` built on different roots
     ensure rules for the same instance id and port, and removing one leaves the other's rules in
     place; assert the description when it is supported.
   - PowerShell, executed (integration test, skips with a reason if `powershell.exe` cannot start):
     dot-source `install/ArkInstall.Common.ps1` in a child Windows PowerShell and check
     `Read-InstanceFirewallTag` against a temp folder with (a) a valid file, returning the tag; (b) no
     file; (c) an empty file; (d) `ArkAscendedServerAdmin-*`; (e) `*`; (f) a 7-character value; each of
     (b)–(f) returns nothing and writes the warning. Also check that `Remove-InstanceFirewallRules '*'`
     and `Remove-InstanceFirewallRules ''` throw before touching the firewall.
   - Elevated cleanup test (in `FirewallRulesTests`, same skip rules): `FirewallRules` on two different
     roots create rules for the same id and port; the first root's tag is written to a temp
     `firewall.tag`; a child PowerShell runs `Read-InstanceFirewallTag` on that folder and feeds the
     result to `Remove-InstanceFirewallRules`; the first root's rules are gone and the second root's
     rules remain, with their ports intact.
   - Startup: a test that the tag file is written to the base directory, holds exactly
     `FirewallRuleNames.InstallTag(root)` and passes the same `^[0-9a-f]{8}$` check the uninstaller uses.
   - Every `IFirewallRules` implementer is updated: `FakeFirewall` in
     `test/.../Processes/ProcessManagerFakes.cs` and any other the build finds. The whole solution is
     built, not just the changed projects.
   - Uninstall retry and upgrade (manual, on a scratch install, recorded in `DECISIONS.md`): install;
     create a scratch instance and start it, and confirm `firewall.tag` exists and both of its UDP
     rules are present under the tagged name (the check is vacuous otherwise); upgrade with `-NoStart` and confirm the new folder has the same
     tag; run `uninstall.ps1 -SimulateFirewallFailure` and confirm it stops with the service still
     registered and the folder still present; run `uninstall.ps1` again and confirm it completes and the
     instance rules for that tag are gone.
   - **Manual check by the owner on the real machine** (recorded in `DECISIONS.md`): start a dev
     instance whose id matches a live instance; confirm both sets of rules exist with different names
     and the live one still has its ports; delete the dev instance; confirm only the dev rules went.
     Before the next live deploy, confirm `firewall.tag` appears in `<DataRoot>\App` after the service starts.

## Key decisions & tradeoffs

- **Tag from the DataRoot rather than the service name.** The app does not know its service name
  (it can run as a console too), and the DataRoot is the one thing that is unique per installation
  and known to both the app and the installer scripts.
- **8 hex characters of SHA-256, not the path itself.** Firewall rule names are free text, but a path
  in the name is long, has characters the docs would have to escape, and leaks the install location
  into `Get-NetFirewallRule` output. 32 bits is plenty for "a few installs on one machine".
- **Case-insensitive normalization.** `C:\Ark` and `c:\ark` are the same install; a different tag
  for each would orphan rules after an edit to `appsettings.json`.
- **The app does not migrate old names.** Any rule the app touches that it did not create under the
  new scheme is a potential cross-install deletion. The old rules are harmless allows that the owner
  removes once; there is no installed base beyond that.
- **The tag file is the handover from app to uninstaller, and the only one.** Re-deriving the tag from
  configuration in two languages with two resolution rules is where mismatches come from. One file,
  written by the code that owns the rules into the folder the uninstaller already validates, removes
  that. When the file is missing or invalid the uninstaller leaves the rules and says so; leaving two
  allow rules behind is better than deleting another installation's.
- **The uninstaller removes by tag, not by prefix.** Removing by prefix would take another install's
  rules with it, which is the same bug in a different place.

## Risks / open questions

- `WindowsFirewallHelper` may not expose a description on the rule object it creates; then the
  comment is corrected and the description part is dropped. The tag in the name already does the
  disambiguation.
- The elevated integration tests only run in an elevated process; CI almost certainly skips them, so
  the manual check on the owner's machine is the real verification of the reconcile path.
- If the DataRoot in `appsettings.json` is ever changed for an existing install, its rules are
  orphaned under the old tag and recreated under the new one on next start. Same as moving an
  install today; noted in `docs/hosting.md` if a DataRoot-change section exists, otherwise not.

## Out of scope

- Restricting the rules to the game executable (review finding 30, separate decision).
- Any change to the web rule the installer manages (`<ServiceName>-Web`).
- RCON, which is never opened.
