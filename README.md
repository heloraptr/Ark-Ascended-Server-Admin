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
```

The Windows-targeted projects (`Infrastructure`, `Server`, `Infrastructure.IntegrationTests`) build
and run on Windows; on Linux/WSL they compile with `-p:EnableWindowsTargeting=true` but cannot run.
