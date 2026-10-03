<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

---
date: 2026-10-02
repo: ArkServerAdmin
tags:
  - code-review
  - ArkServerAdmin
agents: 4
scope: full solution
language: .NET 10 / Blazor Web App (Interactive Server)
---

# Solution Review: ArkServerAdmin

**Date:** 2026-10-02 08:55
**Scope:** full solution (non-test code, docs, install and build scripts), at `d7fe35a`
**Language:** .NET 10 (`net10.0` Core/Components, `net10.0-windows` Infrastructure/Server), C# latest, ASP.NET Core Blazor Web App with Interactive Server rendering, EF Core 10 on SQLite, Radzen.Blazor
**Files Reviewed:** 300 in scope (binary images and fonts excluded). The reviewers read the security, process, backup, scheduling and command code in full and sampled the rest.

Four independent Opus reviewers received the same brief. There is no `CLAUDE.md` or `REVIEW.md` in the repo, so nothing was suppressed; the only convention applied was American English spelling.

Line numbers come from the reviewers and were not all re-checked. Where two reviewers cited different lines for the same code, both are given.

---

## Critical Issues (Must Fix)

None found.

---

## High Issues (Should Fix)

### 1. Scheduled-action tick can strand an instance lock until the service restarts
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Scheduling/ScheduledActionRunner.cs:216-226` (RunTickAsync / TryClaimAsync)
- **Category:** f. Intent concerns; b. Code smells
- **Agents:** 3/4
- **Avg Severity:** 3.00 (High)
- **Description:** The instance lease is taken (`TryLeaseAsync`, line 216) before the run row is inserted (`TryClaimAsync`, line 221). `TryClaimAsync` only catches the unique-index violation (SQLite 2067). Any other failure in `SaveChangesAsync` leaves `RunTickAsync` without disposing the lease: `SQLITE_BUSY`, a full disk, an I/O error, cancellation, or a foreign-key violation when the owner saves the schedule list between the tick's read and the insert (`SaveScheduledActionsAsync` does not take the instance lock). `ExecuteAsync` logs "tick failed" and keeps ticking, so the lock stays held. From then on every Start, Stop, Restart, Backup, Restore and Delete for that instance is refused with "operation in progress" until the service restarts. The failed `run` entity also stays `Added` in the shared context for the rest of the tick.
- **Recommendation:** Dispose the lease on every path that does not hand it to `RunOneAsync` (try/catch or try/finally around the claim, or `using` plus a hand-off flag). Alternatively claim the occurrence first, acquire the lease afterward, and mark the run Skipped if the lease is busy. Detach `run` on failure or use one context per claim.

### 2. Narrow catch filters in background services let one unexpected exception stop the whole host
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupScheduler.cs:46` (ExecuteAsync); `src/ArkAscendedServerAdmin.Infrastructure/Mods/ModMetadataPoll.cs` (ExecuteAsync; cited at lines 38, 155 and 177 by different reviewers); `src/ArkAscendedServerAdmin.Infrastructure/Mods/ModMetadataRefresher.cs:43,59`
- **Category:** b. Code smells and anti-patterns; f. Intent concerns
- **Agents:** 3/4
- **Avg Severity:** 3.00 (High)
- **Description:** Both loops catch only `DbUpdateException`, `InvalidOperationException` and `IOException`. `Program.cs` never sets `HostOptions.BackgroundServiceExceptionBehavior`, so the default `StopHost` applies and any other exception escaping `ExecuteAsync` shuts the service down. The SCM restarts it a bounded number of times and then stops, leaving the game servers running with nothing supervising them.
  - **BackupScheduler:** a query-time failure surfaces as `SqliteException` (a `DbException`, not a `DbUpdateException`), for example a locked database or a missing table. Unlike `ScheduledActionRunner` and `CrashPolicy`, this service does not wait for readiness. If the startup migration failed, its first tick can kill the host and take the `/setup` failure page with it.
  - **ModMetadataPoll:** a malformed CurseForge response throws `JsonException`, and a null logo throws `NullReferenceException` (see Warning 8). Neither is in the filter.
  - `ScheduledActionRunner` already includes `DbException`, so the three loops are inconsistent.
