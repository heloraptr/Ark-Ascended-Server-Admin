# Game updates

The **Update game** page runs SteamCMD against the one shared game install under
`DataRoot\Server`. Before SteamCMD touches a file, every running instance is stopped with a
verified exit; afterward the install is checked against Steam's app manifest and the instances
that were running are started again through the launch queue. Every step of the flow is written to
the database before it runs, so an update interrupted by a service restart picks up where it
stopped, and a relaunch that fails waits for you to **Retry** or **Skip** it.

In one run the manager compares the installed build with Steam's and downloads only what changed
(`app_update 2430930`), optionally verifies every game file (`validate`), stops every live instance
first with the countdown broadcast, a save, and a graceful exit that is verified before SteamCMD
runs, refuses to go near SteamCMD while any
`ArkAscendedServer.exe` under `DataRoot` is still alive (even one it does not know about), retries
SteamCMD with backoff when Steam throttles anonymous downloads, and relaunches the stopped instances
one at a time through the stagger queue.

## The page

Open **Update game** from the bottom of the sidebar (the button is disabled until the service is
**Ready**) or go to `/update`. The panel shows:

| Row | What it shows |
|---|---|
| **Installed build** | The `buildid` from `DataRoot\Server\steamapps\appmanifest_2430930.acf`, in red with the checker's reason if the install is not verified. |
| **Last run** | The summary of the last SteamCMD run since the service started, for example "Updated from build 12345 to build 12400." or "Already on the latest build (12400).", or "No update has run since the service started." |
| **Status** | **Idle**, or the current phase: **Stopping instances for update**, **Updating the game**, **Restarting instances**, with "since HH:mm:ss". |

Under those: a progress bar while SteamCMD downloads (state, bytes done of total), the list of
instances the run is acting on (`waiting`, `done`, or the error with **Retry** and **Skip**), and
the SteamCMD console.

## Running an update

1. Tick **Verify game files** if you want `validate` for this run. When **SteamCMD validate** is on
   in Settings the box is checked and disabled with "Always on: the SteamCMD validate setting is
   enabled on the Settings page." Verifying is slow, which is why it is a toggle rather than the
   default; reach for it when the install looks broken.
2. Press **Check for updates** when nothing is running, or **Stop *N* instances and update** when
   something is. The second one asks for confirmation: "*N* instances are running. Each gets the
   broadcast countdown, a world save, and a graceful stop before SteamCMD runs, then starts again."
   Confirm with **Stop and update**.
3. Watch the console. The bottom of the sidebar shows the phase on every page, and when the flow ends
   a toast says "Game updated" or "Game is up to date" with the summary, or "Update stopped" with the
   reason.

