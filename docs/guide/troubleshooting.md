# Troubleshooting

Sorted by what you see first. Messages are quoted as the code emits them; a `<placeholder>` stands
for the value the manager fills in.

## Every page shows `/setup`

Any URL lands on the Setup page with an indicator and one of **Initializing**, **Recovering**,
**Installing the game**, **Install failed**, or **Startup failed**.

The service has not finished starting. Until it has, every page except `/setup`, `/login`, and
`/healthz` is redirected there, and **Start** is refused inside the service too. On a fresh install
it is downloading SteamCMD and the 12 GB game install, which is the normal first-run wait
([first-run.md](first-run.md)).

| Phase on the page | Cause | Fix |
|---|---|---|
| **Installing the game** with a progress bar | SteamCMD is running. | Wait. The SteamCMD console on the page shows the download. |
| **Install failed** with the hint "SteamCMD retried five times with backoff before giving up. Anonymous Steam downloads are sometimes throttled; trying again later usually works." | Five SteamCMD attempts failed, or `steamcmd.zip` could not be downloaded (`SteamCMD download failed: ...`). | Press **Retry install**. Check the box's outbound HTTPS if it keeps failing. |
| **Install failed**: "Install finished but could not be verified" | SteamCMD exited 0 but `DataRoot\Server\steamapps\appmanifest_2430930.acf` does not report `StateFlags 4`. | **Retry install**; tick **SteamCMD validate** on Settings first if it recurs. |
| **Startup failed**: "Startup failed; check the log and restart the service" | A startup step threw (database migration, process reconciliation, a `DataRoot` that cannot be created). The error text is on the page. | Read the event log entry, fix the cause, restart the service. "Only a service restart clears a failed pipeline step." |
| **Recovering** for a long time | An interrupted update is being resumed; SteamCMD runs before **Ready**. | Wait; the SteamCMD console shows progress ([game-updates.md](game-updates.md)). |

## A direct `http://` request answers 403

The browser shows `HTTPS is required. Connect through the reverse proxy.` with status 403.

The HTTPS guard refuses every request whose effective scheme is not HTTPS. With the default
`Loopback` bind the app listens on `http://127.0.0.1:5000` for a reverse proxy that terminates TLS
and forwards `X-Forwarded-Proto`; hitting that port directly, or a `LanHttps` box over plain
`http://`, is refused. Only `/healthz` is exempt.