- **Recommendation:** Add a last-resort `catch (Exception ex) when (!stoppingToken.IsCancellationRequested)` that logs and continues in each loop, or set `BackgroundServiceExceptionBehavior = Ignore` deliberately next to the existing `ShutdownTimeout` configuration. Make `BackupScheduler` wait for `IReadinessMonitor` like the other services.

### 3. The service, the game servers and SteamCMD all run as LocalSystem
- **File:** `install/install.ps1:315-317` (service registration); `src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:392-407` (StartProcess); `docs/hosting.md:15`
- **Category:** d. Security
- **Agents:** 2/4
- **Avg Severity:** 2.50 (High)
- **Description:** The installer registers the service as LocalSystem, and `ArkAscendedServer.exe` and SteamCMD are started as plain children that inherit the same token. The game server is a large native binary listening on internet-facing UDP ports and loading third-party CurseForge mods, so a remote code execution bug in the game or a mod is full SYSTEM on the host. The two reviewers differ on documentation: one notes the choice is described as deliberate in `docs/hosting.md`, the other notes that `SECURITY.md` and `INSTALL.md` do not mention it. This may be an accepted design decision; it is a candidate for `REVIEW.md` if so.
- **Recommendation:** Longer term, run the game and SteamCMD under a low-privilege account (a virtual service account with ACLs on DataRoot, or `CreateProcessAsUser` with a restricted token) and keep elevated rights only for firewall, WMI and junction work. At minimum, state the LocalSystem decision and its blast radius in `SECURITY.md` and `docs/exposing-servers.md`.

---

## Warnings (Consider Fixing)

### 4. `ProcessManager` is a ~1,900-line class with 22 constructor dependencies
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:51-155`
- **Category:** a. Code quality and maintainability; c. Architecture and design
- **Agents:** 4/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** One class owns launch (config write, firewall, port checks), attach and reconcile, the stop job and countdown, the RCON probe, liveness and telemetry loops, crash recovery, projection reservation and runtime bookkeeping. Every reviewer called the concurrency reasoning careful and well documented, and every reviewer also said the size makes each change risky to review; `TestHooks` are needed to reach race windows. `InstancePage.razor` (about 1,050 lines) and `InstanceCommands` (17 dependencies) show the same shape.
- **Recommendation:** Extract cohesive collaborators that share the `Session` type, such as a launch pipeline, a stop/countdown coordinator, a session supervisor (output, probe, liveness, telemetry) and a crash-recovery coordinator. Keep `ProcessManager` as the lease-owning orchestrator.

### 5. Backup zip and verify are synchronous, uncancellable, and hold the instance lock
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupService.cs:56, 164, 169, 323-378` (BackupNowAsync, RunAsync, Verify); `src/ArkAscendedServerAdmin.Infrastructure/Backups/RestoreService.cs:123-145` (VerifyHashes)
- **Category:** e. Performance
- **Agents:** 3/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** `ZipFile.CreateFromDirectory(..., CompressionLevel.Optimal)` and the full decompress-and-SHA-256 `Verify` run synchronously inside an async method over world files that can be hundreds of MB or more. The reviewers named three consequences:
  - The instance lease is held throughout, so Stop, Start and Restart are refused for the whole compression.
  - The cancellation token is ignored during that work.
  - "Backup now" is called from `InstancePage`; with no `ConfigureAwait(false)` in the library, the work runs on the circuit's synchronization context and freezes that circuit's UI. The scheduled path runs on the thread pool and is unaffected.
- **Recommendation:** Release the lease once the snapshot copy and re-inventory are done, since zip and verify only touch the private snapshot directory. Move the work to `Task.Run` or the async `ZipFile`/`ZipArchive` APIs in .NET 10. Consider `CompressionLevel.Fastest` for `.ark` files.

