# Configuration

Settings come in two kinds, and they are kept apart.

Host settings are read once at startup from `appsettings.Production.json` (written by `install.ps1`)
or from environment variables on the service, and changing one needs a service restart. Most of this
page is about them.

App Settings live in the database and are edited on the Settings page while the service runs; they
are listed [at the end](#app-settings-in-the-database).

Paths below assume the installer defaults: the app in `C:\ArkAscendedServerAdmin\App`, data in
`C:\ArkAscendedServerAdmin`.

## Host settings

| Key | Purpose | Default |
|---|---|---|
| `ArkAdmin:DataRoot` | Root of all managed data: game install, instances, clusters, backups, archive, database, keys, exports. Relative paths resolve against the app folder; `%VAR%` is expanded. | `%ProgramData%\ArkAscendedServerAdmin` when empty. The installer sets `C:\ArkAscendedServerAdmin`. |
| `ArkAdmin:Password` | The login password in plain text. For development, or for anyone who prefers it. | *(empty)* |
| `ArkAdmin:PasswordHash` | The login password as a PBKDF2 string, `pbkdf2$<iterations>$<salt-base64>$<hash-base64>`. The installer writes this. When both keys are set, the hash wins and a warning is logged. A present but malformed hash refuses every login and logs an error; there is no fallback to `Password`. | *(empty)* |
| `ArkAdmin:KnownProxies` | IP addresses of reverse proxies whose `X-Forwarded-*` headers are trusted. Loopback is always trusted. | `[]` |
| `ArkAdmin:AllowInsecureHttp` | Accept plain-HTTP requests. Development only; the installer never sets it. Otherwise any request that is not HTTPS after forwarded-header processing gets 403. | `false` |
| `ArkAdmin:SteamCmdLiveOutput` | Run SteamCMD under a pseudo console so the SteamCMD console shows each line as it happens. Set it to `false` to go back to redirected output, where every line appears when SteamCMD exits; only worth doing if live output misbehaves on a host. The service also falls back to redirected output by itself, with a warning line in the console, when it cannot start SteamCMD that way. | `true` |
| `Kestrel:Endpoints:Http:Url` | The plain-HTTP listener. Keep it on loopback and let a reverse proxy terminate HTTPS. `Loopback` and `Proxy` bind modes set this. | `http://127.0.0.1:5000` |
| `Kestrel:Endpoints:Https:Url` | The HTTPS listener used by the `LanHttps` bind mode. | *(none)* |
| `Kestrel:Endpoints:Https:Certificate:Path` | PFX file for the HTTPS listener. The installer writes `<DataRoot>\keys\web-<timestamp>.pfx`. | *(none)* |
| `Kestrel:Endpoints:Https:Certificate:Password` | Password of that PFX. Random when the installer generates it. | *(none)* |
| `Logging:LogLevel:*` | Log filters for the console (when run interactively). | `Information`; framework at `Warning` |
| `Logging:EventLog:LogLevel:*` | Log filters for the Windows event log. The provider defaults to `Warning`, which hides readiness, launches, and stops; the installer sets `Default` `Warning` and `ArkAscendedServerAdmin` `Information`. | `Warning` |

Neither password key is required in the file if it comes from the environment (below). With
neither set anywhere, the service starts, logs an error, and refuses every login.

The base `appsettings.json` in the app folder declares `Kestrel:Endpoints:Http:Url` as
`http://127.0.0.1:5000`. Configuration merging can override a key but not delete it, so a production
file that adds an HTTPS endpoint still has the loopback HTTP listener; the installer sets it to
`http://127.0.0.1:<port - 1>` in `LanHttps` mode so the two do not collide. See
[hosting.md](hosting.md#lanhttps) for what that listener is good for.

## What the installer writes

`install.ps1` writes `appsettings.Production.json` next to the executable on the first install and
rewrites it for `-SetPassword` and `-SetCertificate`. An upgrade copies it from the previous folder
unchanged. The file is created with permissions for `SYSTEM` (read) and `Administrators` (full) only,
inheritance off, so no other local account can read the hash or the certificate password.

`Loopback` (the default):

```json
{
  "ArkAdmin": {
    "DataRoot": "C:\\ArkAscendedServerAdmin",
    "PasswordHash": "pbkdf2$600000$...$...",
    "AllowInsecureHttp": false,
    "KnownProxies": []
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://127.0.0.1:5000" }
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    },
    "EventLog": {
      "LogLevel": { "Default": "Warning", "ArkAscendedServerAdmin": "Information" }
    }
  }
}
```

`LanHttps` differs in the `Kestrel` block:

```json
"Kestrel": {
  "Endpoints": {
    "Http": { "Url": "http://127.0.0.1:5000" },
    "Https": {
      "Url": "https://0.0.0.0:5001",
      "Certificate": {
        "Path": "C:\\ArkAscendedServerAdmin\\keys\\web-20260914-120000.pfx",
        "Password": "<random>"
      }
    }
  }
}
```

`Proxy` binds the HTTP listener to every interface and names the proxies:

```json
"ArkAdmin": { "...": "...", "KnownProxies": [ "10.0.0.1" ] },
"Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5000" } } }
```

To change something the installer does not have a switch for (a log level, an extra proxy address),
edit the file from an elevated editor (an unelevated administrator runs with a filtered token and
cannot open it), then `Restart-Service ArkAscendedServerAdmin`. Game servers keep running across a
service restart; the service re-attaches to them.

## The password

The installer prompts for the password, hashes it, and writes `ArkAdmin:PasswordHash`. The plain
text is never written to disk. To change it later:

```powershell
.\install.ps1 -SetPassword
```

from any extracted release folder, elevated. It prompts, rewrites the settings file, restarts the
service, and confirms with a login round trip. Details in [hosting.md](hosting.md#changing-the-password).

### Hashing by hand

The executable hashes a password read from standard input, without loading any configuration, so
it works from anywhere and needs no `DataRoot`:

```powershell
'my password' | & 'C:\ArkAscendedServerAdmin\App\ArkAscendedServerAdmin.Server.exe' --hash-password
```

It prints one line starting `pbkdf2$` and exits 0; anything else exits non-zero with nothing on
standard output. Exactly one trailing line break is stripped; every other character, including
leading and trailing spaces, is part of the password, and an empty password or one containing a
second line break is refused. PowerShell's pipeline does not always hand a native command UTF-8, so
for a password with characters outside ASCII use `-SetPassword`, which writes the bytes itself.

Put the string in `ArkAdmin:PasswordHash`. In JSON it needs no escaping; in PowerShell keep it in
single quotes because of the `$` signs.

### Where the password can live

Any standard ASP.NET Core configuration source works:

- `appsettings.Production.json` next to the executable. This is what the installer manages; it
  survives an upgrade because the installer copies it into the new folder.
- Environment variables on the service: `ArkAdmin__PasswordHash` (or `ArkAdmin__Password`), and
  likewise `ArkAdmin__DataRoot`, with a double underscore for the section separator, set through the
  service's `Environment` registry value (`HKLM\SYSTEM\CurrentControlSet\Services\ArkAscendedServerAdmin`,
  `REG_MULTI_SZ`) or the machine environment. Environment variables override the file. Because of
  that, `install.ps1 -SetPassword` refuses to run while either variable exists: the change would be
  written and never take effect.
- The base `appsettings.json`. Works, but an upgrade replaces that file.

Whichever file holds the hash, keep it readable only by `SYSTEM` and `Administrators`. The installer
does this for the file it writes; for a file you placed yourself:

```powershell
icacls C:\ArkAscendedServerAdmin\App\appsettings.Production.json /inheritance:r /grant:r SYSTEM:R Administrators:F
```

### What the hash protects

The cookie carries the hash string as a claim, so changing the password invalidates every session
and every open Blazor circuit at the next check (each request, and every five minutes per circuit).
The threat model for PBKDF2 here is someone who read a file only `SYSTEM` and administrators can
read; it is not a substitute for choosing a long password.

## File permissions the installer applies

On every install and upgrade:

| Folder | Permissions |
|---|---|
| `InstallDir`, `DataRoot` | `SYSTEM` full, `Administrators` full, `Users` read and execute. Inheritance off. You can browse worlds and logs unelevated; nobody but an elevated administrator can replace a file the service executes. |
| `DataRoot\keys`, `DataRoot\Data`, `DataRoot\Exports`, `DataRoot\Backups\_app` | `SYSTEM` full, `Administrators` full, nobody else. The key ring, the database, and every copy of the database (exports, upgrade copies) hold the CurseForge key in plain text. |
| `appsettings.Production.json` | `SYSTEM` read, `Administrators` full. |

The Data Protection key ring under `keys\` is additionally encrypted with DPAPI for the service
account, so a copy of a key file is useless on another machine or under another account. A database
restored on another machine works; the cookies do not, and everyone signs in again.

## App Settings (in the database)

Edited on the Settings page, applied without a restart:

| Setting | Meaning | Default |
|---|---|---|
| Stagger delay | Seconds between consecutive server launches in the start queue. | 30 |
| SteamCMD `validate` | Whether game installs and updates run with `validate`. | off |
| Game port start and step | Where suggested game ports for a new instance begin and how far apart they are (ASA uses the port and port + 1, so the step is two). | 7777, 2 |
| RCON port start and step | Same for RCON ports. | 27020, 1 |
| Default backup interval and retention | Applied to new instances; each instance can override. | 30 minutes, 10 |
| Backup quiescence | How long the world files must be unchanged after `saveworld` before the snapshot is taken. | 10 seconds |
| Pre-stop broadcast minutes | Countdown announced in-game before a stop; zero skips it. | 1 |
| Graceful stop timeout | How long to wait for the server to exit after `doexit` before it is killed. | 60 seconds |
| RCON timeout | Per-command timeout. | 10 seconds |
| Console backfill lines | Lines of `ShooterGame.log` read into the console when the service re-attaches to a running server. | 200 |
| Admin whitelist | Manager-wide list of EOS ids, merged ahead of every cluster and instance whitelist. | *(empty)* |
| CurseForge API key | Your own key, used for mod search. **Stored in plain text in the database.** The key is yours and subject to CurseForge's API terms; the database folder is readable only by `SYSTEM` and administrators after install, and every config export and upgrade copy of the database contains it. |

The Settings page also shows the host values above read-only (`DataRoot`, bind URLs, known proxies,
whether a password is configured, hosting model), the app version, and the config export button.
