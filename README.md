# Ark Ascended Server Admin

Self-hosted manager for **ARK: Survival Ascended** dedicated servers on Windows. It runs as a
Windows service on the game box and is operated from a browser, so you never have to remote-desktop
in to start, stop, update, or back up your servers.

Built for one owner running a handful of servers, clustered or standalone, on one machine.

## What it does

- **One game install, many servers.** SteamCMD installs the game once under `DataRoot\Server`;
  every instance runs the same binaries through its own NTFS junction tree with a private `Saved`
  folder, so updates happen once and worlds never mix.
- **Instances and clusters.** A wizard creates an instance (name, cluster, map, INI starting point,
  mods, launch options, ports) and can start it straight away. Clusters share INI files, mods, base
  launch options, an admin whitelist, and a transfer directory.
- **Configuration you can read.** `Game.ini` and `GameUserSettings.ini` are edited as plain text
  and kept as the source of truth on disk, mirrored into the database. Per-instance overrides sit
  on top. The manager fills in only what it owns (session name, ports, RCON, player cap) and warns
  when your text contradicts it.
- **Lifecycle with verification.** Start goes through a stagger queue and a port check against live
  OS listeners; stop broadcasts a countdown, asks the server to save and exit over RCON, and
  verifies the exit. A restarted service re-attaches to servers that kept running.
- **Console.** A live tail of each server's `ShooterGame.log` with startup markers, plus an RCON
  input.
- **Backups that are checked.** `saveworld`, wait for the files to settle, snapshot, zip, verify
  every entry, then prune to the retention count. Skipped or failed attempts are recorded, never
  silent.
- **Game updates.** Stop everything with verified exits, run SteamCMD, verify the manifest, relaunch
  through the queue, and persist every step so an interrupted update resumes after a service
  restart.
- **Mods** from CurseForge (search with an API key, or add by id), **known players** (on-demand
  `ListPlayers` to find EOS ids for the whitelist), custom **maps**, and a **config export** of the
  database.

## Documentation

- [What happens when you create an instance](docs/instance-creation.md): every step behind the
  wizard's **Create instance** button and the **Start the server right away** option.

## Quick start (the game box)

Requirements: Windows 10/11 or Server, the .NET 10 runtime (the SDK includes it), and about 15 GB
of disk for the game install.

1. Publish the `Server` project and register it as a service under LocalSystem. An elevated
   PowerShell:

   ```powershell
   dotnet publish src/ArkAscendedServerAdmin.Server -c Release -o C:\Ark\App
   New-Service -Name ArkAscendedServerAdmin -BinaryPathName '"C:\Ark\App\ArkAscendedServerAdmin.Server.exe"' -DisplayName "Ark Ascended Server Admin" -StartupType Automatic
   ```

