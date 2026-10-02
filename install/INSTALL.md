# Installing Ark Ascended Server Admin

Windows 10/11 or Windows Server, 64-bit. The app runs as a Windows service under LocalSystem and
manages ARK: Survival Ascended dedicated servers from a web UI.

## 1. Unblock the zip, then extract it

Windows marks downloaded files as "from the internet" and refuses to run their scripts. Before
extracting: right-click the zip, Properties, tick **Unblock**, OK (or `Unblock-File <zip>` in
PowerShell). Then right-click, Extract All. If you skip this, `install.ps1` fails with the standard
"not digitally signed" execution-policy message.

Two zips exist. `...-win-x64.zip` is self-contained and needs nothing else on the machine.
`...-win-x64-fdd.zip` is smaller and needs the ASP.NET Core 10 runtime (x64) from
<https://dotnet.microsoft.com/download/dotnet/10.0>; the installer checks for it first.

## 2. Run install.ps1 elevated

Open **Windows PowerShell as administrator** (Start, type `powershell`, Run as administrator; pwsh 7
works too), `cd` into the extracted folder, and run:

```powershell
.\install.ps1
```

It asks for:

- **Install folder** for the app (default `C:\ArkAscendedServerAdmin\App`).
- **Data folder** (default `C:\ArkAscendedServerAdmin`): the 12 GB game install, every instance, the
  backups, the database, and the keys land here. New or empty folders only.
- **Bind mode** (below), the port, and, depending on the mode, the proxy addresses or the LAN source.
- **Login password** for the web UI, typed twice, never shown. It is stored as a PBKDF2 hash.

The installer sets permissions on both folders (SYSTEM and Administrators full control, Users read),
then closes `keys`, `Data`, `Exports`, `Backups\_app`, `Instances` and `Clusters` inside the data folder
to SYSTEM and Administrators only, since they hold the key ring, the database, the INI files with the admin
and RCON passwords, and the RCON history. It also writes `appsettings.Production.json` readable by
SYSTEM and Administrators only, registers the service with automatic start and restart on failure, starts it, and checks `/healthz`.

Every value can be passed instead: `-InstallDir`, `-DataRoot`, `-Password`, `-Bind`, `-Port`,
`-KnownProxies`, `-LanSource`. `-Quiet` never prompts and fails on a missing value.

## Bind modes

**Loopback** (default). The app listens on `http://127.0.0.1:5000` only. Nothing is opened on the
network; put an HTTPS reverse proxy on the same machine in front of it (Nginx Proxy Manager, Caddy,
IIS as a proxy), forward `X-Forwarded-Proto`, enable WebSockets, and open the proxy's URL. A direct
`http://127.0.0.1:5000/` request answers 403 by design; only `/healthz` answers over plain HTTP.

**LanHttps** (`-Bind LanHttps`). The app listens on `https://0.0.0.0:5001` with a self-signed
certificate for the machine name and its LAN addresses, and a firewall rule admits TCP 5001 from the
local subnet (`-LanSource` changes that). The browser shows a certificate warning once; accept it, or
replace the certificate with `-SetCertificate`. A loopback listener on port 5000 also exists for a
local reverse proxy.

**Proxy** (`-Bind Proxy -KnownProxies <ip>[,<ip>]`). Plain HTTP on `0.0.0.0:5000` for a reverse proxy
on another machine. The firewall rule admits TCP 5000 from the listed addresses only, and only their
`X-Forwarded-*` headers are trusted. Never expose this port to the internet.

## Upgrading

Extract the new zip, run `.\install.ps1` from it. Nothing is asked: the installer finds the service,
copies the new files next to the old folder, stops the service, copies the database to
`DataRoot\Backups\_app\<timestamp>-<old version>\`, swaps the folders, copies the settings file over,
starts the service, and checks `/healthz`. The old folder stays as `App.previous-<timestamp>` until the
next successful upgrade. If the check fails the service is stopped and the recovery steps are printed;
`.\install.ps1 -Rollback` performs them.

## Changing the password

```powershell
.\install.ps1 -SetPassword
```

Prompts for the new password, rewrites the settings file, restarts the manager service (game servers
keep running), and verifies the change with a login. If the login fails the previous file is restored.

## Replacing the certificate (LanHttps)

```powershell
.\install.ps1 -SetCertificate                                   # new self-signed certificate
.\install.ps1 -SetCertificate -PfxPath C:\path\my.pfx -PfxPassword '...'   # your own
```

The new PFX is written to `DataRoot\keys\web-<timestamp>.pfx`, the settings point at it, the service
restarts, and the health check must answer with the new certificate before the old PFX is deleted.

## -NoStart, -Verify, -Rollback

`-NoStart` on an install or upgrade leaves the service stopped with every recovery copy in place and
records the pending operation in `DataRoot\keys\install-pending.json`. Start the service when ready
and run `.\install.ps1 -Verify` to run the check and clean up. While an operation is pending every
other install command is refused; `.\install.ps1 -Rollback` undoes the recorded steps (previous folder
back, database file set restored, settings file restored) and starts the previous version.

## Uninstalling

```powershell
.\uninstall.ps1
```

Stops the service, removes the web firewall rule and this installation's game-port firewall rules
(found by the tag the app records in `firewall.tag` in the app folder; without a valid file they are
left in place with a warning), deletes the service, and deletes the app folder and its `.previous-*`
copies. The data folder is left in place: the game install, instances, backups, keys,
database, and certificate are yours.

Because the installer replaces the permissions on the data folder, a later `install.ps1` only accepts a
data folder that is new, empty, or the one an installed service already uses. To install again into the
folder an uninstall left behind, move or empty it first.

## First start

Every page shows `/setup` until the 12 GB game install under `DataRoot\Server` finishes; the SteamCMD
console is on that page. The service logs to the Windows Application event log.

## Testing parameter

`-ServiceName <name>` on both scripts installs under a different service name (and firewall rule name)
so the installer can be exercised next to a live installation; leave it at the default otherwise.

`-SimulateFirewallFailure` on `uninstall.ps1` fails the firewall step on purpose, after the service is
stopped and before it is deleted, so a rerun of the uninstall can be tested on a scratch install.
