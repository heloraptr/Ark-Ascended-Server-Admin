<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# Plan: Public 1.0.0 release of Ark Ascended Server Admin

> **Version note (2026-09-14, reference only, no decision reopened):** the first public tag is `v1.0.0`, not
> `v0.0.1`; every version string below was updated to match (rehearsals `v1.0.0-rc.N`, fixes `v1.0.x`,
> the post-1.0.0 work in `RELEASE-PLAN-FUTURE.md` targets `v1.1.0` and `v1.2.0`). **Every tag, rehearsal
> tags included, is created and pushed by the owner; Claude never tags.**
_Locked via grill — by Claude + heloraptr, 2026-09-13. Revised after Codex rounds 1 to 5 (the round-5 changes were not re-reviewed; MAX_ROUNDS reached). Planning only;
no code has been written._

## Goal

Make the repository public and publish a `v1.0.0` GitHub release that a stranger can install on a Windows
box in a few minutes: a zip with the published app and an `install.ps1` that asks the questions and
registers the Windows service. Kestrel as a Windows service is the only deployment scenario the tests and
the alpha exercise; IIS is documented as untested and potentially breaking, not as an option. Before the
first tag the login password stops being stored in plaintext, the cookie key ring stops being readable by
every local account, the third-party licenses ship with the app, and the repository carries a license, a
disclaimer, and policy files. The web UI is the product; the WPF client is dropped for good and containers
are out of scope.

## Approach

### A. Code changes before the first tag

1. **Password hash in configuration.** New key `ArkAdmin:PasswordHash` holding a PBKDF2-SHA256 string
   `pbkdf2$<iterations>$<salt-base64>$<hash-base64>` (600 000 iterations, 16-byte salt, 32-byte hash).
   - `PasswordSource` accepts `Password` (development, and anyone who prefers it) or `PasswordHash`. When
     both are set, `PasswordHash` wins and a warning is logged.
   - A present but malformed `PasswordHash` (bad prefix, wrong field count, salt or hash of the wrong
     length, iterations outside 10 000–5 000 000) refuses every login and logs an error. There is no
     fallback to `Password`.
   - `Verify(candidate)` returns the credential snapshot it verified against (the hash string) or null,
     and `LoginService` issues the cookie claim from that snapshot, never from a second read of
     `CurrentHash`. This closes the window where a reload between verify and claim would hand an
     old-password login the new password's claim.
   - The cookie claim is the stored hash string (today: unsalted SHA-256 of the plaintext); the
     "password changed → cookies and circuits refused" behavior is unchanged. The claim is about 100
     characters and sits inside the Data Protection-encrypted cookie.
   - `--hash-password` on the exe reads standard input as UTF-8 to end of stream, strips exactly one
     trailing line terminator (`\r\n` or `\n`) and nothing else (interior whitespace and leading or
     trailing spaces are part of the password), refuses an empty password or one containing another
     line break, prints only the hash string on standard output, exits 0, and runs before
     `WebApplication.CreateBuilder` (no configuration, no `DataRoot`). Any other outcome exits non-zero
     with nothing on stdout.
   - Unit tests: format round trip, verify success and failure, every malformed shape refuses, both keys
     set prefers the hash, and a round trip through the hashing entry point for a password with
     non-ASCII characters, leading and trailing spaces, and a `$` sign.
2. **Key ring protection.** `AddDataProtection().PersistKeysToFileSystem(...)` disables the default
   encryption at rest, so today `DataRoot\keys\key-*.xml` is plaintext and readable by any local account.
   Add `.ProtectKeysWithDpapi()` (current-user scope: only the service account, LocalSystem, can unwrap).
   Newly generated keys are protected. Existing plaintext key files keep decrypting cookies even after
   they expire (expiry only stops new protection), so the installer deletes every `keys\key-*.xml` that
   lacks an `encryptedSecret` element while the service is stopped on the first upgrade to this version;
   every session is signed out once and the app issues a protected key on start. The folder is ACL'd
   (step 12) on every run.
3. **`/healthz`.** Anonymous `GET /healthz` returning `text/plain` `ArkAscendedServerAdmin ok`, exempt
   from the HTTPS guard and the readiness redirect, no version, no state. Used by the installer probe
   and usable by monitoring. Nothing else changes in the pipeline.
4. **Version in the UI.** Show `AssemblyInformationalVersion` (MinVer: `1.0.0+<sha>`) on Settings under
   the host values and in the rail footer.
5. **Hosting label.** `HostConfiguration.IsWindowsService` becomes a hosting-model label on Settings
   (Windows service / console); no behavior change.
6. **Fonts and notices.** `OFL-Saira.txt` and `OFL-JetBrainsMono.txt` next to the `.woff2` files in
   `Components/wwwroot/fonts/` (the SIL Open Font License requires the license text to accompany the
   files). `THIRD-PARTY-NOTICES.md` at the repo root, copied into the publish output: Radzen.Blazor
   (MIT), CoreRCON (MIT), WindowsFirewallHelper (MIT, Soroush 2016–2019), MinVer (Apache-2.0), Saira
   (OFL 1.1), JetBrains Mono (OFL 1.1). All six verified 2026-09-13.
7. **Disclaimer.** README and Settings page: "Not affiliated with, sponsored by, or endorsed by Studio
   Wildcard or Snail Games. ARK: Survival Ascended and related marks are trademarks of their respective
   owners." The CurseForge key field gets one sentence: the key is the user's own and subject to
   CurseForge's API terms.
8. **Test data.** Replace the owner's gamertag and EOS id in `PlayerLogLinesTests`, `PlayerTrackerTests`,
   and the doc comments of `PlayerLogLines` and `ListPlayersParser` with a synthetic name and a synthetic
   32-hex id. The tests pin the format, not the person.
9. **Flaky test.** Fix or rewrite `IniSourceStoreTests.Save_IsAtomic_NoTempFileRemains` so it does not
   depend on timing.
10. **`Authors`** in `Directory.Build.props` becomes `heloraptr`; copyright `Copyright (c) 2026 heloraptr`.

### B. Packaging and installer