### 6. Login lockout is check-then-record, so parallel requests bypass it and multiply PBKDF2 work
- **File:** `src/ArkAscendedServerAdmin.Server/Auth/LoginService.cs` (LoginAsync; cited at 38-58 and 125-146); `src/ArkAscendedServerAdmin.Core/Auth/LoginThrottle.cs:18-57`
- **Category:** d. Security
- **Agents:** 2/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** `GetLockoutEnd` and `RecordFailure` are separate lock sections with a ~0.3 s PBKDF2 verify and a 1 s delay between them. N concurrent POSTs from one address all pass the lockout check before any failure is recorded, so "5 tries then lock" becomes "5 tries per burst of any size". There is also no limit across addresses, so an unauthenticated flood buys 600k PBKDF2 iterations per request on the box that hosts the game servers.
- **Recommendation:** Reserve the attempt atomically before verifying (for example `TryBeginAttempt(clientKey)` counting in-flight attempts toward the limit) and cap concurrent verifies globally with a small `SemaphoreSlim`, or put ASP.NET Core rate limiting on `/login`.

### 7. CurseForge search pages without a cap and has no empty-page guard
- **File:** `src/ArkAscendedServerAdmin.Core/CurseForge/CurseForgeApi.cs` (SearchModsAsync; cited at 42-57 and 55-86)
- **Category:** e. Performance
- **Agents:** 2/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** The loop requests 50-item pages until `results.Count >= totalCount`. A short term can mean up to about 200 sequential requests for one search. CurseForge rejects `index + pageSize > 10000`, so a broad search ends in an error that discards everything already fetched. If a page returns no items while `TotalCount` is still larger, `results.Count` never grows and the loop does not terminate on its own.
- **Recommendation:** Cap the results (the first one to three pages), break when a page returns fewer than `PageSize` items, and never request past index 10,000. Add paging or "load more" in the UI if more is needed.

### 8. A null `logo` from CurseForge throws and aborts the refresh or add
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Mods/ModMetadataRefresher.cs:59,63`; `src/ArkAscendedServerAdmin.Server/Commands/ModCommands.cs:130,207`; `src/ArkAscendedServerAdmin.Core/CurseForge/Models/Mods/Mod.cs:19`
- **Category:** f. Intent concerns
- **Agents:** 2/4 (one as a separate finding, one as a cause inside finding 2)
- **Avg Severity:** 2.00 (Warning)
- **Description:** `Mod.Logo` is non-nullable with an `= new()` default, but System.Text.Json still assigns `null` for `"logo": null` unless `RespectNullableAnnotations` is on. The code guards `mod.Links?.WebsiteUrl` but dereferences `mod.Logo.ThumbnailUrl` directly. In the refresher one logo-less mod throws mid-loop, no rows are saved, and the exception reaches the poll loop (finding 2). In `AddAsync` the exception is not in the caught set.
- **Recommendation:** Use `mod.Logo?.ThumbnailUrl` and declare `Logo` and `Links` as nullable on the DTO so the compiler enforces the checks.

### 9. Firewall rule names are keyed only by instance id, so installs on one box collide
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Firewall/FirewallRules.cs:18-22, 37-48, 151` (RuleName, EnsureInstanceRules)
- **Category:** f. Intent concerns
- **Agents:** 2/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** Rules are named `ArkAscendedServerAdmin-<instanceId>`. `EnsureInstanceRules` deletes same-named rules whose port differs and `RemoveInstanceRules` deletes every rule with that name. A dev DataRoot next to the live service, or a second install with a custom `-ServiceName`, shares ids starting at 1, so starting instance 1 in one installation removes the other installation's rule for its instance 1. The class comment also says "the description names the port", but no description is set.
- **Recommendation:** Add an installation discriminator to the name (service name or a DataRoot hash) and set the description the comment promises.