2. Set the host settings (see [Configuration](#configuration)); at minimum `ArkAdmin:DataRoot`
   and `ArkAdmin:Password`. Edit the published `C:\Ark\App\appsettings.json`, or create
   `appsettings.Production.json` next to it, or set environment variables on the service.
3. `Start-Service ArkAscendedServerAdmin` and open the URL. Every page shows `/setup` with the
   SteamCMD console until the game install under `DataRoot\Server` is downloaded and verified
   (12 GB, roughly 20 minutes on a fast line). Then log in and create the first instance.

Redeploying a new version is: publish again, stop the service, replace the files under `C:\Ark\App`
except `appsettings.Production.json`, start the service. Migrations run automatically at start.

## Configuration

Two kinds of settings, deliberately kept apart.

**Host settings** live in `appsettings.json` (or `appsettings.Production.json` next to it), are read
at startup, and need a service restart to change:

| Key | Purpose | Default |
|---|---|---|
| `ArkAdmin:DataRoot` | Root of all managed data: game install, instances, clusters, backups, archive, database, keys, exports. Relative paths resolve against the app folder; `%VAR%` is expanded. | `%ProgramData%\ArkAscendedServerAdmin` |
| `ArkAdmin:Password` | The single login password. Empty refuses every login. | *(empty)* |
| `ArkAdmin:KnownProxies` | IP addresses of reverse proxies whose `X-Forwarded-*` headers are trusted. Loopback is always trusted. | `[]` |
| `ArkAdmin:AllowInsecureHttp` | Accept plain-HTTP requests. Meant for development; otherwise any request that is not HTTPS after forwarded-header processing gets 403. | `false` |
| `Kestrel:Endpoints:Http:Url` | Bind address. Keep it on loopback and let a reverse proxy terminate HTTPS. | `http://127.0.0.1:5000` |

**Where the password goes.** Any of the standard ASP.NET Core configuration sources works; pick
whichever fits how you install:

- Edit the published `appsettings.json` directly. Simplest for a one-off install. A later publish
  into the same folder overwrites it, so keep a copy of your values.
- Create `appsettings.Production.json` next to it. It overrides `appsettings.json` and survives a
  redeploy that replaces only the published files.
- Set environment variables on the service, `ArkAdmin__Password` and `ArkAdmin__DataRoot`
  (double underscore for the section separator), through the service's `Environment` registry
  value or an installer. Nothing on disk then holds the password.

Whichever file holds the password is readable by every local account unless you tighten it. On a
box only you use that is fine. To restrict it to the service and elevated administrators:

```powershell
icacls C:\Ark\App\appsettings.Production.json /inheritance:r /grant:r SYSTEM:R Administrators:F
```

After that the file opens only from an elevated editor, because an unelevated administrator runs
with a filtered token.

**App Settings** live in the database and are edited on the Settings page at runtime: stagger
delay, SteamCMD `validate`, port start and step for suggestions, default backup interval and
retention, backup quiescence, pre-stop broadcast minutes, graceful stop timeout, RCON timeout,
console backfill lines, CurseForge API key (stored in plain text).

### Security model

Kestrel binds to loopback; the owner's reverse proxy (Nginx Proxy Manager or similar) terminates
HTTPS and forwards to it. The app additionally refuses any request whose effective scheme is not
HTTPS, so a LAN rebind without the proxy fails closed. Authentication is one password with a
12-hour sliding cookie; the password hash is validated on every request and every Blazor circuit is
revalidated every five minutes, and every command re-checks the session server-side before doing
anything. The service runs as LocalSystem so firewall rules, WMI, and junctions need no separate
elevation; RCON never leaves loopback.

### Data layout

```
DataRoot\
  Server\            the game install (SteamCMD target); Server\steamapps\appmanifest_2430930.acf proves it
  SteamCMD\          steamcmd.exe, downloaded on first run
  Instances\<slug>\  per-instance junction tree, source INI (standalone), private ShooterGame\Saved
  Clusters\<slug>\   cluster source INI and the transfer directory
  Backups\<slug>\    verified world backups
  Archive\           retained worlds of deleted instances (slug stays reserved while present)
  Exports\           "export config backup" copies of the database
  keys\              Data Protection key ring for the auth cookie
  ArkAscendedServerAdmin.db
```

## Web UI

Every screen lives in the `Components` Razor Class Library and talks only to `Core` interfaces:
the scoped command facades (`IInstanceCommands`, `IClusterCommands`, `IConfigCommands`,
`IModCommands`, `IPlayerCommands`, `IMapCommands`, `ISettingsCommands`, `IMaintenanceCommands`)
re-check the session on every call, and live state (instance runtimes, console lines, readiness,
maintenance) is observed through the singleton services' events.

| Page | What it does |
|---|---|
| Instances (`/`) | Rows grouped by cluster, standalone last; start/stop/restart/back up per row, selected, or per cluster; game update; update-recovery banner with retry/skip. |
| Instance (`/instances/{id}`) | Console with RCON input, INI editors (source files) and overrides, mods, launch options with a command-line preview, settings, backups. |
| New instance (`/instances/new`) | Wizard: name, cluster, map, INI starting point and admin password, mods, launch options, ports, summary with "start right away". |
| Clusters | Shared INI files, cluster mods, base launch options, cluster id and whitelist. |
| Mods | CurseForge search with an API key, manual ids without one, usage per cluster and instance. |
| Players | On-demand `ListPlayers` across running instances; pick an EOS id into a whitelist. |
| Maps, Settings, Setup | Map list, App Settings plus read-only host values and config export, install console. |

The visual system (fonts, tokens, the horizon rule, the state lamp) is in
`src/ArkAscendedServerAdmin.Components/wwwroot/css/ark.css`; Radzen's `standard-dark` theme is
re-tokened rather than restyled.

## Repository layout

| Project | Role |
|---|---|
| `src/ArkAscendedServerAdmin.Core` | Domain models, INI and launch-argument logic, port allocation, the update state machine, CurseForge client, service interfaces. No Windows dependencies. |
| `src/ArkAscendedServerAdmin.Infrastructure` | EF Core + SQLite, SteamCMD, process management and reconciliation, RCON, junction layout, firewall, backups, update flow. Windows only. |
| `src/ArkAscendedServerAdmin.Components` | Razor Class Library with every screen (Radzen). |
| `src/ArkAscendedServerAdmin.Server` | Blazor Server host, Windows service, the command facades. |
| `test/ArkAscendedServerAdmin.UnitTests` | Pure unit tests over `Core`. |
| `test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests` | `Infrastructure` and command-facade tests on a real temp filesystem and SQLite database. Windows only. |

## Building and testing

Requires the .NET 10 SDK (see `global.json`). Package versions are managed centrally in
`Directory.Packages.props`; versions come from git tags via MinVer.

```
dotnet build ArkAscendedServerAdmin.slnx
dotnet run --project test/ArkAscendedServerAdmin.UnitTests/ArkAscendedServerAdmin.UnitTests.csproj --no-build
dotnet run --project test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests/ArkAscendedServerAdmin.Infrastructure.IntegrationTests.csproj --no-build
```

The test projects are Microsoft.Testing.Platform executables, so `dotnet run` (or the built `.exe`)
runs them directly. With SDK 10.0.400 and xunit.v3 4.0.0, `dotnet test --project ...` reports
"Zero tests ran" for both projects; use `dotnet run` until that combination is sorted out. The
executables accept `--filter-namespace <namespace>` to run one folder.

The Windows-targeted projects (`Infrastructure`, `Server`, `Infrastructure.IntegrationTests`) build
and run on Windows; on Linux/WSL they compile with `-p:EnableWindowsTargeting=true` but cannot run.

### Running from the repo

`appsettings.Development.json` points `DataRoot` at `./data` in the repo (git-ignored), sets the
password to `dev`, and allows plain HTTP, so
`dotnet run --project src/ArkAscendedServerAdmin.Server` works without a proxy.

The readiness pipeline only needs `DataRoot\Server\steamapps\appmanifest_2430930.acf` with
`StateFlags 4`, the game tree under `Server`, and `DataRoot\SteamCMD\steamcmd.exe`. With an existing
game install elsewhere, junction `Server\Engine` and `Server\ShooterGame` at it, copy the manifest
and `steamcmd.exe`, and `dotnet run` reaches Ready in a few seconds instead of downloading 12 GB.

### Database migrations

The `AppDbContext` and its migrations live in `Infrastructure`; `dotnet ef` is a local tool
(`.config/dotnet-tools.json`, run `dotnet tool restore` once):

```
dotnet ef migrations add <Name> --project src/ArkAscendedServerAdmin.Infrastructure --startup-project src/ArkAscendedServerAdmin.Server --output-dir Data/Migrations
```

Migrations are applied automatically at service start. "Export config backup" on the Settings page
writes a consistent copy with SQLite's online backup API; a raw copy of the `.db` file is only safe
with the service stopped.

## Notes on ASA specifics

- `-port=` is mandatory; the game ignores `Port` in the INI. `RCONPort`, `RCONEnabled`, and
  `ServerAdminPassword` are honored from the INI.
- The player cap is `-WinLiveMaxPlayers`; ASA ignores the INI `MaxPlayers` and resets it. The
  manager writes both from the one *Max players* field.
- ASA does not use the Steam query port. Each server still opens UDP 27015 as a vestigial Steam
  socket and several instances can share it; only the game port (UDP, plus port + 1) needs a
  firewall rule, and RCON stays on loopback.
- `doexit` exits with code -1 every time; exit codes are never treated as crash signals.
- The game rewrites `GameUserSettings.ini` at launch and exit, which is why generated files are
  never read back as source.
