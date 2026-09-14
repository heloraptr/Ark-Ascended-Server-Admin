# Hosting

The only deployment that is tested is the one `install.ps1` makes: Kestrel as a Windows service. IIS
is covered [at the end](#iis), as untested.

Paths assume the installer defaults: the app in `C:\ArkAscendedServerAdmin\App` (`InstallDir`),
data in `C:\ArkAscendedServerAdmin` (`DataRoot`). Every `install.ps1` command below runs from an
extracted release folder in an elevated PowerShell (Windows PowerShell 5.1 or PowerShell 7, both work).

## The service

| | |
|---|---|
| Service name | `ArkAscendedServerAdmin` |
| Account | LocalSystem. Firewall rules, WMI, and junctions need no separate elevation; the game servers and SteamCMD run under the same account. |
| Start | Automatic. On a crash the service control manager restarts it twice, five seconds apart, then leaves it stopped; a day without failures resets the count. |
| Executable | `InstallDir\ArkAscendedServerAdmin.Server.exe` |
| Settings | `InstallDir\appsettings.Production.json`, see [configuration.md](configuration.md). |
| Marker | `InstallDir\install.json`: install id, service name, `DataRoot`, version, install time. The installer refuses to touch a folder without it (except to adopt an install made by hand, when the service already points into that folder). |

A service restart does not stop the game servers. The service finds them again by their executable
path and process id and re-attaches the console tails and player tracking.

## Bind modes

`install.ps1 -Bind` picks one of three; on an upgrade the mode and port are read from the existing
settings file and never re-prompted.

| Mode | Listens on | Reachable from | Firewall rule | Use when |
|---|---|---|---|---|
| `Loopback` (default) | `http://127.0.0.1:5000` | This machine only | None | A reverse proxy on the same box terminates HTTPS. The only mode that never opens a port. |
| `LanHttps` | `https://0.0.0.0:5001`, plus `http://127.0.0.1:5000` | Your LAN | TCP 5001 from `-LanSource` (`LocalSubnet` by default) | No proxy; you accept a self-signed certificate warning. |
| `Proxy` | `http://0.0.0.0:5000` | The proxies you name | TCP 5000 from `-KnownProxies` only | The reverse proxy is another machine (or a container with its own address). |

The port is `-Port`; the defaults above follow the mode. Whatever the mode, the app refuses any
request whose scheme is not HTTPS after forwarded-header processing with **403**. That is the HTTPS
guard, and it is why a `Loopback` install answers 403 when you open `http://127.0.0.1:5000/` in a
browser: nothing has said "this was HTTPS". The exception is `GET /healthz` (below).

### Loopback

The installer prints, in place of a URL:

> Point your HTTPS reverse proxy at http://127.0.0.1:5000, forward `X-Forwarded-Proto` and enable
> WebSockets, then open the proxy's URL. A direct http:// request answers 403 by design.

Three things the proxy has to do:

1. Forward to `http://127.0.0.1:5000`.
2. Send `X-Forwarded-Proto: https` (and `X-Forwarded-For`, `X-Forwarded-Host`). Loopback is always
   a trusted proxy, so nothing needs to be added to `KnownProxies`.
3. Pass WebSocket upgrades through. Blazor Server runs every page over one WebSocket; without it the
   pages load and then nothing responds.

Examples for Nginx Proxy Manager and Caddy are [below](#reverse-proxy-examples).

### LanHttps

The installer creates a self-signed certificate (valid one year, with the machine name and every
non-loopback IPv4 address of the box in its subject alternative names), exports it to
`DataRoot\keys\web-<timestamp>.pfx` with a random password, removes it from the certificate store,
and points `Kestrel:Endpoints:Https` at the file. Open `https://<box-ip>:5001/` from a machine on
the LAN; the browser warns because nobody it trusts signed the certificate. Accept the warning or
replace the certificate (below). The cookie is marked secure and the HTTPS guard sees a real HTTPS
connection, so no forwarded headers are involved.

**The second listener.** The base `appsettings.json` declares an HTTP endpoint on
`127.0.0.1:5000`, and configuration merging cannot delete a key, so the production file sets it to
`http://127.0.0.1:<port - 1>` (5000 when the HTTPS port is 5001). That listener exists in `LanHttps`
mode. It is loopback only, guard-protected like everything else (a direct request answers 403), and
usable as a proxy target if you later put a reverse proxy on the box without reinstalling.

### Proxy

For a reverse proxy that is not on this machine: `-KnownProxies` takes the addresses (comma
separated) and is required. They go into `ArkAdmin:KnownProxies`, so their `X-Forwarded-*` headers
are trusted, and into the firewall rule, so nothing else on the network can reach port 5000. The
proxy still has to send `X-Forwarded-Proto: https` and pass WebSockets. Traffic between the proxy and
the box is plain HTTP; keep that leg on a network you trust.

A proxy running in Docker on the same box is this case too: from inside the container `127.0.0.1` is
the container, so it reaches the box by the host's LAN address or the Docker gateway, and that source
address is what goes in `-KnownProxies`.

## Reverse proxy examples

### Nginx Proxy Manager

Add a proxy host:

| Field | Value |
|---|---|
| Domain Names | your hostname |
| Scheme | `http` |
| Forward Hostname / IP | `127.0.0.1` (same box) or the box's LAN address (`Proxy` mode) |
| Forward Port | `5000` |
| Websockets (the switch on the Details tab) | on |
| Block Common Exploits | on |
| SSL tab | your certificate (Let's Encrypt works), Force SSL on, HTTP/2 on |

Nginx Proxy Manager's default proxy configuration already sends `X-Forwarded-Proto`, `X-Forwarded-For`,
and `X-Real-IP`; nothing custom is needed. The console and the SteamCMD output are long-lived
WebSocket sessions, so if you have shortened `proxy_read_timeout` in a custom location, raise it
back above the default 60 seconds or the connection drops during a quiet console.

### Caddy

```
ark.example.com {
    reverse_proxy 127.0.0.1:5000
}
```

Caddy obtains the certificate, sets `X-Forwarded-Proto` and `X-Forwarded-For`, and proxies WebSocket
upgrades without further configuration. In `Proxy` mode replace `127.0.0.1` with the box's address
and add Caddy's address to `-KnownProxies`.

### Plain nginx

```nginx
location / {
    proxy_pass         http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header   Upgrade $http_upgrade;
    proxy_set_header   Connection "upgrade";
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
    proxy_read_timeout 300s;
}
```

## Monitoring

`GET /healthz` answers `200` with the plain-text body `ArkAscendedServerAdmin ok`. It needs no
login, is exempt from the HTTPS guard and from the `/setup` redirect, and carries no version or
state. The installer probes it after every install, upgrade, and password change; an uptime monitor
can hit it on whichever listener it can reach:

```powershell
Invoke-WebRequest http://127.0.0.1:5000/healthz -UseBasicParsing
```

Anything more than "the process answers" is on the Instances page.

## The certificate (`LanHttps`)

### Re-issue the self-signed certificate

```powershell
.\install.ps1 -SetCertificate
```

Makes a new self-signed certificate with the same names (run it after the box's IP address or
machine name changes, or when the year is up), writes a new `keys\web-<timestamp>.pfx`, rewrites
the settings file, restarts the service, and probes the HTTPS port pinned to the new thumbprint. On
success the previous PFX is deleted; on failure the previous settings and certificate are put back,
the service restarted, and the new PFX removed. Browsers show the warning once more for the new
certificate.

### Use your own certificate

```powershell
.\install.ps1 -SetCertificate -PfxPath C:\path\to\cert.pfx -PfxPassword 'pfx password'
```

Same flow with your PFX copied into `keys\web-<timestamp>.pfx`. The certificate needs the name or
address you type into the browser in its subject alternative names. Kestrel does not reload a
changed certificate file on its own here, which is why the service is restarted.

### By hand

1. Write the new PFX to `DataRoot\keys\web-<timestamp>.pfx`. Use a new file name; do not overwrite
   the one the settings point at.
2. Elevated, edit `InstallDir\appsettings.Production.json`: set
   `Kestrel:Endpoints:Https:Certificate:Path` to the new file and `Certificate:Password` to its
   password.
3. `Restart-Service ArkAscendedServerAdmin`, then open the site and check the certificate the
   browser shows.
4. Delete the old PFX.

## The Windows event log

The service logs to the **Application** log under the source **`ArkAscendedServerAdmin.Server`**.
Readiness, `DataRoot`, launches, stops, backups, updates, and every warning and error are there;
the installer's settings file keeps the framework at `Warning` so request noise stays out. To read
the last 50 entries:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ArkAscendedServerAdmin.Server' } -MaxEvents 50 |
    Format-List TimeCreated, LevelDisplayName, Message
```

This is the excerpt a bug report asks for. When the installer's probe fails it prints these lines
itself.

## Upgrading

Extract the new release zip anywhere, then, elevated, from that folder:

```powershell
.\install.ps1
```

No prompts: the service exists, so `InstallDir` comes from the service's binary path (a different
`-InstallDir` is refused), and `DataRoot`, bind mode, and port come from the existing settings
file. With `<ts>` as a `yyyyMMdd-HHmmss` stamp, the upgrade:

1. Re-applies the folder permissions.
2. Copies the new files to `App.new`.
3. Stops the service and waits for it.
4. Copies the database file set (`ArkAscendedServerAdmin.db` and, if present, `-wal` and `-shm`)
   from `DataRoot\Data\` to `DataRoot\Backups\_app\<ts>-<old-version>\`. A raw copy is only
   consistent while the service is stopped, which is why this happens here.
5. Renames `App` to `App.previous-<ts>` and `App.new` to `App`, and copies
   `appsettings.Production.json` from the previous folder.
6. Starts the service, runs migrations (the app does that itself at start), and probes `/healthz`
   on the configured scheme and port, confirming the socket belongs to the service's process.
7. On success, deletes older `App.previous-*` folders and `_app` copies, keeping the newest of each.

So after a successful upgrade there is always one `App.previous-<ts>` next to `App` and one
`Backups\_app\<ts>-<version>` folder, until the next successful upgrade replaces them.

Every mutating run keeps a journal at `DataRoot\keys\install-pending.json` from its first step
until verification and cleanup are done. While it exists, every other `install.ps1` operation is
refused with the pending operation named.

`-NoStart` stops after step 5, leaves the service stopped and every recovery artifact in place, and
prints "verification pending". Start the service when you are ready and run `.\install.ps1 -Verify`
to probe and clean up.

## Recovery after a failed upgrade

If a step before "start" fails, the installer reverses the renames and restarts the service on the
old files by itself. If the service starts but the probe fails, the installer stops the service,
prints the last 50 event-log lines, and leaves the journal in place. From there:

```powershell
.\install.ps1 -Rollback
```

stops the service, undoes the recorded steps in reverse (rename `App.previous-<ts>` back, restore
the database file set from `Backups\_app\<ts>-...`), starts the service, probes the restored
configuration, and clears the journal. The same command recovers from a run that was interrupted
(window closed, machine rebooted) at any phase; `.\install.ps1 -Verify` is the other way out when
the service is in fact fine and only the probe and cleanup were missed.

**What a rollback loses.** The database is restored from the copy taken while the service was
stopped, because the new version may have run a migration the old version cannot read. Anything
changed in the UI between the upgrade and the rollback (settings, a new instance, backups recorded)
is gone from the database. Files on disk (worlds, backups, INI text) are not touched.

By hand, if the script cannot run:

1. `Stop-Service ArkAscendedServerAdmin`.
2. Rename `App` to something else and `App.previous-<ts>` back to `App`.
3. In `DataRoot\Data\`, delete `ArkAscendedServerAdmin.db`, `-wal`, and `-shm`, then copy the whole
   set from `Backups\_app\<ts>-<version>\`. Restore all three together (or the `.db` alone if the copy
   has no side files); a `.db` with someone else's `-wal` is corrupt.
4. Delete `DataRoot\keys\install-pending.json`.
5. `Start-Service ArkAscendedServerAdmin`.

## Changing the password

```powershell
.\install.ps1 -SetPassword
```

Prompts for the new password, hashes it, rewrites the settings file (removing any plaintext
`ArkAdmin:Password`), restarts the service, probes `/healthz`, and then logs in through the loopback
listener with the new password to prove it took. Only after that succeeds is the settings backup
deleted; on failure the old file is put back and the service restarted again.

The service restart signs every browser out; game servers are unaffected.

The command is refused when the service's `Environment` registry value or the machine environment
carries `ArkAdmin__Password` or `ArkAdmin__PasswordHash`, because those override the file and the
change would not take effect. Remove the variable (and restart the service) first, or change the
variable instead. `-SetPassword -NoStart` is refused too: the login check cannot be deferred.

## Uninstalling

From an extracted release folder, elevated:

```powershell
.\uninstall.ps1
```

Stops and deletes the service, removes the web firewall rule, and deletes `InstallDir` and every
`InstallDir.previous-*` that belongs to the same install. `DataRoot` stays, and the script says so:
the game install, instances, worlds, backups, the key ring, the database, and the certificate are
yours. Delete the folder yourself when you are done with them. Per-instance game-port firewall
rules are removed when an instance is deleted in the app, not by the uninstaller.

## IIS

Untested. The published folder carries a `web.config` because the project uses the web SDK, so the
app may start under IIS with the ASP.NET Core hosting bundle. Expect it to break, for reasons that
are about what the app is, not about configuration:

- The app pool identity cannot create firewall rules, so no game server gets its inbound UDP rule
  at start. The service runs as LocalSystem for exactly this.
- IIS idles the worker process out and recycles it. Every recycle stops the backup scheduler, the
  console log tails, and player tracking until the next request wakes the app, and a backup that
  was running is cut off.
- An overlapping recycle runs two copies of the process manager against the same instances for a
  while. Two managers, one set of game servers, one database.
- Whether a game server launched from the IIS worker process survives a recycle of that process is
  unknown.
- Path handling, `DataRoot` permissions, and the event-log source all assume the service account.

If you try it anyway, do it on a box you can wipe, and do not report the breakage as a bug unless
it also happens under the service.