### 10. N+1 latest-backup queries and an un-split four-collection Include
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/InstanceCommands.cs:53-72` (GetDashboardAsync); `src/ArkAscendedServerAdmin.Server/Commands/ClusterCommands.cs:55-63` (GetAsync); `src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupScheduler.cs:64-76` (RunDueBackupsAsync)
- **Category:** e. Performance
- **Agents:** 4/4
- **Avg Severity:** 1.50 (Warning)
- **Description:** Each site issues one `BackupRecords` query per instance; the dashboard does it on every reload and the scheduler every minute. The dashboard query also includes `Cluster.Mods`, `Cluster.ScheduledActions`, `Mods` and `ScheduledActions` in a single query, which produces a cartesian product, and no split-query behavior is configured. Two reviewers noted the cost is minor at this app's scale.
- **Recommendation:** Fetch the newest record per instance in one grouped query and pass it as a dictionary. Add `.AsSplitQuery()` to the multi-collection includes or set `UseQuerySplittingBehavior(SplitQuery)` globally.

### 11. Retention pruning drops the database row even when the archive delete fails
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupService.cs:502-510` (PruneAsync)
- **Category:** f. Intent concerns
- **Agents:** 2/4
- **Avg Severity:** 1.50 (Warning)
- **Description:** `TryDelete` swallows `IOException` and `UnauthorizedAccessException` (a file held by antivirus, Explorer or an open restore), and `db.BackupRecords.Remove(record)` runs regardless. The zip stays on disk with no record, so neither retention nor the UI can see it and disk use can grow past what the retention setting promises.
- **Recommendation:** Remove the row only when the file is gone, otherwise keep it and log. Optionally sweep the backup directory for `*.zip` files that no record references.

### 12. UI components bypass the guarded command facades
- **File:** `src/ArkAscendedServerAdmin.Components/Pages/Instances/InstancePage.razor:8-12, 687, 720`; `Pages/Players.razor:7`; `Shared/WhitelistEditor.razor:5`; `Pages/Clusters/ClusterPage.razor:6-7`; `Pages/Home.razor`; `Layout/Rail.razor:4`
- **Category:** c. Architecture and design
- **Agents:** 2/4
- **Avg Severity:** 1.50 (Warning)
- **Description:** The stated invariant is that every UI call goes through a facade that re-checks authorization. Pages instead inject `IProcessManager` (which exposes Start, Stop and DismissCrash), `IBackupService`, `IPlayerTracker` and `IRconHistoryStore`. `InstancePage` calls `IRconHistoryStore.LoadAsync` and `AppendAsync` (a file write) with no guard. Both reviewers said nothing is exploitable today because the calls are read-only or follow a guarded call; the concern is that a later handler can call a mutator directly.
- **Recommendation:** Route RCON history load and append through `IInstanceCommands`, and expose the events and runtime reads through a narrow read-only interface instead of the full services.

---

## Suggestions (Nice to Have)

### 13. Per-session `CancellationTokenSource` and `SemaphoreSlim` are never disposed
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:1843, 1884, 1376` (Session, HandleExitAsync)
- **Category:** b. Code smells
- **Agents:** 3/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** `Session.Cancellation` is a linked source on `ApplicationStopping`. `HandleExitAsync` cancels it but never disposes it, so each launch or attach leaves a registration on the lifetime token (and a `WriteMutex`) until shutdown. Small, but it grows with every scheduled restart or crash-restart cycle.
- **Recommendation:** Dispose `session.Cancellation` and `WriteMutex` at the end of `HandleExitAsync`, after the loops have observed cancellation. `TrySkipCountdown` already tolerates `ObjectDisposedException`.

### 14. British spellings in user-facing strings, docs and comments
- **File:** `src/ArkAscendedServerAdmin.Core/Processes/LaunchQueue.cs:69, 156`; `src/ArkAscendedServerAdmin.Infrastructure/Install/SteamCmdRunner.cs:59-60`; `src/ArkAscendedServerAdmin.Infrastructure/Processes/ProcessManager.cs:1337, 1716`; `docs/guide/instances.md:346`; `StartupOrchestrator.cs:63`; `LogTailOutputSource.cs:26`; `ScheduleEditor.razor:187`; `CronDialog.razor:1`
- **Category:** g. Naming, consistency, and readability
- **Agents:** 3/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** "cancelled", "cancelling" and "signalled" appear about 22 times, including the user-visible messages "Launch cancelled before it started.", "SteamCMD run cancelled." and "was not signalled". The convention is American English.
- **Recommendation:** Change to "canceled", "canceling" and "signaled". Update the docs table row that quotes the LaunchQueue message and any tests that assert on these strings.

### 15. `SaveOverrideAsync` edits a row without checking it belongs to the instance
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/ConfigCommands.cs` (SaveOverrideAsync; cited at 78-102 and 533-555)
- **Category:** f. Intent concerns
- **Agents:** 2/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** The duplicate check filters on `entry.InstanceId`, but the update loads the row by `entry.Id` alone, so a mismatched pair edits another instance's override and checks uniqueness against the wrong instance. `entry.File` is not checked with `Enum.IsDefined`. A row deleted while being edited throws `InvalidOperationException` instead of returning `CommandResult.Fail`. With a single owner this is a correctness issue rather than an access one.
- **Recommendation:** Load with `o.Id == entry.Id && o.InstanceId == entry.InstanceId`, validate `File`, and return `Fail` instead of throwing.