While a phase is **Stopping instances for update** or **Updating the game**, both the Update page
and the Instances page offer **Resume update**, which re-runs the interrupted phase in the
background (its tooltip: "Re-runs the interrupted phase if SteamCMD failed and nothing is
running.").

## The phases

The flow is a state machine whose every decision is persisted to the single `MaintenanceStates`
row (phase, the list of instance entries with `done` and `error`, start time) before the action it
decided on is executed.

### Begin

The request is refused if an update or recovery already holds the operation lock, if the
persisted phase is not `None`, if any instance is in the **Unknown** state (reconciliation found
more than one process), if any instance is in **Identity not saved**, or if instances are running
and you did not confirm. Then the maintenance gate is taken exclusively: the launch queue drains,
in-flight launches finish, and from here until the install is verified no start can slip in
(**Start** answers `update in progress`). The set of live instances is collected, the phase becomes
**Stopping**, the SteamCMD console is cleared, and the console reads "Update started; stopping *N*
instance(s)."

### Stopping

Every entry is stopped with `RequireVerifiedExit`: the pre-stop broadcast countdown
(unless set to zero), `doexit` over RCON, a wait of **Graceful stop timeout** seconds, then a kill
if needed, and the exit must be observed. An instance that is already dead is marked done. If any
stop cannot be verified the update ends with "Could not stop every instance with a verified exit
(instance *N*: ...). The stopped instances stay stopped; fix the cause and start the update again."
Once every entry is done, the manager enumerates processes and refuses if any
`ArkAscendedServer.exe` still runs from under `DataRoot`: "ArkAscendedServer.exe is still running
under the data root (*path* (PID *n*)); stop it before updating."

### Updating

The console reads "Running SteamCMD app_update 2430930 [validate]; installed build
*X*." SteamCMD is downloaded and extracted to `DataRoot\SteamCMD` if `steamcmd.exe` is missing,
then run with

```
steamcmd.exe +force_install_dir <DataRoot>\Server +login anonymous +app_update 2430930 [validate] +quit
```

Anonymous login is the default because the dedicated server depot needs no account.

Exit code 7 (SteamCMD updated itself) re-runs immediately, up to three times in a row. Any other
non-zero exit, or exit 0 without a verified manifest, costs one of five attempts with waits of
30 s, 60 s, 120 s, and 240 s between them ("Attempt *N* failed (...); retrying in *X* (attempt *N+1*
of 5)."). Steam throttles anonymous downloads in practice, and without the backoff a single failed
attempt meant a manual retry.

The install counts as verified only when `appmanifest_2430930.acf` reports `StateFlags 4`;
a folder that exists is never taken as proof. On success the console prints the summary, the
**Last run** row and the sidebar update, and the phase becomes **Restarting**. On failure the
phase stays **Updating** with the detail "SteamCMD did not produce a verified install: ..." so
that **Resume update** (or the next service start) runs SteamCMD again; nothing is launched against
an unverified install.

### Restarting

The gate is released. Entries are launched one at a time through the normal start
path (recovery launches are allowed before the service is fully **Ready**), which means the stagger
delay from Settings applies between them. Each entry is persisted `done` as soon as its process is
started and its identity saved, so a restart in the middle relaunches only what has not launched.
An instance that is already alive is marked done without a launch. A failed launch records its
error on the entry; when nothing launchable is left the flow parks with "Update recovery
incomplete: instance *N*: ... Retry or skip each instance." **Retry** clears the error and launches
that entry again; **Skip** marks it done without launching. When every entry is done the phase
returns to `None` and the console reads "Update complete; every instance has been relaunched or
resolved."

## Picking up after a service restart

The "Resuming interrupted maintenance" step during startup reads the row. A **Stopping** or
**Updating** phase re-takes the gate and runs to the end of SteamCMD before the service reports
**Ready**; a **Restarting** phase hands the pending relaunches to a background task so readiness
does not wait for them. The console reads "Resuming interrupted update from the *Phase* phase." A
`steamcmd.exe` left running from the previous service instance is waited for and then killed, so two
SteamCMDs never write the same install.

## Why every transition is written down first

DESIGN.md fixed the update flow as: broadcast countdown, graceful stop of running instances,
SteamCMD, staggered restart of the ones that were running, and blocked while instances run unless
confirmed. Persisting every transition, and verifying the manifest rather than trusting an exit
code, come from the single-install layout: every instance shares `DataRoot\Server` through
junctions, so a half-written update or a launch against it would break all of them at once. The
verified stop and the foreign-process check are the same rule from the other side: nothing may hold
the binaries while SteamCMD replaces them.

## Messages, and what to do about them

| Message | Meaning and what to do |
|---|---|
| `An update is already in progress.` | Wait for the phase to return to **Idle**. |
| `The previous stopping phase is unresolved; resolve the recovery on the Dashboard first.` | A prior run is parked. Use **Retry**, **Skip**, or **Resume update** on the Instances page banner. |
| `Resolve ambiguous instances first: reconciliation found more than one process for at least one instance.` | An instance shows **Unknown**. Stop the extra `ArkAscendedServer.exe` by hand and restart the service ([instances.md](instances.md)). |
| `Instance identity unresolved for #N; retry persisting the identity or stop the instance first.` | An instance shows **Identity not saved**. Press **Retry persist** on its page. |
| `N instance(s) are running (#N); confirm stopping them to update.` | The confirmation was declined or bypassed. Use the **Stop N instances and update** button. |
| `Could not stop every instance with a verified exit (instance N: ...). The stopped instances stay stopped; fix the cause and start the update again.` | A stop failed (typically `Pid N is still alive after kill`). Check Task Manager, end the process, and start the update again. |
| `ArkAscendedServer.exe is still running under the data root (...); stop it before updating.` | A server the manager does not own runs from `DataRoot`. End it, then **Resume update** or start again. |
| `SteamCMD did not produce a verified install: SteamCMD failed after 5 attempt(s): ...` | Five attempts failed. Steam is down or throttling; try **Resume update** later. Starts stay refused until the install is verified. |
| `SteamCMD download failed: ...` | `steamcmd.zip` could not be fetched from Steam's CDN. Check the box's outbound HTTPS. |
| `SteamCMD is already running.` | Another run holds SteamCMD (a resume racing a manual run). Wait for it. |
| `Update recovery incomplete: instance N: <error>. Retry or skip each instance.` | A relaunch failed; the error is the start refusal (port conflict, missing password, ...). Fix it and **Retry**, or **Skip** and start the instance by hand later. |
| `There is no restart to retry.` / `Instance N has no failed restart to retry.` | The entry is not in a failed **Restarting** state; the banner is stale. Reload the page. |
| `An update operation is in progress; try again when it finishes.` | **Retry** or **Skip** was pressed while the flow was still moving. |
| `There is no interrupted update to resume.` | **Resume update** was pressed with the phase at **Idle**. |
| Start button answers `update in progress` | The gate is held. Wait for the update to finish. |

The SteamCMD console on the Update page (and on `/setup` during the first install) holds the raw
SteamCMD output for the current run; the service log has the same lines with timestamps
([troubleshooting.md](troubleshooting.md#where-every-log-lives)).