11. **`build/publish-release.ps1`** (tracked; runs under Windows PowerShell 5.1 and pwsh 7).
    `$ErrorActionPreference = 'Stop'` plus an explicit `$LASTEXITCODE` check after every native command
    (the `Invoke-Checked` pattern from today's deploy script). Steps:
    - Version: `dotnet msbuild src/ArkAscendedServerAdmin.Server/ArkAscendedServerAdmin.Server.csproj
      -restore -t:MinVer -getProperty:MinVerVersion` (`-getProperty` alone only evaluates the project;
      MinVer's target must run first). The result is the full SemVer (`1.0.0-rc.1+<sha>`). Zip names use
      the version without build metadata. In the release workflow the version without build metadata
      must equal the tag without its `v`, otherwise the job fails. Fallback if the property comes back
      empty: `[Diagnostics.FileVersionInfo]::GetVersionInfo(<published exe>).ProductVersion`, which
      carries the informational version, compared the same way.
    - `dotnet build ArkAscendedServerAdmin.slnx -c Release`; both test executables via
      `dotnet run --project <csproj> -c Release --no-build -- --report-trx --results-directory <dir>`.
    - Two publishes of the Server project (`-c Release`, `--no-build` is not used for publish because
      the runtime identifier differs from the build):
      `ArkAscendedServerAdmin-<version>-win-x64.zip` (`-r win-x64 --self-contained`, about 110 MB) and
      `ArkAscendedServerAdmin-<version>-win-x64-fdd.zip` (framework-dependent, about 20 MB, needs the
      ASP.NET Core 10 runtime on the box). No single-file, trimming, or ReadyToRun.
    - Each staging folder: `appsettings.Development.json` removed; `install.ps1`, `uninstall.ps1`,
      `THIRD-PARTY-NOTICES.md`, `LICENSE`, `INSTALL.md` copied in; `package.json` written with
      `{ version, selfContained }` (describes the package; it is not the installation marker); zipped
      with `Compress-Archive`. `SHA256SUMS` covers both zips.
12. **`install.ps1`** (source under `install/`, shipped in both zips). `#requires -RunAsAdministrator`;
    written for Windows PowerShell 5.1 and pwsh 7 (no `-SkipHttpErrorCheck`, no `-SkipCertificateCheck`;
    probes use `try/catch` on `WebException` and a temporary certificate-validation callback).
    Runs from the unzipped folder and copies from there; never builds. A named mutex
    (`Global\ArkAscendedServerAdmin.Install`) is taken by `install.ps1` for every operation (install,
    upgrade, `-SetPassword`, `-SetCertificate`) and by `uninstall.ps1`; a second concurrent run stops.
    - **Parameters**, each prompted when omitted (except in `-Quiet`, which fails on a missing required
      value): `-InstallDir` (default `C:\ArkAscendedServerAdmin\App`), `-DataRoot` (default
      `C:\ArkAscendedServerAdmin`, with the reminder that the 12 GB game install, the instances, and the
      backups land there), `-Password` (secure prompt), `-Bind` (`Loopback` default, `LanHttps`,
      `Proxy`), `-Port` (5000 for `Loopback` and `Proxy`, 5001 for `LanHttps`), `-KnownProxies`
      (required for `Proxy`), `-LanSource` (`LocalSubnet` default for `LanHttps`), `-NoStart`, `-Quiet`,
      `-SetPassword`, `-SetCertificate [-PfxPath -PfxPassword]`. `-NoStart` is a separate completion
      path: the service is left stopped, every recovery artifact (`.previous-<ts>`, `_app` copy,
      settings `.bak`) is kept, no probe runs, the script prints "verification pending: start the
      service and run install.ps1 -Verify", and exits 0. `-Verify` (also under the mutex) runs the probe
      against the installed configuration and, on success, performs the cleanup a normal run does.
    - **Pending transaction.** Every mutating operation (install, upgrade, `-SetPassword`,
      `-SetCertificate`) first writes a journal at `DataRoot\keys\install-pending.json` (a stable,
      SYSTEM/Administrators-only location that no rename touches; `InstallDir` is swapped during an
      upgrade so nothing there can hold it): `{ operation, startedAt, phase, installDir, previousDir,
      dbCopyDir, settingsBak, pfxOld, pfxNew }`. The `phase` field is rewritten atomically (temp file
      plus `File.Replace`) after each completed step (`copied`, `stopped`, `db-copied`, `swapped`,
      `settings-written`, `started`, `verified`), so `-Rollback` knows exactly which steps to undo
      after an interruption. The journal is deleted only after verification and cleanup succeed. While
      it exists, every other mutating operation is refused with the pending operation named and two
      ways out: `-Verify` or `-Rollback` (stop the service, undo the recorded steps in reverse, start,
      probe against the restored configuration, then clear the journal). `-Verify` is
      operation-specific: for an upgrade or `-SetCertificate` it runs the probe (with the thumbprint
      pin) and cleans up; for a pending `-SetPassword` it prompts for the new password again and
      repeats the login round trip before committing, and refuses to commit on liveness alone; if the
      user no longer knows the new password, `-Rollback` restores the old one. Cleanup only ever deletes artifacts named in the current
      record plus older `.previous-*` and `_app` sets that are not named in it, so the pair a
      transaction started from is intact until that transaction verifies.
    - **Path validation** before anything is touched: both paths canonicalized (`[IO.Path]::GetFullPath`,
      trailing separators removed), rooted on a fixed local drive. Rules: `InstallDir` may not equal
      `DataRoot`; `DataRoot` may not be inside `InstallDir`; `InstallDir` may be a direct child of
      `DataRoot` (the default `C:\ArkAscendedServerAdmin\App` and the VM's `<DataRoot>\App`) but not any
      of the managed names (`Server`, `SteamCMD`, `Instances`, `Clusters`, `Backups`, `Archive`, `keys`,
      `Exports`, `Data`) or deeper; neither may contain the zip's own folder or be contained by it;
      neither path nor any ancestor of either may be a reparse point (checked segment by segment); the
      same rules apply to `<InstallDir>.new` and every `<InstallDir>.previous-*` before they are created
      or deleted. Neither path may be a volume root, `%SystemRoot%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`,
      `%ProgramData%`, `%Public%`, a user profile root, or any ancestor of those. `DataRoot` must either
      not exist, be empty, or be the `ArkAdmin:DataRoot` named by the existing service's settings file;
      any other existing folder is refused, because the installer replaces its ACL.
    - **Installation marker.** The installer writes `InstallDir\install.json` at install time:
      `{ installId (GUID), serviceName, dataRoot, version, installedAt }`. An existing non-empty
      `InstallDir` is only touched when it carries a marker whose `serviceName` matches, or under the
      legacy adoption rule below. `.new` and `.previous-*` folders are only deleted when they carry a
      marker with the same `installId`.
    - **Existing service**: the target folder is derived from the service's quoted `BinaryPathName`
      (`Get-CimInstance Win32_Service`), not from the parameter default. A `-InstallDir` that differs from
      it is refused with both paths printed. `DataRoot`, the bind scheme, and the port on an upgrade are
      read from the existing `appsettings.Production.json`, never re-prompted. **Legacy adoption** (one
      time, for installs made by the old dev script): a folder without a marker is accepted only when
      the service exists and its binary path points into that folder; the installer then writes the
      marker before proceeding.
    - **Runtime check first** (framework-dependent zip): before any packaged executable is invoked,
      `dotnet --list-runtimes` must show an x64 `Microsoft.AspNetCore.App 10.*`; otherwise stop with the
      download link.
    - **Hashing**: the secure string is converted in memory and written to the exe's standard input
      through `System.Diagnostics.Process` (`UseShellExecute = false`, `RedirectStandardInput`,
      `RedirectStandardOutput`) as explicit UTF-8 bytes on `StandardInput.BaseStream` followed by one
      `\n`, then the stream is closed so the child's read-to-end completes. `StandardInputEncoding` is
      not used because .NET Framework's `ProcessStartInfo` (Windows PowerShell 5.1) does not have it; the
      PowerShell pipeline is not used because its native-command encoding differs between shells. Stdout
      must be exactly one `pbkdf2$` line and the exit code 0, otherwise stop. The plaintext is never an
      argument and never written.
    - **ACLs, applied on every run** (install and upgrade, before anything sensitive is written,
      idempotent): a folder created under `C:\` inherits "Authenticated Users: Modify" on its contents,
      which would let any local account replace an executable that LocalSystem runs. So: `InstallDir`,
      `<InstallDir>.new`, and `DataRoot` get inheritance removed and `SYSTEM:(OI)(CI)F`,
      `Administrators:(OI)(CI)F`, `Users:(OI)(CI)RX` (the owner can still browse worlds and logs
      unelevated; the game executable under `DataRoot\Server` is run by LocalSystem, so it needs the same
      protection as the app). `DataRoot\keys`, `DataRoot\Data`, `DataRoot\Exports` (config exports are database copies),
      and `DataRoot\Backups\_app` get `SYSTEM:(OI)(CI)F` and `Administrators:(OI)(CI)F` only (the
      database holds the plaintext CurseForge key). After applying, the installer reads the ACLs back and stops if they differ.
    - **Settings file writes** (first run, `-SetPassword`, `-SetCertificate`): create
      `appsettings.Production.json.tmp` in the same folder as an empty file with an explicit security
      descriptor (`[IO.File]::Create` with a `FileSecurity` of `SYSTEM:R`, `Administrators:F`,
      inheritance off, or create-then-`icacls` before any content is written), then write the content,
      then `[IO.File]::Replace(tmp, target, target + ".bak")` when the target exists (same volume, atomic
      on NTFS) or `[IO.File]::Move` when it does not; the `.bak` is deleted after the following probe
      succeeds and restored if it fails. Content is never present in a file other accounts can read. On
      upgrade the settings file is copied from the previous folder the same way (create with the ACL,
      then copy content), never with a plain `Copy-Item`.
    - **First run**: create the folders; apply the ACLs; copy the staging folder to `InstallDir`; write
      the marker; write `appsettings.Production.json` (`ArkAdmin:DataRoot`, `ArkAdmin:PasswordHash`,
      `AllowInsecureHttp=false`, `KnownProxies`, the Kestrel endpoints, the event-log logging block from
      today's script) as above; register the service
      (LocalSystem, automatic, description); `sc.exe failure <name> reset= 86400 actions=
      restart/5000/restart/5000//0` (two restarts, then stop: the SCM repeats the last action for every
      later failure, so a third `restart` would loop forever; the reset period counts failure-free
      time); start; probe.
    - **Bind modes**:
      - `Loopback`: `Kestrel:Endpoints:Http:Url = http://127.0.0.1:<port>`. The installer prints, in
        place of a URL: "Point your HTTPS reverse proxy at http://127.0.0.1:<port>, forward
        `X-Forwarded-Proto` and enable WebSockets, then open the proxy's URL. A direct http:// request
        answers 403 by design." Documented in `docs/hosting.md` with Nginx Proxy Manager and Caddy
        examples.
      - `LanHttps`: `Kestrel:Endpoints:Https:Url = https://0.0.0.0:<port>` with
        `Certificate:Path = <DataRoot>\keys\web.pfx` and a random `Certificate:Password`. Certificate
        via `New-SelfSignedCertificate` with a `2.5.29.17` text extension carrying `DNS=` entries for
        the machine name and `IPAddress=` entries for every non-loopback IPv4 address (IP addresses are
        not DNS names), 1-year validity (browsers tolerate a click-through either way), exported to the
        PFX and removed from the store.
      - `-SetCertificate`: without arguments re-issues the self-signed certificate with the same SAN
        logic; with `-PfxPath`/`-PfxPassword` imports the user's own. PFX files are never renamed after
        the configuration points at them: the first install writes `keys\web-<ts>.pfx`, and each
        replacement writes a new `keys\web-<ts>.pfx` in full, then rewrites the settings file as above
        with the new path and password, restarts the service (Kestrel's reload of a changed certificate
        file is not relied on), and probes with the new thumbprint. On success the previous PFX is
        deleted; on failure the settings `.bak` is restored, the service restarted, probed again with the
        old thumbprint, and the new PFX deleted. The doc also describes doing it by hand. The base `appsettings.json`
        still declares `Http` on `127.0.0.1:5000`; configuration merging cannot delete it, so the
        production file sets it explicitly to `http://127.0.0.1:<port-1>` and the doc says both
        listeners exist (the loopback one is guard-protected and proxy-able). Firewall rule for TCP
        `<port>` from `-LanSource` (`LocalSubnet` by default), all profiles.
      - `Proxy`: `Http:Url = http://0.0.0.0:<port>`, `KnownProxies` required, firewall rule for TCP
        `<port>` limited to those addresses.
    - **Upgrade run** (service exists, paths validated, marker or legacy adoption satisfied), with
      `<ts>` = `yyyyMMdd-HHmmss`: apply the ACLs; copy the new files to `<InstallDir>.new` and write the
      marker there with the existing `installId`; stop the service and wait; copy every file in
      `DataRoot\Data\` (`.db`, and `-wal`/`-shm` if present after the stop) to
      `DataRoot\Backups\_app\<ts>-<old-version>\` (unique per attempt; a raw copy is consistent only
      while stopped); delete the unprotected key files (A2); rename `InstallDir` →
      `<InstallDir>.previous-<ts>`; rename `.new` → `InstallDir`; copy `appsettings.Production.json`
      from the previous folder; start; probe using the scheme, port, and (for HTTPS) the thumbprint read
      from the PFX named in the installed settings, and confirm the listening socket's owning process is
      the service's process id (`Get-NetTCPConnection` / `Get-CimInstance Win32_Service.ProcessId`).
      On success, delete older `.previous-*` folders and `_app` copies, keeping the newest of each. If
      any step before "start" fails, the renames are reversed and the service is restarted on the old
      files. If the probe fails after start, the installer stops the service, reports the last 50
      Application event-log lines from the app's source, and prints the recovery: rename
      `.previous-<ts>` back and restore the whole `_app\<ts>-...` file set (all of `.db`, `-wal`, `-shm`
      together, deleting any newer `-wal`/`-shm` first), because a migration may already have run.
    - **`-SetPassword`**: refuse (not warn) when the service's `Environment` registry value or the
      machine environment carries `ArkAdmin__Password` or `ArkAdmin__PasswordHash`, because those
      override the file and the change would not take effect. Then hash as above, read the settings
      file, remove `ArkAdmin:Password` if present, set `PasswordHash`, write as above (temp with final
      ACL, `File.Replace`), restart the service (a restart reads the file fresh; the configuration
      reload is not relied on for verification), probe `/healthz`, and confirm adoption by a login
      round trip from a loopback client: `GET /login` then `POST /login` with the new password, both
      carrying `X-Forwarded-Proto: https` (loopback is always a trusted proxy in `Program.cs`, so the
      HTTPS guard sees HTTPS and stays as it is), the antiforgery cookie and form token from the `GET`
      echoed on the `POST`, automatic redirects disabled, success being a 302 to `/` with a `Set-Cookie`
      for `ArkAscendedServerAdmin.Auth`; a wrong password is a 200 with the form re-rendered, which the
      installer treats as "not adopted". In `LanHttps` mode the same client talks to the HTTPS port with
      the thumbprint pin. The `.bak` is deleted only after that succeeds and restored (with another
      restart) if it fails. Game servers are unaffected by the restart; the doc says so.
      `-SetPassword -NoStart` is refused: the adoption check cannot be deferred.
    - **Probe** (every run): scheme and port read from the installed settings file; for HTTPS a
      validation callback that accepts only the thumbprint of the PFX the settings name; success is
      HTTP 200 with body `ArkAscendedServerAdmin ok` from a socket owned by the service's process;
      anything else within 60 s is a failure and the script exits non-zero after printing the event-log
      tail.
    - Prints at the end: service state, the URL (or the proxy instruction), `InstallDir`, `DataRoot`,
      the settings file path, and "every page shows /setup until the 12 GB game install finishes".
13. **`uninstall.ps1`**: takes the same mutex; refuses unless `InstallDir\install.json` exists and the
    service's binary path points into `InstallDir`; stops and deletes the service, removes the web
    firewall rule, deletes `InstallDir` and every `InstallDir.previous-*` whose marker carries the same
    `installId`, leaves `DataRoot` in place and says so (worlds, backups, keys, database, certificate are
    the user's).
14. **The dev `deploy.ps1`** (untracked) becomes: run `publish-release.ps1`, then
    `install.ps1 -InstallDir <DataRoot>\App -DataRoot <DataRoot>` from the staging folder. The first run goes
    through legacy adoption (no marker yet). The VM's settings file keeps a plaintext `Password` until
    migrated with `-SetPassword`; both keys are accepted.
15. **Clean-machine smoke test** before the tag, on unactivated Windows 11 VMs in Proxmox (not the
    development VM, which has the SDK and pwsh 7). The step-by-step protocol, with the expectation at
    each step, is Appendix B of this document; it is the acceptance test for steps 11–13 and is run in
    full once per release candidate. Results go into a copy of the Appendix B table in
    `docs/.untracked/SMOKE-<version>.md`.

### C. Continuous integration and releases

16. **`.github/workflows/ci.yml`** on push to `main` and pull requests: `windows-latest`,
    `actions/checkout` with `fetch-depth: 0`, `actions/setup-dotnet` from `global.json`,
    `dotnet build ArkAscendedServerAdmin.slnx -c Release`, both test executables via
    `dotnet run --project <csproj> -c Release --no-build -- --report-trx --results-directory
    TestResults`, `actions/upload-artifact` with `if: always()`. The runner is elevated, so the firewall
    integration test runs. Every `dotnet` step passes `-c Release` explicitly.
17. **`.github/workflows/release.yml`** on `push: tags: ['v*']`: job `permissions: contents: write`;
    same build and test steps; `build/publish-release.ps1`; assert the MinVer version equals the tag;
    `gh release create "$TAG" --generate-notes --prerelease <zips> SHA256SUMS` with
    `GH_TOKEN: ${{ github.token }}`. `--prerelease` stays while the major version is 0. **The owner creates and
    pushes every tag himself; Claude never tags**, not even for a rehearsal. Release procedure for the
    owner: `git tag v1.0.0 && git push origin v1.0.0`. Rehearsal: the owner tags `v1.0.0-rc.1` while
    the repo is still private, the workflow is checked, then the owner deletes the release and the tag.
18. Later, not 1.0.0: an "update available" notice from the GitHub releases API once a day; Dependabot
    for NuGet monthly if the noise is acceptable; `MinVerMinimumMajorMinor` once 0.1 exists.

### D. Repository policy and documentation

19. **`LICENSE`**: MIT, `Copyright (c) 2026 heloraptr`.
20. **`CONTRIBUTING.md`**: issues welcome; pull requests are reviewed and kept or closed when the owner
    has the bandwidth; open an issue first for anything larger than a fix. Issues on, Discussions off,
    Wiki off. `SECURITY.md` points at GitHub's private vulnerability reporting. Issue templates: bug
    (app version from Settings, Windows version, bind mode, event-log excerpt, `/setup` console text)
    and feature request.
21. **README statement** near the top: "This is a personal project I run for my own servers. Issues are
    read; fixes and features land when I have the time and bandwidth. No schedule, no guarantees."
    The words "support" and "supported" are avoided throughout; "tested", "documented", "untested".
22. **README rewrite**: install from a release (download, unblock the zip, run `install.ps1` elevated,
    answer the prompts, open the URL, wait for `/setup` to finish the 12 GB install), then build from
    source, then links to the docs pages. Keep the feature, configuration, layout, and ASA-notes sections.
23. **`docs/hosting.md`**: the three bind modes with their reverse-proxy requirements
    (`X-Forwarded-Proto`, WebSockets), Nginx Proxy Manager and Caddy examples, replacing the self-signed
    certificate, the second loopback listener in `LanHttps`, the event log, upgrading and the `.previous`
    folder, recovery after a failed upgrade, uninstalling. An "IIS" section: untested; the published
    folder carries a `web.config` so it may start under IIS, but the app pool identity cannot create
    firewall rules, idle timeout and recycling stop the backup scheduler, log tails, and player
    tracking, an overlapping recycle runs two copies of the process manager, and whether a game launched
    from the worker process survives a recycle is unknown. Anyone who tries it should expect breakage.
24. **`docs/exposing-servers.md`**: what must be reachable (one inbound UDP port per instance, the game
    port; advertising is outbound HTTPS to Epic; 7778 and 27015 are Survival Evolved leftovers; RCON on
    TCP must never be reachable from outside; the app already writes the Windows firewall rule). Three
    shapes: (a) home box behind a router: forward the UDP game port, needs a real public IP, CGNAT means
    it will not work, UPnP by hand if the router offers it; (b) cloud Windows VM: allow the UDP port in
    the provider's security group, the server lists with the VM's public IP, the home network is never
    involved, cost and disk are the tradeoff; (c) private: Tailscale, ZeroTier, or a LAN, players join
    with the in-game `open <ip>:<port>` console command, nothing is exposed to the internet, the server
    never appears in the list. Caveats: tunnels such as playit.gg or a VPS relay make the server appear in
    the list but joins fail because Epic advertises the home WAN address (`-PublicIPForEpic` is documented
    for Survival Evolved only and untested on ASA); ngrok and Cloudflare Tunnel do not carry public UDP;
    there is no Epic relay for dedicated servers; a UDP game port is a flood target whichever way it is
    exposed, and forwarding from home exposes the home IP to every joiner.
25. **`docs/configuration.md`** if the README table outgrows the README; otherwise the table stays.
26. **User guide** (added 2026-09-14, owner request). The README plus one supplemental page is not
    enough for a stranger; every implemented feature gets a page under `docs/guide/` that explains
    **what** it is, **how** to use it (the exact screens, buttons, fields, and what appears on disk
    or in the log), and **why** it works the way it does (the design decision, in one or two
    paragraphs, drawn from `DESIGN.md` and the code, never invented). Each page follows the same
    shape: a one-paragraph summary, "What it does", "How to use it", "What happens underneath"
    (files, database, RCON, SteamCMD), "Why it works this way", "When it refuses or fails" (the
    exact messages and what to do). Screens and labels are quoted as the UI shows them; every
    claim is checked against the Razor pages and the Core/Infrastructure code, and anything the
    code does not do is not written. The pages, all written for 1.0.0:
    - `docs/guide/README.md`: start here. The mental model (one game install under
      `DataRoot\Server`, junction trees per instance, clusters, `DataRoot` layout table, the rail,
      the state lamp and instance states, the readiness pipeline and `/setup`), and a table linking
      every page below plus `hosting.md`, `configuration.md`, `exposing-servers.md`, and
      `instance-creation.md`.
    - `first-run.md`: after install: `/setup` and the 12 GB game install console, what is on disk
      afterwards, the first Settings to fill in (CurseForge key, manager-wide admin whitelist),
      creating the first cluster and instance, joining with `open <ip>:<port>`.
    - `instances.md`: the Instances page (rows grouped by cluster, standalone last, per-row,
      selected, and per-cluster actions, the update-recovery banner), start (stagger queue, port
      check against live listeners), stop (countdown broadcast, `saveworld`, RCON exit, verified
      exit), restart, re-attach after a service restart, the instance page and its tabs, deleting
      an instance and what is kept. Links to `instance-creation.md` for the wizard.
    - `clusters.md`: what a cluster shares (INI files, mods, base launch options, admin whitelist,
      transfer directory, cluster id), standalone versus clustered, creating and editing one, moving
      an instance between clusters if the UI allows it, what the game needs for transfers.
    - `configuration-files.md`: `Game.ini` and `GameUserSettings.ini` as text on disk mirrored into
      the database, cluster source files and per-instance overrides, effective values, what the
      manager owns (session name, ports, RCON, player cap, admin password) and the contradiction
      warnings, the INI editor, external edits and reload, the starting points the wizard offers.
    - `launch-options.md`: the flags editor, cluster base plus instance additions, the command-line
      preview, the always-on game log flag, map and mod arguments the manager adds itself.
    - `console-and-rcon.md`: the `ShooterGame.log` tail, the startup markers, the RCON input, the
      handful of commands worth knowing, why RCON is loopback-only.
    - `backups.md`: how one backup runs (`saveworld`, wait for the files to settle, snapshot, zip,
      verify every entry, prune to retention), manual and scheduled backups, the Backups tab,
      skipped and failed attempts, where zips live, restoring a world by hand, the installer's
      `Backups\_app` database copies and how they differ.
    - `game-updates.md`: the Update page, "verify game files", stop-everything with verified exits,
      SteamCMD, manifest check, relaunch through the queue, the persisted steps and resume after a
      service restart, the update-recovery banner with retry and skip.
    - `mods.md`: the CurseForge key and its terms, search versus manual ids, cluster mods versus
      instance mods, usage view, a custom map's own mod and why it is only added through the map,
      how mods reach the launch line.
    - `maps.md`: official maps with type and release date, custom maps and their mod id, choosing a
      map in the wizard, what a map change means for a world.
    - `players-and-whitelists.md`: players recorded from the log as they join and leave, `ListPlayers`
      on demand, online and last-seen, platform and EOS id, the three whitelists (manager-wide,
      cluster, instance), how they combine into the file the game reads, what an admin whitelist
      grants in ASA, picking an id from the Players page.
    - `settings-and-export.md`: App Settings, the read-only host values, the version, the config
      export (a database copy, holds the CurseForge key in plaintext), the disclaimer.
    - `troubleshooting.md`: the symptoms a user meets first and what to do: every page shows
      `/setup`; a direct http:// request answers 403; login refused (no password, malformed hash,
      lockout); an instance will not start (port in use, missing map or mod); an instance will not
      stop; the service will not start (event log); a backup was skipped; an update stalled; where
      every log lives.
    The README "Documentation" section links `docs/guide/README.md` first. The guide is written on
    its own branch from `docs/public-release` in two halves that share no files and is reviewed page
    by page. Every page is re-read against the code once more before `v1.0.0`.
27. **History gate before flipping visibility.** The tracked history was scanned on 2026-09-13: no
    hostnames, proxy addresses, or keys. The owner's gamertag and EOS id exist in commit `5b73e19` and
    stay in history after step 8. **Decided 2026-09-13 (owner): rewrite history** with
    `git filter-repo --replace-text` (14 commits, solo, no forks, no published artifacts; every SHA
    changes, so the untracked docs that cite SHAs are updated afterwards and `origin` is force-pushed
    while the repo is still private). The scan is repeated on the rewritten history immediately before
    the repo goes public.

### E. Order of work

A (1–10) → B (11–14) on the VM with a real install into `<DataRoot>` → B15 on a clean machine →
C (16–17) with the `rc.1` rehearsal while private → D (19–27) → history decision → make the repo
public → `v1.0.0`.

## Key decisions & tradeoffs

- **Two zips, self-contained and framework-dependent.** The self-contained one removes the "which
  runtime" question; the framework-dependent one is for people who keep .NET installed and want a small
  footprint. Neither is "for IIS".
- **Password hash in configuration, CurseForge key plaintext in the database.** The hash has no restore
  dependency. Encrypting the CurseForge key with the Data Protection ring would make a database restored
  on another machine unable to read it; it stays plaintext for 1.0.0 and the README says so. The
  per-instance `ServerAdminPassword` is read by the game from the INI and is plaintext by necessity.
- **PBKDF2 rather than Argon2.** In the box (`Rfc2898DeriveBytes.Pbkdf2`), no new package. The threat
  model is "someone read a file only SYSTEM and Administrators can read".
- **DPAPI-protected key ring rather than a certificate.** One line, no key management, matches the
  single-machine, single-service-account deployment. Cross-machine restore of the key ring is not a goal;
  a new machine issues new cookies.
- **Rename-swap upgrade rather than `robocopy /MIR` in place.** Rollback is a rename; the database copy
  covers the migration case. `/MIR` stays out of the installer entirely.
- **ACLs on `DataRoot` and `InstallDir`, not only on the secret folders.** LocalSystem executes files
  from both (the app, SteamCMD, the game). `Users:RX` keeps the folders browsable; write access is
  SYSTEM and elevated administrators only.
- **Legacy plaintext keys are deleted, not kept.** One forced sign-in on the first upgrade is cheaper
  than reasoning about which old key can still decrypt what.
- **Self-signed HTTPS in the installer rather than plain HTTP on the LAN.** Keeps the HTTPS guard and
  the secure cookie as they are; the browser warning is the user's to accept or replace.
  `AllowInsecureHttp` stays a development switch and the installer never sets it.
- **`Loopback` stays the default bind** even though it needs a proxy before the UI is usable, because
  it is the only mode that never opens a port on the box; the installer says so in words instead of
  printing a URL that answers 403.
- **`/healthz` is unauthenticated and content-free** so the probe and monitoring work in every mode
  without leaking the version.
- **Windows PowerShell 5.1 compatibility** for the shipped scripts rather than requiring pwsh 7: a home
  user should not have to install a second shell to install the app.
- **Kestrel service only; IIS documented as untested and potentially breaking.**
- **Single password; no OAuth or multi-user in 1.0.0.**
- **Exposure is documentation only in 1.0.0.**
- **Pull requests stay open**; handled when there is bandwidth.
- **`gh release create` rather than a marketplace action.**
- **Install paths default to one folder** (`C:\ArkAscendedServerAdmin` with `App\` inside).

## Risks / open questions

- `-t:MinVer -getProperty:MinVerVersion`: confirm on the VM that the target name is `MinVer` in MinVer
  8.0.0 and that `-getProperty` reports the post-target value; the `ProductVersion` fallback is defined
  either way.
- `Users:(OI)(CI)RX` on `DataRoot` must not break anything the game writes: it runs as LocalSystem, so
  it is unaffected; verify SteamCMD (also launched by the service) likewise.
- `dotnet test` reports zero tests on SDK 10.0.400 with xunit.v3 4.0.0; CI uses `dotnet run`. Switch
  when a future SDK fixes it.
- `--hash-password` must be parsed before `WebApplication.CreateBuilder` reads configuration, so a
  missing `appsettings` or an unwritable `DataRoot` cannot break hashing.
- The certificate-validation callback in Windows PowerShell 5.1 is process-global
  (`ServicePointManager`); it is set for the probe only and reset in `finally`.
- Whether `Compress-Archive` in 5.1 handles the 110 MB self-contained output and long paths; if not,
  use `[IO.Compression.ZipFile]` directly.
- Owner's VM keeps a plaintext `Password` until migrated; both keys are accepted, so nothing breaks on
  upgrade.
- History rewrite (D27) is the owner's call.
- The `-SetPassword` login round trip depends on the login form's field names, the antiforgery
  cookie, and the redirect target; the installer test in step 15 covers it, and if the form changes
  the installer changes with it.
- `-Rollback` after a migration restores the database file set from the `_app` copy; the doc states
  that anything changed in the UI between the upgrade and the rollback is lost.

## Out of scope

- IIS hosting (documented as untested), containers, the WPF client, OAuth, multi-user, UPnP automation,
  an in-app reachability panel, an app update notifier, Dependabot, encrypting the CurseForge key,
  ban and exclusive-join lists, scheduled restarts, crash restart, Discord notifications, light mode.
  All of these are considered in the post-1.0.0 pass (`RELEASE-PLAN-FUTURE.md`, grilled separately).

---

## Appendix B: clean-machine smoke test protocol

Run on Proxmox. Activation is irrelevant to anything below; an unactivated Windows 11 works. Each
scenario starts from the same snapshot so nothing leaks between them.

### B.1 One-time VM preparation

1. Create a Windows 11 VM: 4 vCPU, 8 GB RAM, 60 GB disk (the game install is not downloaded in this
   protocol; 60 GB leaves room if it is). Windows 11 Pro ISO, skip activation, local account
   (`tester`, administrator), no Microsoft account.
2. Install nothing else. In particular: no .NET SDK, no .NET runtime, no PowerShell 7, no Git. The point
   is a machine like a stranger's.
3. Confirm the baseline in an elevated Windows PowerShell:
   `$PSVersionTable.PSVersion` shows 5.1; `dotnet --list-runtimes` errors with "not recognized";
   `Get-Service ArkAscendedServerAdmin` errors with "cannot find".
4. Note the VM's LAN IP (`ipconfig`) and confirm the Proxmox host's browser can reach it (ping).
5. Snapshot the VM as `clean`. Every scenario below begins with "revert to `clean`".

### B.2 Getting the zips onto the VM

Either download the `v1.0.0-rc.N` release assets from GitHub inside the VM (the repo is still private,
so use a browser logged into the account, or a fine-grained token with `Invoke-WebRequest` and the
`Authorization: Bearer` header against the asset's `browser_download_url`), or serve the two zips from
the development VM with `python -m http.server 8000` in the release folder and fetch
`http://<dev-vm-ip>:8000/<zip>` from the test VM. Save under `<UserProfile>\Downloads`.

After download: right-click → Properties → Unblock (or `Unblock-File`), then extract with
right-click → Extract All. Not unblocking is itself a test: the scripts must fail with the standard
execution-policy message, not something confusing.

### B.3 Scenario table

Each scenario: revert to `clean`, transfer the zip named, open an **elevated Windows PowerShell 5.1**
(`Start → type powershell → Run as administrator`), `cd` into the extracted folder, run the commands.
"Expect" is what passes; anything else is a failed scenario and a bug against step 12 or 13.

| # | Scenario | Commands | Expect |
|---|---|---|---|
| 1 | Self-contained, `Loopback` default, prompts | `.\install.ps1` | Prompts for InstallDir (default shown), DataRoot (default shown, 12 GB warning), password (masked). Creates `C:\ArkAscendedServerAdmin\App` and `C:\ArkAscendedServerAdmin`. Service `ArkAscendedServerAdmin` Running. Prints the proxy instruction, not a URL. `Invoke-WebRequest http://127.0.0.1:5000/healthz` returns `ArkAscendedServerAdmin ok`; `http://127.0.0.1:5000/` returns 403. `appsettings.Production.json` contains `PasswordHash` starting `pbkdf2$` and no `Password`. `icacls` on it shows only SYSTEM and Administrators. `icacls C:\ArkAscendedServerAdmin` shows SYSTEM, Administrators, Users RX and nothing about Authenticated Users. `keys\install-pending.json` does not exist. Event log (Application, source `ArkAscendedServerAdmin.Server`) shows DataRoot and readiness lines. |
| 2 | Self-contained, `LanHttps` | `.\install.ps1 -Bind LanHttps -Password 'Sm0ke test!' -Quiet` | Service Running. `keys\web-<ts>.pfx` exists, `Data`, `keys`, `Exports` show only SYSTEM and Administrators. Firewall rule for TCP 5001 with remote address LocalSubnet. From the Proxmox host's browser, `https://<vm-ip>:5001/` shows the certificate warning, then the `/setup` page with the SteamCMD console starting (the download begins; that is expected). Log in with the password: succeeds. `Get-Service` on the VM shows the game download running under the service. |
| 3 | `LanHttps`, then password change | after 2: `.\install.ps1 -SetPassword` | Prompts for the new password, restarts the service, prints "password verified" (the login round trip). Old password refused in the browser, new one accepted. No `.bak` left next to the settings file, no journal in `keys`. |
| 4 | `Proxy` mode | `.\install.ps1 -Bind Proxy -KnownProxies <ip> -Password x -Quiet` | Binds `http://0.0.0.0:5000`; firewall rule TCP 5000 with remote address `<ip>` only. From the Proxmox host, `http://<vm-ip>:5000/healthz` is refused by the firewall (the host is not `<ip>`); from the VM, loopback `/healthz` is `ok` and `/` is 403. |
| 5 | Upgrade with the same zip | after 1: `.\install.ps1` again from a second extracted copy | No prompts (existing service). `App.previous-<ts>` exists next to `App`; `Backups\_app\<ts>-1.0.0-rc.N\` holds the `.db` (and `-wal`/`-shm` if present); settings file identical to before (same hash, same ACL); service Running; `/healthz` ok; journal gone. Second upgrade: the older `.previous-*` and `_app` set are pruned, only the newest remain. |
| 6 | Upgrade, `-NoStart`, `-Verify` | after 1: `.\install.ps1 -NoStart` then `Start-Service ArkAscendedServerAdmin` then `.\install.ps1 -Verify` | After `-NoStart`: service Stopped, `keys\install-pending.json` present with `phase = swapped`, `.previous-<ts>` present, script says "verification pending". A second `.\install.ps1` before `-Verify` is refused naming the pending upgrade. After `-Verify`: journal gone, `.previous-*` pruned to the newest, service Running. |
| 7 | Rollback | after 1: `.\install.ps1 -NoStart`, then `.\install.ps1 -Rollback` | Service Running on the previous folder's files (`install.json` `version` equals the original), database file set restored from `_app`, journal gone, `.new` and the swapped-out folder gone. |
| 8 | Interrupted upgrade | after 1: run `.\install.ps1`, close the PowerShell window the moment "Stopping service" prints | Journal present with the last completed phase. `.\install.ps1` refused with the pending operation named. `.\install.ps1 -Rollback` restores service Running on the original files. |
| 9 | Password change, wrong-password check | after 1: `.\install.ps1 -SetPassword`, enter a password, then when the login round trip runs, it must pass; separately, set `ArkAdmin__PasswordHash` in the service's `Environment` registry value and rerun | First: succeeds as in 3. Second: refused before any prompt with the message naming the environment override. |
| 10 | Uninstall | after 2: `.\uninstall.ps1` | Service gone, firewall rule gone, `App` and `App.previous-*` gone, `C:\ArkAscendedServerAdmin\{Data,keys,Backups,Server,...}` still present with the message saying they were left. |
| 11 | Path validation | `clean`: `.\install.ps1 -InstallDir C:\ -DataRoot C:\ -Quiet -Password x`; then `-DataRoot C:\Windows`; then `-InstallDir C:\X\Server -DataRoot C:\X`; then `-DataRoot <UserProfile>\Downloads` (non-empty, unrelated) | Every one refused before anything is created, each with a message naming the rule. `Get-ChildItem C:\X` afterwards: does not exist. |
| 12 | Framework-dependent, no runtime | `clean`: fdd zip, `.\install.ps1 -Quiet -Password x` | Refused before any packaged exe runs, with the ASP.NET Core 10 runtime download link; nothing created. |
| 13 | Framework-dependent, runtime present | after installing the ASP.NET Core 10 hosting bundle or runtime from the link: `.\install.ps1 -Quiet -Password x` | Same result as scenario 1. Folder size about 20 MB. |
| 14 | Not unblocked | `clean`: extract without unblocking, `.\install.ps1` | The standard "not digitally signed" execution-policy error, and `INSTALL.md` tells the user to unblock the zip first. |
| 15 | pwsh 7 pass | `clean`: install PowerShell 7 with `winget install Microsoft.PowerShell`, then repeat scenarios 1, 3, 5, 10 in an elevated `pwsh` | Same expectations. |
| 16 | Certificate replacement | after 2: `.\install.ps1 -SetCertificate` | A new `keys\web-<ts>.pfx`, the old one deleted after the probe, browser shows the new certificate (new validity dates), login still works. |

### B.4 What is not in the protocol

The 12 GB game download and running a game server are not part of the installer test; scenario 2
confirms the download starts and that is enough. Stop the service (`Stop-Service ArkAscendedServerAdmin`)
after the browser check if the VM disk is small. Instance creation and gameplay are exercised on the
development VM as today.

### B.5 Recording

Copy the table into `docs/.untracked/SMOKE-<version>.md`, add a `Result` column (pass, fail, with the
message on fail), the date, and the VM snapshot name. A release candidate with any fail is not tagged
as `v1.0.0`.

## Appendix: research digest (2026-09-13)

**Competing tools.** Open source: ASMA (Rust, no license file, "not an official Studio Wildcard tool"),
ASAM (.NET 8, Discord bot, backups, clustering, no license file), Ark Server Creation Tool (.NET 9,
GPL-3, DepotDownloader install, firewall automation), Ch4r0ne's manager (Python, MIT, archived 2026-08),
LokiASAM (Tauri, GPL-3, cron automation, restart countdown, CurseForge browser, tiered backups,
Discord/SMTP notifications), ARK ASA Server Manager 2.0 (Tauri, MIT, scheduler, crash guardian, cloud
backups, Discord bridge, uses ARK branding without a disclaimer), jsknnr and POK-manager (MIT,
Linux/Docker via Proton, disclaimers). Commercial: AASM (remote agents, roles, audit log, watchdog, ban
lists, character restore, build rollback, cross-server chat), ASA Server Manager 3.0 (Discord OAuth,
roles, map rotation). Multi-user exists only in the paid tools.

**Licensing.** Studio Wildcard fan content guidelines: fan works must not imply affiliation, sponsorship,
or endorsement; no tool-name policy found; the careful tools carry a "not affiliated" line and ship no
game art. ark.wiki.gg text is CC BY-NC-SA 4.0; map keys, names, and dates are facts and `OfficialMaps`
carries no wiki prose; the one quoted sentence in `LaunchArgumentBuilder` is short and cited. CurseForge
API terms forbid sharing or embedding a key and caching the catalog; per-user keys are the norm; no
attribution requirement found; storing the ids and names of mods the user chose is inventory, not a cache.

**Exposure.** ASA discovery is Epic Online Services: registration is outbound HTTPS, the session's host
address is filled in by Epic from the server's public address, and only the UDP game port must be
reachable inbound. `open <ip>:<gameport>` works from the main-menu console; there are no Steam favorites
or LAN broadcast. Tunnels (playit.gg, VPS relays) produce a listed server that cannot be joined; ngrok
has no UDP; Cloudflare Tunnel has no public UDP; there is no Epic relay for dedicated servers.
`-PublicIPForEpic` is documented for Survival Evolved only. Mono.Nat 3.0.4 is the live .NET UPnP library.