### 16. Resuming maintenance reports success when nothing resumed
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/SettingsCommands.cs` (MaintenanceCommands.ResumeMaintenanceAsync; cited at 208-231 and 543-567); `src/ArkAscendedServerAdmin.Infrastructure/Maintenance/UpdateService.cs:128-131` (ResumeAsync)
- **Category:** f. Intent concerns
- **Agents:** 2/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** The facade fires `ResumeAsync` and returns `Success` immediately. `ResumeAsync` returns silently when the operation lock is already held, so the owner is told the resume started when it did not. One reviewer added that the `Task.Run` uses `CancellationToken.None` and is not registered with `DetachedJobs`, which restore and delete use so that shutdown waits for them.
- **Recommendation:** Check `IsOperationInProgress` first and return a rejection, or have `ResumeAsync` return an outcome. Register the job with `DetachedJobs` and pass the application-stopping token.

### 17. SQLite connection strings built by string interpolation
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/ServiceCollectionExtensions.cs:39`; `src/ArkAscendedServerAdmin.Infrastructure/Data/SqliteConfigBackupExporter.cs` (cited at 27, 34 and 144, 151)
- **Category:** b. Code smells
- **Agents:** 2/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** `$"Data Source={path}"` breaks or picks up extra keywords when the DataRoot path contains `;` or `=`, both legal in Windows paths. In the exporter, the `SqliteConnection` passed to `ClearPool` is also never disposed.
- **Recommendation:** Use `new SqliteConnectionStringBuilder { DataSource = path }.ToString()`.

### 18. Dead code: `IServerModService`, `ServerMod`, `ServerModCategory`, `ModelExtensions`
- **File:** `src/ArkAscendedServerAdmin.Core/Services/IServerModService.cs`; `src/ArkAscendedServerAdmin.Core/ServerMod.cs`; `src/ArkAscendedServerAdmin.Core/ServerModCategory.cs`; `src/ArkAscendedServerAdmin.Core/ModelExtensions.cs`
- **Category:** a. Code quality and maintainability; h. Other
- **Agents:** 2/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** The interface has no implementation and the mapping extensions have no callers. `ServerMod.Categories` is initialized `= null!`, and the file `ModelExtensions.cs` holds a class named `ModExtensions`. `ICurseForgeApi` sits in the `CurseForge.Models.Services` namespace, and the CurseForge DTOs use a different style from the rest of Core (unsealed mutable classes, braceless `if`).
- **Recommendation:** Delete the four files, move `ICurseForgeApi` next to `CurseForgeApi`, and align the DTO style.

