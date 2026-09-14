# Security

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository:

https://github.com/heloraptr/Ark-Ascended-Server-Admin/security/advisories/new

Do not open a public issue for a vulnerability. A report is read when I see it; there is no response
time I can promise. If the report is valid, the fix ships in the next release and the advisory is
published with credit to you unless you ask otherwise.

## What counts

Anything that lets someone who does not know the password act on the app or the game servers, or read
what they should not be able to read. For example:

- Logging in, staying logged in, or invoking a command without the password, or after the password
  changed.
- Reaching the app over plain HTTP in a configuration where the HTTPS guard should refuse it.
- Reading the settings file, the Data Protection key ring, or the database from an account that is
  not SYSTEM or an elevated administrator, after `install.ps1` has applied its permissions.
- RCON reachable from anywhere but the local machine.
- Path handling in the installer or the app that writes outside `DataRoot` and `InstallDir`.

## What does not count

- Bugs in ARK: Survival Ascended, SteamCMD, or the Windows firewall themselves.
- A game server whose UDP port is reachable from the internet. That is what a public server is; see
  [docs/exposing-servers.md](docs/exposing-servers.md) for the tradeoffs.
- The CurseForge API key being stored in plain text in the database. This is a documented decision for
  1.0.0 (the database is readable only by SYSTEM and administrators after install).
- Anything that requires an administrator account on the box. An administrator can already do
  everything the app can.

## Versions

Only the latest release receives fixes. There are no backports.