Open the proxy's HTTPS address, or the `https://<box-ip>:5001/` address in `LanHttps` mode. If the
proxy is set up but you still get 403, its address is not in `ArkAdmin:KnownProxies` or it is not
sending `X-Forwarded-Proto`. [hosting.md](../hosting.md#bind-modes) has the three modes and proxy
examples.

## Login is refused

| You see | Cause | Fix |
|---|---|---|
| `No usable login password is configured. Set ArkAdmin:PasswordHash (or ArkAdmin:Password) in appsettings.json and restart the service.` | Neither key is set, or `PasswordHash` is present but malformed (wrong prefix, wrong field count, wrong salt or hash length, iterations outside 10 000 to 5 000 000). A malformed hash refuses every login with no fallback to `Password`. The event log has `ArkAdmin:PasswordHash is not a valid pbkdf2$<iterations>$<salt>$<hash> string. Every login will be refused; generate one with --hash-password.` or `No login password is configured ...`. | Run `install.ps1 -SetPassword`, or hash one by hand and put it in `appsettings.Production.json` ([configuration.md](../configuration.md#the-password)). |
| `Incorrect password.` | Wrong password. Each failure waits one second before answering. | Type it again. If both `Password` and `PasswordHash` are set the hash wins (the log warns `Both ArkAdmin:Password and ArkAdmin:PasswordHash are set; the hash is used and Password is ignored.`). |
| `Too many failed attempts. Try again in a few minutes.` | Five failures from your address inside five minutes; the address is locked out for five minutes. | Wait five minutes. A service restart also clears it. |
| Signed in, then sent back to the login page | The password in configuration changed; every cookie carrying the old credential is refused on its next request and every circuit at its next revalidation. | Sign in with the new password. |

## An instance will not start

The refusal appears as a toast and, before you press anything, as the "Start would be refused."
notice on the instance page.

| You see | Cause | Fix |
|---|---|---|
| `The service is not ready yet (<Phase>: <Message>).` | The service has not finished starting. | Watch `/setup`. |
| `update in progress` | An update holds the maintenance gate. | Wait for it ([game-updates.md](game-updates.md)). |
| `operation in progress` | A stop, backup, or delete of this instance is still running. | Wait. |
| `The instance is already <state>.` | It has a live process. | Nothing to do; use **Restart** if you meant that. |
| `Port conflict: Port <n> is used by '<other>' (game port range <a>-<b>).` / `... (RCON port).` / `Port <n> is used by the web UI.` / `Port <n> is in use by an OS listener (UDP).` / `Port <n> is in use by an OS listener (TCP).` | The game port, game port + 1, or RCON port collides with another instance, the app's own listener, or something else on the box. The game does not report a failed bind, so the manager refuses instead. | Change the ports on the **Settings** tab, or stop whatever holds them. |
| `ServerAdminPassword under [ServerSettings] in GameUserSettings.ini is empty. Start is refused until it is set; RCON is the only way the manager saves and stops the server.` | No admin password in the source INI. | Set it in the INI editor (the cluster's for a member) ([configuration-files.md](configuration-files.md)). |
| `Instance '<name>' has no map.` / `Choose a map.` | The map row was deleted. | Maps in use cannot be deleted through the UI; if the database was edited by hand, re-create the map ([maps.md](maps.md)). |
| `Mod id <n> is not a valid CurseForge project id.` | A non-positive mod id on a list or a map row. | Fix it on the Mods or Maps page ([mods.md](mods.md)). |
| `Process.Start failed: <error>` | `Instances\<slug>\ShooterGame\Binaries\Win64\ArkAscendedServer.exe` is missing or unreadable through the junction. | Check that `DataRoot\Server` holds the install and run **Update game** with **Verify game files**. |
| `The server started (pid <n>) but its identity could not be saved after 3 retries: ... Use 'Retry persist'.` | The database write failed; the server is up. | Press **Retry persist** on the instance page. |
| Console: `Firewall: <error> The server starts anyway; open UDP <p>-<p+1> manually if players cannot join.` | The Windows Firewall API refused the rule. | Add the inbound UDP rule by hand ([exposing-servers.md](../exposing-servers.md)). |

**Starts, then exits.** The console shows `Server exited unexpectedly (code <n>).` and the row
returns to **Stopped**. The cause is in `ShooterGame.log` above that line: a mod that failed to
download, a map key the server does not know, an INI value it rejects. Read the console tail, or
the file at `DataRoot\Instances\<slug>\ShooterGame\Saved\Logs\ShooterGame.log`.

If the instance has **Restart automatically after an unexpected exit** on, the manager relaunches it
up to three times in a row, and the console says so. When all three end within 10 minutes the row
shows **Crashed** with a restart count and stays that way; fix the cause from the log, then press **Start**.

**Starting, unconfirmed** ("Alive for over 10 minutes without answering RCON."): the process runs
but RCON never answered. The password in the generated `GameUserSettings.ini` or the RCON port is
wrong, or the map is still loading on a slow disk. Check the console for the startup markers
([console-and-rcon.md](console-and-rcon.md)).

## An instance will not stop

| You see | Cause | Fix |
|---|---|---|
| Row stays **Stopping** for a minute or more | The pre-stop countdown is broadcasting one line per minute for **Countdown before a stop, minutes**. | Press **Stop now** to skip it, or set the countdown to 0 on Settings. |
| Console: `RCON 'doexit' failed (<Failure>): <message> Continuing with the next step.` then `doexit was not acknowledged; killing pid <pid> now instead of waiting <n> s.` | RCON could not deliver `doexit`, so the process is killed at once rather than after the graceful timeout. A server that never answered the command never began a save either. Usual when the instance was still **Starting** and its RCON port was not open yet. | Nothing, if you were stopping a server that had not finished loading. Otherwise fix the RCON credentials before the next start. |
| Console: `The server did not exit within <n> s; killing pid <pid>.` | The server took `doexit` but was still running when the graceful timeout ran out. Whatever it had not written by then is lost. | Raise **Graceful stop timeout, seconds** on Settings if the world is large and the kill keeps arriving first. |
| Console: `No RCON credentials for this process; skipping doexit and waiting for the graceful timeout before killing.` | The instance was re-attached without a readable generated config. | Expect the kill after the timeout; restart the instance afterward so the config is regenerated. |
| `Pid <n> is still alive after kill; its exit could not be verified.` | Windows could not end the process. | End it in Task Manager (or `Stop-Process -Id <n> -Force`), then restart the service so reconciliation sees it gone. |
| `Kill failed: <error>` | The kill call itself threw (usually access denied). | Same. |
| `The instance is not running.` | There is no live process to stop; the row is stale. | Reload the page. |
| `The service is shutting down.` | The stop was interrupted by a service stop. | Start the service; the instance is re-attached if it survived. |

[instances.md](instances.md) has the full stop sequence.

## The service will not start

`Start-Service ArkAscendedServerAdmin` fails, or the browser cannot connect at all and `/healthz`
does not answer. The host failed before it could listen: a malformed
`appsettings.Production.json`, a port already bound, a `DataRoot` on a drive that is not there, a
certificate that cannot be loaded in `LanHttps` mode.

Read the last entries in the Application event log under the source
`ArkAscendedServerAdmin.Server`:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ArkAscendedServerAdmin.Server' } -MaxEvents 50 |
    Format-List TimeCreated, LevelDisplayName, Message
```

If the failure followed an upgrade, `install.ps1 -Rollback` puts the previous version and its
database copy back ([hosting.md](../hosting.md#recovery-after-a-failed-upgrade)). A service that
starts but answers nothing on its port is a bind problem; the `Kestrel:Endpoints` section of the
settings file names the URL it tried ([configuration.md](../configuration.md)).

## A backup was skipped

**Skipped** shows on the Backups tab or on the Instances row, with a reason.

| Reason | Cause | Fix |
|---|---|---|
| `instance not running (Stopped)` | A scheduled attempt found the instance stopped. | None; the schedule resumes when it runs. |
| `RCON unreachable (Unreachable)` / `(StartingUnconfirmed)` | The server is alive but RCON is not answering, so `saveworld` cannot be sent. | Fix `ServerAdminPassword` / `RCONPort`; restart the instance. |
| `RCON unreachable: the generated GameUserSettings.ini is missing` | The instance was never started by this service, or `Saved\Config\WindowsServer` was removed. | Restart the instance. |
| `saveworld failed (Timeout): ...` | The command timed out. | Raise **RCON command timeout, seconds**. |
| `world file missing: <path>` | The server has not saved yet, or the map key changed. | Wait for the first save (`saveworld` in the console forces one). |
| `world file in use after 3 attempts: ...` / `files changed during the snapshot twice (...)` | The game kept writing the world during three settle windows. | Raise **Settle window after saveworld, seconds**, or try when the server is quieter. |

**Failed** with `verification: ...` or an I/O error is a disk problem; the archive was deleted.
[backups.md](backups.md) has the whole sequence.

## An update stalled

| You see | Cause | Fix |
|---|---|---|
| The sidebar shows **Updating the game** and the SteamCMD console shows `Attempt <n> failed (...); retrying in <x> (attempt <n+1> of 5).` | Steam is throttling or unreachable. | Wait; five attempts span about eight minutes. |
| Toast "Update stopped: SteamCMD did not produce a verified install: SteamCMD failed after 5 attempt(s): ..." and the phase stays **Updating the game** | Every attempt failed. Nothing is launched against an unverified install, and **Start** answers `update in progress`. | Press **Resume update** (Update page or the Instances page banner) later. |
| Toast "Update stopped: Could not stop every instance with a verified exit (instance <n>: ...)." | A stop failed. The stopped instances stay stopped. | Fix the instance (see above), then start the update again. |
| Toast "Update stopped: ArkAscendedServer.exe is still running under the data root (<path> (PID <n>)); stop it before updating." | A server process the manager does not own runs from `DataRoot`. | End it, then **Resume update** or start again. |
| Banner **Update recovery incomplete.** with `Update recovery incomplete: instance <n>: <error>. Retry or skip each instance.` | A relaunch failed after SteamCMD finished. | Fix the start refusal, then **Retry**; or **Skip** and start by hand. |
| The sidebar shows a phase after a service restart | The persisted phase is being resumed. | Wait; the SteamCMD console shows "Resuming interrupted update from the <Phase> phase." |

[game-updates.md](game-updates.md) explains each phase.

## Where every log lives

| Log | Where | What is in it |
|---|---|---|
| Service log | Windows Application event log, source `ArkAscendedServerAdmin.Server` (the command above) | Readiness phases, `DataRoot`, the version, credential diagnostics, launches, stops, backups, updates, every warning and error. |
| Instance console | Instance page, **Console** tab | The live tail of `ShooterGame.log` with the manager's own lines (launch, RCON, firewall, backup, exit) interleaved. In memory only, bounded; a service restart backfills the last **Console history on re-attach, lines**. |
| `ShooterGame.log` | `DataRoot\Instances\<slug>\ShooterGame\Saved\Logs\ShooterGame.log` | The game's own log; the console's source. |
| SteamCMD console | `/setup` during the first install, the Update page afterward | The raw SteamCMD output of the current run, in memory. |
| Generated config | `DataRoot\Instances\<slug>\ShooterGame\Saved\Config\WindowsServer\*.ini` and `*.ini.bak` | What the server was actually started with, and the previous one. |
| Backup records | Instance page, **Backups** tab | Every attempt with its outcome and reason. |
| Installer journal | `DataRoot\keys\install-pending.json` while an install or upgrade is unfinished | Which step the installer reached; `install.ps1 -Verify` or `-Rollback` clears it ([hosting.md](../hosting.md#upgrading)). |

A bug report wants the event-log excerpt, the instance console around the failure, and the
version from the bottom of the sidebar.