### 19. Case-insensitive EOS id lookups cannot use the index
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Players/PlayerTracker.cs:88-91, 148-149`; `src/ArkAscendedServerAdmin.Infrastructure/Data/AppDbContext.cs:142-144`
- **Category:** e. Performance
- **Agents:** 2/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** Lookups use `p.EosId.ToLower() == id`, which cannot use the unique index, so every 15-second probe per instance and every join or leave line scans `KnownPlayers`. The index is BINARY collation, so it also does not enforce the case-insensitive uniqueness the code assumes.
- **Recommendation:** Give the column `UseCollation("NOCASE")` or store the id lower-cased, then compare directly. This needs a migration.

### 20. Comment defects in `AppSettings` and `GeneratedConfigWriter`
- **File:** `src/ArkAscendedServerAdmin.Core/Configuration/AppSettings.cs:47`; `src/ArkAscendedServerAdmin.Infrastructure/Provisioning/GeneratedConfigWriter.cs:19-24`
- **Category:** g. Naming, consistency, and readability
- **Agents:** 2/4 (typo); 1/4 (HANDOVER reference)
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** "Unioned into every instance.s" should read "instance's". The `GeneratedConfigWriter` remarks say the whitelist path is "unverified … on the owner-in-the-loop list in HANDOVER §5"; HANDOVER is untracked, so readers of the public repo cannot resolve the reference.
- **Recommendation:** Fix the typo. Confirm the path and drop the remark, or point to a tracked issue.

---

## Potential Issues (Single Agent - Manual Review)

> These were flagged by a single agent. They may be valid or false positives.
> Review manually to decide.

### 21. Custom map key flows unvalidated into filesystem paths
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/MapCommands.cs:36-56` (SaveAsync); `src/ArkAscendedServerAdmin.Core/Configuration/DataRootLayout.cs:93` (InstanceWorldDirectory)
- **Category:** d. Security
- **Agents:** 1/4
- **Avg Severity:** 3.00 (High)
- **Description:** A custom map key is checked for emptiness, length, whitespace and `ReservedKeys.ValidateTypedValue` (`?`, `=`, line breaks). It may still contain `\`, `/`, `..` or a drive root. `InstanceWorldDirectory` does `Path.Combine(saved, slug, mapKey)`, so `..\..\..\x` escapes DataRoot and a rooted key replaces the base path entirely. BackupService reads that directory, and RestoreService deletes files there and extracts into it, all as LocalSystem. Exploiting this needs the web password, but `SECURITY.md` lists path handling that writes outside DataRoot as in scope.
- **Aggregation note:** The validation in `MapCommands.SaveAsync` was re-read during aggregation and matches the description: no path-separator or rooted-path check is present there. The downstream path use in `DataRootLayout` and the restore code was not re-checked.
- **Recommendation:** Validate the key as a single plain path segment (reuse `RestoreArchiveRules.CheckSegment` or an allowlist such as `^[A-Za-z0-9_]+$`), and assert in `DataRootLayout` that every resolved path stays under Root.

### 22. Generated INI holding the admin/RCON password is readable by every local user
- **File:** `install/ArkInstall.Common.ps1:361-372` (Set-InstallAcls); `src/ArkAscendedServerAdmin.Infrastructure/Provisioning/GeneratedConfigWriter.cs:68-74`; `src/ArkAscendedServerAdmin.Core/Configuration/DataRootLayout.cs:70-90`
- **Category:** d. Security
- **Agents:** 1/4
- **Avg Severity:** 3.00 (High)
- **Description:** `Set-InstallAcls` grants Users read and execute on DataRoot with inheritance and locks down only `keys`, `Data`, `Exports` and `Backups\_app`. Everything else inherits the Users grant, including the generated `GameUserSettings.ini` (which holds `ServerAdminPassword`, also the RCON password), the source INIs under `Instances\<slug>\Config` and `Clusters\<slug>\Config`, and `rcon-history.txt`. RCON listens on loopback, so any non-admin interactive account on the box could read the password and take game-admin control. `SECURITY.md` lists reading from a non-admin account as in scope. The database that mirrors the same INI text is protected; the files are not.
- **Aggregation note:** A grep of the installer confirms `Set-ProtectedAcl` is called with `$true` (Users read) at line 364 and `$false` at line 369; which directories fall on each side was not re-checked.
- **Recommendation:** Apply `Set-ProtectedAcl … $false` to `Instances` and `Clusters`, or have the app create the `Config` and `Saved\Config` folders with a protected DACL. If Users read is intended there, say so in `SECURITY.md`.

### 23. The anonymous `/Error` page shows the version and system state
- **File:** `src/ArkAscendedServerAdmin.Components/Pages/Error.razor:1-2`; `src/ArkAscendedServerAdmin.Components/Layout/Rail.razor:27-68`
- **Category:** d. Security; f. Intent concerns
- **Agents:** 1/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** `/Error` is `[AllowAnonymous]`, reachable by a plain GET, and renders inside `MainLayout`, whose `Rail` shows the app version, readiness phase, running-instance count, installed game build and maintenance detail text outside any `AuthorizeView`. That contradicts the intent stated on `HealthEndpoint` ("a fixed line of text with no version and no state") and opens an interactive circuit for an unauthenticated visitor.
- **Recommendation:** Give `Error.razor` `@layout LoginLayout` or a minimal layout, or move the Rail's status block and version under `<AuthorizeView>`.

### 24. `ClusterCommands` downcasts its injected interface to the concrete class
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/ClusterCommands.cs:105` (CreateAsync)
- **Category:** c. Architecture and design
- **Agents:** 1/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** `((InstanceCommands)instanceCommands).SeedIniAsync(...)` depends on the registered `IInstanceCommands` being exactly `InstanceCommands`. A decorator or test double throws `InvalidCastException` at runtime, and one guarded facade depends on another.
- **Recommendation:** Move `SeedIniAsync` into a small internal service built on `IIniSourceStore` that both facades inject.

