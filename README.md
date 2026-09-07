# Ark Ascended Server Admin

Self-hosted manager for **ARK: Survival Ascended** dedicated servers on Windows. Runs as a Windows
service on the game box and is operated from a browser, so you never have to remote-desktop in.

## Layout

| Project | Role |
|---|---|
| `src/ArkAscendedServerAdmin.Core` | Domain models, INI/launch-argument logic, CurseForge client, service interfaces. No Windows dependencies. |
| `src/ArkAscendedServerAdmin.Infrastructure` | EF Core + SQLite, SteamCMD, process management, RCON, junction layout, firewall, backups. Windows only. |
| `src/ArkAscendedServerAdmin.Components` | Razor Class Library with every screen (Radzen). Shared by all hosts. |
| `src/ArkAscendedServerAdmin.Server` | Blazor Server host / Windows service. |
| `test/ArkAscendedServerAdmin.UnitTests` | Pure unit tests over `Core`. |
| `test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests` | `Infrastructure` tests on a real filesystem. Windows only. |

## Building

Requires the .NET 10 SDK (see `global.json`). Package versions are managed centrally in
`Directory.Packages.props`; versions come from git tags via MinVer.

```
dotnet build ArkAscendedServerAdmin.slnx
dotnet test --project test/ArkAscendedServerAdmin.UnitTests/ArkAscendedServerAdmin.UnitTests.csproj
dotnet test --project test/ArkAscendedServerAdmin.Infrastructure.IntegrationTests/ArkAscendedServerAdmin.Infrastructure.IntegrationTests.csproj
```

The Windows-targeted projects (`Infrastructure`, `Server`, `Infrastructure.IntegrationTests`) build
and run on Windows; on Linux/WSL they compile with `-p:EnableWindowsTargeting=true` but cannot run.

## Configuration

Two kinds of settings, deliberately kept apart:

- **`appsettings.json`** (host settings, read at startup, change needs a service restart):

  | Key | Purpose | Default |
  |---|---|---|
  | `ArkAdmin:DataRoot` | Root of all managed data (install, instances, backups, database, keys). Relative paths resolve against the content root; `%VAR%` is expanded. | `%ProgramData%\ArkAscendedServerAdmin` |
  | `ArkAdmin:Password` | The single login password. Empty refuses every login. | *(empty)* |
  | `ArkAdmin:KnownProxies` | IP addresses of reverse proxies whose `X-Forwarded-*` headers are trusted. Loopback is always trusted. | `[]` |
  | `ArkAdmin:AllowInsecureHttp` | Development only: accept plain-HTTP requests. Otherwise any non-HTTPS request gets 403. | `false` |
  | `Kestrel:Endpoints:Http:Url` | Bind address. Keep it on loopback; the reverse proxy terminates HTTPS. | `http://127.0.0.1:5000` |

- **App Settings** (database, edited from the web UI at runtime): stagger delay, SteamCMD `validate`,
  port start/step, backup interval/retention, RCON and stop timeouts, console backfill, CurseForge API
  key (plain text).

`appsettings.Development.json` points `DataRoot` at `./data` in the repo (git-ignored), sets the
password to `dev`, and allows plain HTTP so `dotnet run` works without a proxy.

## Running as a Windows service

Publish, then register the service under LocalSystem (an elevated PowerShell):

```powershell
dotnet publish src/ArkAscendedServerAdmin.Server -c Release -o C:\ArkAscendedServerAdmin
# edit C:\ArkAscendedServerAdmin\appsettings.json: set ArkAdmin:Password (and KnownProxies if the proxy is not on this box)
New-Service -Name ArkAscendedServerAdmin -BinaryPathName "C:\ArkAscendedServerAdmin\ArkAscendedServerAdmin.Server.exe" -DisplayName "Ark Ascended Server Admin" -StartupType Automatic
Start-Service ArkAscendedServerAdmin
```

Point the reverse proxy (Nginx Proxy Manager) at `http://127.0.0.1:5000` with the usual
`X-Forwarded-For` / `X-Forwarded-Proto` headers. Until the game install under `DataRoot\Server` is
verified, every page redirects to `/setup`.

## Database migrations

The `AppDbContext` and its migrations live in `Infrastructure`; `dotnet ef` is a local tool
(`.config/dotnet-tools.json`, run `dotnet tool restore` once):

```
dotnet ef migrations add <Name> --project src/ArkAscendedServerAdmin.Infrastructure --startup-project src/ArkAscendedServerAdmin.Server --output-dir Data/Migrations
```

Migrations are applied automatically at service start. "Export config backup" on the Settings page
writes a consistent copy with SQLite's online backup API; a raw copy of the `.db` file is only safe
with the service stopped.