### 25. Scheduled-action runs are pruned only at service start
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Scheduling/ScheduledActionRunner.cs:146-176` (RecoverAsync)
- **Category:** f. Intent concerns
- **Agents:** 1/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** The 30-day retention runs only in the start-of-service pass. The comment "the table holds at most a month of rows" holds only if the service restarts at least monthly; a long-running service with per-minute RCON schedules grows `ScheduledActionRuns` without bound. Each pass also loads every row into memory.
- **Recommendation:** Run the prune daily from the tick loop, and store `StartedAt` in a form SQL can filter (see finding 27).

### 26. A stale INI editor can recreate a deleted owner's folder
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Provisioning/IniSourceStore.cs:210-226` (ResolvePathAsync), `279-308` (SaveAsync)
- **Category:** f. Intent concerns
- **Agents:** 1/4
- **Avg Severity:** 2.00 (Warning)
- **Description:** Slugs are cached per owner id for the life of the service and never evicted on delete, and `SaveAsync` never re-checks that the owner exists. `AtomicFile.WriteTempAsync` calls `Directory.CreateDirectory`, so a save from an editor left open after deleting the instance or cluster recreates `Instances\<slug>\Config\…` as an orphan, then the mirror insert fails on the foreign key.
- **Recommendation:** Evict the cache entry on delete, or confirm the owner row exists under the gate before writing.

### 27. Recurring "SQLite cannot compare or order `DateTimeOffset`" workarounds
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/CommandSupport.cs:232, 283-309`; `src/ArkAscendedServerAdmin.Infrastructure/Scheduling/ScheduledActionRunner.cs:165-170`; `src/ArkAscendedServerAdmin.Infrastructure/Backups/BackupScheduler.cs`
- **Category:** a. Code quality and maintainability
- **Agents:** 1/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** Several queries order by `Id` as a proxy for time or load whole tables to filter by date in memory. This relies on id and time order staying aligned and grows with table size.
- **Recommendation:** Add a convention-wide `DateTimeOffsetToBinaryConverter` (or UTC ticks) so filters and ordering run in SQL. This needs a migration.

### 28. Whitelist validation differs between manager, cluster and instance
- **File:** `src/ArkAscendedServerAdmin.Server/Commands/CommandSupport.cs:124-127` (NormalizeWhitelist); `src/ArkAscendedServerAdmin.Core/Configuration/AppSettings.cs:93-96`
- **Category:** g. Naming, consistency, and readability
- **Agents:** 1/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** The manager-wide whitelist rejects whitespace and control characters inside an id. Cluster and instance whitelists only trim lines, so `abc def` is accepted and written verbatim into `AllowedCheaterAccountIDs.txt`.
- **Recommendation:** Share one validator across all three editors.

### 29. Port-conflict exclusion matches by name with different case folding from the uniqueness check
- **File:** `src/ArkAscendedServerAdmin.Core/Ports/PortAllocator.cs:118` (FindConflicts)
- **Category:** f. Intent concerns
- **Agents:** 1/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** `FindConflicts` skips any other instance whose name matches under `OrdinalIgnoreCase`, while name uniqueness uses SQL `lower()`, which folds ASCII only in SQLite. Two names differing only in non-ASCII case pass uniqueness and then skip each other's port check. Callers already exclude by id, so the name skip is redundant.
- **Recommendation:** Drop the name-based skip and rely on the id-based exclusion.

### 30. Firewall rules open the port for any program on every profile
- **File:** `src/ArkAscendedServerAdmin.Infrastructure/Firewall/FirewallRules.cs:24-60` (EnsureInstanceRules)
- **Category:** d. Security
- **Agents:** 1/4
- **Avg Severity:** 1.00 (Suggestion)
- **Description:** The rules are port-only allows (UDP game port and port+1) on the Domain, Private and Public profiles with no application path, so any process that binds the port while the instance is down is reachable from outside.
- **Recommendation:** Scope each rule to the instance's `ArkAscendedServer.exe` path, which the layout already knows.

---

## Positive Observations

- **Authorization is defense in depth** (4 agents noted this). The cookie `ValidatePrincipal`, circuit revalidation every five minutes and a guard call at the top of every facade method all apply one rule (`PasswordClaimValidator`). A password change invalidates all sessions, and the claim is issued from the snapshot `Verify` matched, which closes the reload race at sign-in.
- **Credential and cookie handling is strict** (4 agents). PBKDF2-SHA256 at 600k iterations with length-checked parsing that fails closed, constant-time comparison, a DPAPI-protected Data Protection key ring, `SameSite=Strict`/`HttpOnly`/`Secure` cookies, antiforgery-validated logout and open-redirect validation on `returnUrl`.
- **HTTPS fails closed** (3 agents). The guard runs after forwarded headers from explicitly trusted proxies only, and the anonymous health probe discloses nothing.
- **Restore validation is allowlist-based** (4 agents). Archive entry names are checked segment by segment (no traversal, ADS, reserved device names or case-only duplicates), entries map one to one to the manifest with lengths and hashes verified before anything is deleted, file names resolve only through the instance's own backup record, and reparse points are refused.
- **Command-line and INI injection is closed off** (4 agents). One `ReservedKeys` policy covers typed values, free-text arguments and INI overrides, `?` map-string injection is blocked, and processes start through `ProcessStartInfo.ArgumentList` with no shell.
- **The concurrency design is careful and documented** (4 agents). Non-reentrant per-instance leases with `*Core`/`*UnderLease` variants, a maintenance gate, a projection reservation, compare-and-swap runtime updates scoped to a session, a write mutex that orders database mirrors before exit cleanup, an epoch-guarded crash-recovery marker, and countdowns aimed at an absolute deadline.
- **Scheduled actions get once-per-occurrence semantics from a unique index** rather than in-memory state, and interrupted runs are recovered at startup (2 agents).
- **File writes are atomic and concurrency-checked** (4 agents). Temp file plus same-volume rename or `File.Replace`, optimistic SHA-256 concurrency on INI saves, a `Version` row on App Settings, and SQLite's online backup API for the config export.
- **Build hygiene is strict** (4 agents). Nullable on, warnings as errors, code style enforced in the build, analyzers at `latest`, Central Package Management, deterministic builds and MinVer versioning. `TimeProvider` is injected throughout.
- **The installer is thorough** (3 agents). It refuses non-empty folders it does not own, journals the install with rollback, writes protected files ACL-first with read-back verification, takes the password as a SecureString and stores only the hash, and configures bounded SCM restart actions.
- **Layering is clean** (1 agent). Core holds pure testable logic, Infrastructure holds Windows and I/O concerns, Server holds the guarded facades, and singletons use `IDbContextFactory`, so there are no captive DbContexts.
- **Doc comments explain the why** (3 agents), recording design decisions, spike results and edge cases at the code, and user-facing error messages name the instance, the field and the fix.
- **No raw HTML rendering** (3 agents). There is no `MarkupString` or `innerHTML`, CurseForge website links are vetted (https, curseforge.com hosts only, no userinfo) before they are stored or rendered, and `target=_blank` links use `rel="noopener noreferrer"`.
- **Event subscriptions in components are balanced** (3 agents). Every `+=` has a matching `-=` in `Dispose`, subscriber exceptions are isolated, and console buffers are bounded rings.
- **The SteamCMD pseudo-console launcher is robust** (1 agent), with documented handle ownership and teardown order and a fallback to the pipe launcher.
