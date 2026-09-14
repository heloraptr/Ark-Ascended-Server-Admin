#requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, upgrades, and maintains Ark Ascended Server Admin as a Windows service.

.DESCRIPTION
    Run from the extracted release zip in an elevated Windows PowerShell 5.1 or pwsh 7. First run: asks for
    the install folder, the data folder, the login password, and the bind mode, then registers and starts the
    service. Later runs with the service present are upgrades: the new files go next to the old ones, the
    folders are swapped, and the old folder stays as <InstallDir>.previous-<timestamp> until the new version
    answers its health probe. See INSTALL.md.

.PARAMETER InstallDir
    Where the app lives. Default C:\ArkAscendedServerAdmin\App. On an upgrade it is read from the service.

.PARAMETER DataRoot
    Where the game install (12 GB), the instances, the backups, the database, and the keys live. Default
    C:\ArkAscendedServerAdmin. On an upgrade it is read from the installed settings.

.PARAMETER Password
    The login password. Prompted (masked) when omitted.

.PARAMETER Bind
    Loopback (default; put an HTTPS reverse proxy in front), LanHttps (self-signed certificate, reachable
    from the LAN), or Proxy (plain HTTP on every interface for a reverse proxy on another machine).

.PARAMETER Port
    5000 for Loopback and Proxy, 5001 for LanHttps.

.PARAMETER KnownProxies
    Proxy mode: the reverse proxy addresses whose X-Forwarded-* headers are trusted and that the firewall
    rule admits.

.PARAMETER LanSource
    LanHttps mode: the firewall rule's remote address (default LocalSubnet).

.PARAMETER NoStart
    Leave the service stopped and every recovery artifact in place; finish later with -Verify.

.PARAMETER Quiet
    Never prompt; a missing required value is an error.

.PARAMETER SetPassword
    Change the login password of the installed service and verify it with a login.

.PARAMETER SetCertificate
    LanHttps: issue a new self-signed certificate, or import -PfxPath/-PfxPassword.

.PARAMETER Verify
    Probe the installed service and finish a pending operation.

.PARAMETER Rollback
    Undo a pending operation.

.PARAMETER ServiceName
    Testing parameter: the Windows service name (default ArkAscendedServerAdmin).
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$DataRoot,
    [string]$Password,
    [ValidateSet('Loopback', 'LanHttps', 'Proxy')]
    [string]$Bind,
    [ValidateRange(1, 65535)]
    [int]$Port,
    [string[]]$KnownProxies = @(),
    [string]$LanSource,
    [switch]$NoStart,
    [switch]$Quiet,
    [switch]$SetPassword,
    [switch]$SetCertificate,
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$Verify,
    [switch]$Rollback,
    [string]$ServiceName = 'ArkAscendedServerAdmin'
)

$ErrorActionPreference = 'Stop'
# Captured here: inside a function $PSBoundParameters is the function's own.
$scriptParameters = $PSBoundParameters
. (Join-Path $PSScriptRoot 'ArkInstall.Common.ps1')

$zipDir = ConvertTo-CanonicalPath $PSScriptRoot
$defaultDataRoot = 'C:\ArkAscendedServerAdmin'
$defaultInstallDir = 'C:\ArkAscendedServerAdmin\App'
$displayName = if ($ServiceName -eq 'ArkAscendedServerAdmin') { 'Ark Ascended Server Admin' } else { "Ark Ascended Server Admin ($ServiceName)" }
$serviceDescription = 'Self-hosted manager for ARK: Survival Ascended dedicated servers.'
$firewallDisplayName = "$displayName web UI"
$setupNote = 'Every page shows /setup until the 12 GB game install under DataRoot\Server finishes.'

# ---- prompts ------------------------------------------------------------------------------------------

function Read-Value([string]$prompt, [string]$default) {
    if ($Quiet) {
        if ($default) { return $default }
        throw "-Quiet: a value for '$prompt' is required."
    }
    $suffix = if ($default) { " [$default]" } else { '' }
    $answer = Read-Host -Prompt "$prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

function Read-PasswordValue([string]$prompt) {
    if ($Password) {
        # Built by hand: ConvertTo-SecureString needs the Microsoft.PowerShell.Security module.
        $secure = New-Object System.Security.SecureString
        foreach ($char in $Password.ToCharArray()) { $secure.AppendChar($char) }
        $secure.MakeReadOnly()
        return $secure
    }
    if ($Quiet) { throw '-Quiet: -Password is required.' }
    while ($true) {
        $first = Read-Host -Prompt $prompt -AsSecureString
        if ($first.Length -eq 0) { Write-Note 'The password cannot be empty.'; continue }
        $second = Read-Host -Prompt 'Repeat the password' -AsSecureString
        if ((ConvertTo-PlainText $first) -ceq (ConvertTo-PlainText $second)) { return $first }
        Write-Note 'The passwords do not match; try again.'
    }
}

function Read-BindMode {
    if ($scriptParameters.ContainsKey('Bind') -or $Bind) { return $Bind }
    if ($Quiet) { return 'Loopback' }
    Write-Host ''
    Write-Host 'Bind mode:'
    Write-Host '  Loopback  http://127.0.0.1 only; put an HTTPS reverse proxy on this machine in front (default)'
    Write-Host '  LanHttps  https on every interface with a self-signed certificate; reachable from the LAN'
    Write-Host '  Proxy     plain http on every interface, only for a reverse proxy on another machine'
    while ($true) {
        $answer = Read-Value 'Bind (Loopback, LanHttps, Proxy)' 'Loopback'
        if ($answer -in @('Loopback', 'LanHttps', 'Proxy')) { return $answer }
        Write-Note "Enter Loopback, LanHttps, or Proxy."
    }
}

# ---- summary ------------------------------------------------------------------------------------------

function Write-Summary([string]$installDir, $settings) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $state = if ($service) { "$($service.Status)" } else { 'not registered' }
    Write-Host ''
    Write-Host "Service : $ServiceName ($state)"
    switch ($settings.Bind) {
        'Loopback' {
            Write-Host "Bind    : http://127.0.0.1:$($settings.Port) (loopback)"
            Write-Host "          Point your HTTPS reverse proxy at http://127.0.0.1:$($settings.Port), forward X-Forwarded-Proto and enable"
            Write-Host '          WebSockets, then open the proxy''s URL. A direct http:// request answers 403 by design.'
        }
        'LanHttps' {
            $addresses = @(Get-LanIPv4Addresses)
            $shown = if ($addresses.Count -gt 0) { $addresses[0] } else { [System.Environment]::MachineName.ToLowerInvariant() }
            Write-Host "URL     : https://${shown}:$($settings.Port)/ (self-signed certificate: the browser warns once; accept it)"
            Write-Host "          The loopback listener http://127.0.0.1:$($settings.Port - 1) also exists for a local reverse proxy."
        }
        'Proxy' {
            Write-Host "Bind    : http://0.0.0.0:$($settings.Port) for the reverse proxies $($settings.KnownProxies -join ', ')"
            Write-Host '          Point the proxy at this machine''s address and port, forward X-Forwarded-Proto and enable WebSockets,'
            Write-Host '          then open the proxy''s URL. A direct http:// request answers 403 by design.'
        }
    }
    Write-Host "App     : $installDir"
    Write-Host "DataRoot: $($settings.DataRoot)"
    Write-Host "Settings: $($settings.Path)"
    Write-Host $setupNote
}

# The probe every run ends with: scheme, port, and thumbprint from the installed settings.
function Invoke-InstalledProbe([string]$installDir) {
    $settings = Get-InstalledSettings $installDir
    $thumbprint = $null
    if ($settings.Scheme -eq 'https') { $thumbprint = Get-PfxThumbprint $settings.CertPath $settings.CertPassword }
    return Wait-HealthProbe $settings.Scheme $settings.Port $thumbprint $ServiceName 60
}

function Write-ProbeFailure($probe, [string]$installDir, [string]$recovery) {
    Write-Host "Probe failed: $($probe.Reason)" -ForegroundColor Red
    Write-EventLogTail $ServiceName
    Write-Host ''
    Write-Note $recovery
}

# ---- existing service ---------------------------------------------------------------------------------

# InstallDir, settings, and marker of the installed service. Legacy adoption: a folder without a marker
# is accepted only because the service's binary path points into it; the marker is written before anything
# else happens.
function Get-Installation {
    $service = Get-ArkService $ServiceName
    if ($null -eq $service) { return $null }
    # Variable names are case-insensitive in PowerShell, so the local $installDir below IS the -InstallDir
    # parameter; what the caller asked for has to be captured before the service's own folder overwrites it.
    $requestedInstallDir = $InstallDir
    $installDir = Get-ServiceBinaryDir $service
    if ($requestedInstallDir -and -not (Test-PathEquals (ConvertTo-CanonicalPath $requestedInstallDir) $installDir)) {
        throw "Service '$ServiceName' runs from '$installDir' but -InstallDir names '$(ConvertTo-CanonicalPath $requestedInstallDir)'. Omit -InstallDir to upgrade the existing installation."
    }
    $settings = Get-InstalledSettings $installDir
    if ($DataRoot -and -not (Test-PathEquals (ConvertTo-CanonicalPath $DataRoot) $settings.DataRoot)) {
        throw "The installed settings name DataRoot '$($settings.DataRoot)' but -DataRoot names '$(ConvertTo-CanonicalPath $DataRoot)'. Omit -DataRoot; it is never changed by an upgrade."
    }
    $marker = Read-Marker $installDir
    if ($null -eq $marker) {
        Write-Note "No install.json in $installDir; adopting the installation because service '$ServiceName' runs from it."
        Write-Marker $installDir ([guid]::NewGuid().ToString()) $ServiceName $settings.DataRoot (Get-ExeProductVersion (Join-Path $installDir $script:ExeName))
        $marker = Read-Marker $installDir
    }
    elseif ((Get-JsonProperty $marker 'serviceName') -ne $ServiceName) {
        throw "$installDir\install.json belongs to service '$(Get-JsonProperty $marker 'serviceName')', not '$ServiceName'."
    }
    return [pscustomobject]@{
        Service    = $service
        InstallDir = $installDir
        Settings   = $settings
        Marker     = $marker
        InstallId  = [string](Get-JsonProperty $marker 'installId')
        Version    = [string](Get-JsonProperty $marker 'version')
    }
}

function Assert-Installation {
    $installation = Get-Installation
    if ($null -eq $installation) { throw "Service '$ServiceName' is not installed on this machine." }
    return $installation
}

# ---- first install ------------------------------------------------------------------------------------

function Invoke-FirstInstall($package) {
    Write-Step "Install $displayName $($package.Version)"
    $installDir = ConvertTo-CanonicalPath (Read-Value 'Install folder for the app' ($(if ($InstallDir) { $InstallDir } else { $defaultInstallDir })))
    if (-not $Quiet -and -not $DataRoot) { Write-Host 'The data folder receives the 12 GB game install, every instance, and the backups.' }
    $dataRoot = ConvertTo-CanonicalPath (Read-Value 'Data folder' ($(if ($DataRoot) { $DataRoot } else { $defaultDataRoot })))
    Assert-InstallPaths $installDir $dataRoot $zipDir

    if (Test-Path -LiteralPath $installDir) {
        $existingMarker = Read-Marker $installDir
        $nonEmpty = @(Get-ChildItem -LiteralPath $installDir -Force | Select-Object -First 1).Count -gt 0
        if ($nonEmpty -and ($null -eq $existingMarker -or (Get-JsonProperty $existingMarker 'serviceName') -ne $ServiceName)) {
            throw "InstallDir '$installDir' exists and is not empty, and it carries no install.json for service '$ServiceName'. Choose another folder or empty it."
        }
    }
    if (Test-Path -LiteralPath $dataRoot) {
        if (@(Get-ChildItem -LiteralPath $dataRoot -Force | Select-Object -First 1).Count -gt 0) {
            throw "DataRoot '$dataRoot' exists and is not empty. The installer replaces its permissions, so it only accepts a new or empty folder (or the data folder of the installed service on an upgrade)."
        }
    }

    $bind = Read-BindMode
    $defaultPort = if ($bind -eq 'LanHttps') { 5001 } else { 5000 }
    $port = if ($Port) { $Port } else { [int](Read-Value 'Port' "$defaultPort") }
    if ($bind -eq 'LanHttps' -and $port -le 1) { throw 'LanHttps needs a port above 1 (the loopback listener uses the port below it).' }
    $proxies = @($KnownProxies | Where-Object { $_ } | ForEach-Object { $_.Trim() })
    # $lanSource and the -LanSource parameter are one variable (names are case-insensitive), so whether the
    # caller gave one is read from the bound parameters rather than from the variable below.
    $lanSourceGiven = $scriptParameters.ContainsKey('LanSource')
    $lanSource = if ($LanSource) { $LanSource } else { 'LocalSubnet' }
    if ($bind -eq 'Proxy') {
        if ($proxies.Count -eq 0) {
            $answer = Read-Value 'Reverse proxy addresses (comma separated)' ''
            $proxies = @($answer -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        if ($proxies.Count -eq 0) { throw 'Proxy mode needs -KnownProxies: the reverse proxy addresses to trust.' }
        foreach ($proxy in $proxies) { if (-not ($proxy -as [System.Net.IPAddress])) { throw "KnownProxies entry '$proxy' is not an IP address." } }
    }
    elseif ($bind -eq 'LanHttps' -and -not $lanSourceGiven -and -not $Quiet) {
        $lanSource = Read-Value 'Allow HTTPS from (LocalSubnet, Any, or an address)' 'LocalSubnet'
    }
    $password = Read-PasswordValue 'Login password for the web UI'

    Write-Step 'Folders and permissions'
    Set-InstallAcls $installDir $dataRoot
    Assert-NoPendingTransaction $dataRoot
    if (Test-Path -LiteralPath $installDir) { Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force }
    $installId = [guid]::NewGuid().ToString()
    $journal = New-Journal $dataRoot 'install' $installDir $null $null $null $null $null

    try {
        Write-Step 'Password hash'
        $passwordHash = Get-PasswordHash (Join-Path $zipDir $script:ExeName) $password

        Write-Step "Copy to $installDir"
        Copy-PackageFiles $zipDir $installDir
        Write-Marker $installDir $installId $ServiceName $dataRoot $package.Version
        Set-JournalPhase $dataRoot $journal 'copied'

        $pfxPath = $null; $pfxPassword = $null
        if ($bind -eq 'LanHttps') {
            Write-Step 'Self-signed certificate'
            $pfxPath = Join-Path (Join-Path $dataRoot 'keys') "web-$(Get-Timestamp).pfx"
            $pfxPassword = New-RandomPassword
            $thumbprint = New-SelfSignedWebCertificate $pfxPath $pfxPassword
            Write-Host "Written $pfxPath (thumbprint $thumbprint)"
            Set-JournalValue $dataRoot $journal 'pfxNew' $pfxPath
        }

        Write-Step 'Settings'
        $settingsPath = Get-SettingsPath $installDir
        $settingsObject = New-ProductionSettings $dataRoot $passwordHash $bind $port $proxies $pfxPath $pfxPassword
        $null = Write-ProtectedTextFile $settingsPath (ConvertTo-JsonText $settingsObject)
        Write-Host "Written $settingsPath"
        Set-JournalPhase $dataRoot $journal 'settings-written'

        if ($bind -eq 'LanHttps') {
            Write-Step "Firewall: TCP $port from $lanSource"
            Set-WebFirewallRule $ServiceName $port @($lanSource) $firewallDisplayName
        }
        elseif ($bind -eq 'Proxy') {
            Write-Step "Firewall: TCP $port from $($proxies -join ', ')"
            Set-WebFirewallRule $ServiceName $port $proxies $firewallDisplayName
        }

        Write-Step "Register service $ServiceName (LocalSystem, automatic start)"
        $exe = Join-Path $installDir $script:ExeName
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName $displayName -Description $serviceDescription -StartupType Automatic | Out-Null
        # Two restarts 5 s apart, then stop: the SCM repeats the last action for every later failure, so a
        # third restart would loop forever. The reset period counts failure-free time.
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000//0 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe failure returned exit code $LASTEXITCODE." }
        Set-JournalPhase $dataRoot $journal 'registered'
    }
    catch {
        # Nothing has started yet: take the half-made install down and leave DataRoot.
        Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Note 'Undoing the partial install.'
        Undo-FirstInstall $installDir $installId $dataRoot
        throw
    }

    if ($NoStart) {
        Write-Note "Service registered but not started (-NoStart). Verification pending: start the service and run install.ps1 -Verify."
        Write-Note "Until then $(Get-JournalPath $dataRoot) records the pending install."
        return
    }

    Write-Step "Start $ServiceName"
    Start-ArkService $ServiceName
    Set-JournalPhase $dataRoot $journal 'started'
    $probe = Invoke-InstalledProbe $installDir
    if (-not $probe.Success) {
        Stop-ArkService $ServiceName
        Write-ProbeFailure $probe $installDir "The service is stopped. Fix the cause (see the event log above), then Start-Service $ServiceName and run install.ps1 -Verify; or run install.ps1 -Rollback to remove the service and $installDir (DataRoot stays)."
        exit 1
    }
    Set-JournalPhase $dataRoot $journal 'verified'
    Remove-Journal $dataRoot
    Write-Host "Probe ok: $($probe.Url)" -ForegroundColor Green
    Write-Summary $installDir (Get-InstalledSettings $installDir)
}

function Undo-FirstInstall([string]$installDir, [string]$installId, [string]$dataRoot) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-ArkService $ServiceName
        & sc.exe delete $ServiceName | Out-Null
    }
    Remove-WebFirewallRule $ServiceName
    if (Test-Path -LiteralPath $installDir) {
        $marker = Read-Marker $installDir
        if ($null -eq $marker -or (Get-JsonProperty $marker 'installId') -eq $installId) {
            Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force
        }
    }
    Remove-Journal $dataRoot
}

# ---- upgrade ------------------------------------------------------------------------------------------

function Invoke-Upgrade($package, $installation) {
    $installDir = $installation.InstallDir
    $dataRoot = $installation.Settings.DataRoot
    $oldVersion = $installation.Version
    Write-Step "Upgrade $displayName $oldVersion -> $($package.Version) in $installDir"
    foreach ($name in 'Password', 'Bind', 'Port', 'KnownProxies', 'LanSource') {
        if ($scriptParameters.ContainsKey($name)) { Write-Note "-$name is ignored on an upgrade; the installed settings are kept." }
    }
    Assert-InstallPaths $installDir $dataRoot $zipDir
    Assert-NoPendingTransaction $dataRoot
    $timestamp = Get-Timestamp
    $newDir = "$installDir.new"
    $previousDir = "$installDir.previous-$timestamp"
    Assert-SwapPath $newDir 'InstallDir.new' $dataRoot $zipDir
    Assert-SwapPath $previousDir 'InstallDir.previous' $dataRoot $zipDir
    if (Test-Path -LiteralPath $previousDir) { throw "'$previousDir' already exists; wait a second and run again." }
    $dbCopyDir = Join-Path (Join-Path (Join-Path $dataRoot 'Backups') '_app') "$timestamp-$oldVersion"

    Write-Step 'Permissions'
    Set-InstallAcls $installDir $dataRoot
    Remove-SwapFolder $newDir $installation.InstallId $dataRoot $zipDir
    New-Item -ItemType Directory -Path $newDir -Force | Out-Null
    Set-ProtectedAcl $newDir $true

    $journal = New-Journal $dataRoot 'upgrade' $installDir $previousDir $dbCopyDir $null $null $null

    Write-Step "Copy to $newDir"
    Copy-PackageFiles $zipDir $newDir
    Write-Marker $newDir $installation.InstallId $ServiceName $dataRoot $package.Version
    Set-JournalPhase $dataRoot $journal 'copied'

    $swapped = $false
    try {
        Write-Step "Stop $ServiceName"
        Stop-ArkService $ServiceName
        Set-JournalPhase $dataRoot $journal 'stopped'

        Write-Step "Copy the database to $dbCopyDir"
        $count = Copy-DatabaseFiles $dataRoot $dbCopyDir
        Write-Host "$count file(s) copied"
        Set-JournalPhase $dataRoot $journal 'db-copied'

        $deletedKeys = Remove-UnprotectedKeys $dataRoot
        if ($deletedKeys -gt 0) { Write-Note "$deletedKeys unprotected key-ring file(s) deleted; every browser session must sign in again." }

        Write-Step 'Swap folders'
        Rename-Item -LiteralPath $installDir -NewName (Split-Path $previousDir -Leaf)
        $swapped = $true
        Rename-Item -LiteralPath $newDir -NewName (Split-Path $installDir -Leaf)
        Set-JournalPhase $dataRoot $journal 'swapped'

        $bak = Copy-ProtectedFile (Get-SettingsPath $previousDir) (Get-SettingsPath $installDir)
        Set-JournalValue $dataRoot $journal 'settingsBak' $bak
        Set-JournalPhase $dataRoot $journal 'settings-written'
    }
    catch {
        Write-Host "Upgrade failed before the service was started: $($_.Exception.Message)" -ForegroundColor Red
        Write-Note 'Putting the previous version back.'
        if ($swapped) {
            if (Test-Path -LiteralPath $installDir) { Rename-Item -LiteralPath $installDir -NewName (Split-Path $newDir -Leaf) }
            Rename-Item -LiteralPath $previousDir -NewName (Split-Path $installDir -Leaf)
        }
        Remove-SwapFolder $newDir $installation.InstallId $dataRoot $zipDir
        Remove-Journal $dataRoot
        Start-ArkService $ServiceName
        throw
    }

    if ($NoStart) {
        Write-Note "Files swapped but the service is not started (-NoStart). Verification pending: start the service and run install.ps1 -Verify."
        Write-Note "Recovery copies kept: $previousDir and $dbCopyDir."
        return
    }

    Write-Step "Start $ServiceName"
    Start-ArkService $ServiceName
    Set-JournalPhase $dataRoot $journal 'started'
    $probe = Invoke-InstalledProbe $installDir
    if (-not $probe.Success) {
        Stop-ArkService $ServiceName
        Write-ProbeFailure $probe $installDir "The service is stopped on the new files. Run install.ps1 -Rollback to put $previousDir back and restore the database file set from $dbCopyDir (a migration may already have run), or fix the cause, Start-Service $ServiceName, and run install.ps1 -Verify."
        exit 1
    }
    Set-JournalPhase $dataRoot $journal 'verified'
    Complete-Upgrade $journal $installation.InstallId $dataRoot
    Write-Host "Probe ok: $($probe.Url)" -ForegroundColor Green
    Write-Summary $installDir (Get-InstalledSettings $installDir)
}

# Cleanup after a verified upgrade: the settings .bak, then every older recovery set (the newest pair stays).
function Complete-Upgrade($journal, [string]$installId, [string]$dataRoot) {
    $installDir = [string](Get-JsonProperty $journal 'installDir')
    $bak = [string](Get-JsonProperty $journal 'settingsBak')
    if ($bak -and (Test-Path -LiteralPath $bak)) { Remove-Item -LiteralPath $bak -Force }
    Remove-OldRecoverySets $installDir $installId $dataRoot $zipDir ([string](Get-JsonProperty $journal 'previousDir')) ([string](Get-JsonProperty $journal 'dbCopyDir'))
    Remove-Journal $dataRoot
}

# ---- -SetPassword -------------------------------------------------------------------------------------

function Invoke-SetPassword($installation) {
    if ($NoStart) { throw '-SetPassword -NoStart is refused: the new password is verified by a login, which cannot be deferred.' }
    $installDir = $installation.InstallDir
    $settings = $installation.Settings
    $dataRoot = $settings.DataRoot
    Write-Step "Change the login password of $ServiceName"
    Assert-NoPendingTransaction $dataRoot
    $override = Get-PasswordEnvironmentOverride $ServiceName
    if ($override) { throw "The password is overridden by $override, so a change to the settings file would not take effect. Remove that override first." }

    $password = Read-PasswordValue 'New login password'
    Write-Step 'Password hash'
    $passwordHash = Get-PasswordHash (Join-Path $installDir $script:ExeName) $password

    $journal = New-Journal $dataRoot 'set-password' $installDir $null $null $null $null $null
    $raw = $settings.Raw
    $ark = Get-JsonProperty $raw 'ArkAdmin'
    if ($null -eq $ark) { $ark = [pscustomobject]@{}; Set-JsonProperty $raw 'ArkAdmin' $ark }
    if ($ark.PSObject.Properties['Password']) { $ark.PSObject.Properties.Remove('Password') }
    Set-JsonProperty $ark 'PasswordHash' $passwordHash
    $bak = Write-ProtectedTextFile $settings.Path (ConvertTo-JsonText $raw)
    Set-JournalValue $dataRoot $journal 'settingsBak' $bak
    Set-JournalPhase $dataRoot $journal 'settings-written'

    Write-Step "Restart $ServiceName"
    Stop-ArkService $ServiceName
    Start-ArkService $ServiceName
    Set-JournalPhase $dataRoot $journal 'started'

    if (Complete-PasswordChange $journal $installDir $password) {
        Write-Host 'Password verified: the new password signs in.' -ForegroundColor Green
        Write-Note 'Game servers were not affected by the restart of the manager service.'
        return
    }
    Write-Note 'The new password did not sign in. Restoring the previous settings file.'
    Undo-SettingsChange $journal $installDir
    exit 1
}

# Probe, then the login round trip; on success the .bak and the journal go. Returns $true on success.
function Complete-PasswordChange($journal, [string]$installDir, [securestring]$password) {
    $dataRoot = (Get-InstalledSettings $installDir).DataRoot
    $probe = Invoke-InstalledProbe $installDir
    if (-not $probe.Success) {
        Write-Host "Probe failed: $($probe.Reason)" -ForegroundColor Red
        Write-EventLogTail $ServiceName
        return $false
    }
    $settings = Get-InstalledSettings $installDir
    $thumbprint = $null
    if ($settings.Scheme -eq 'https') { $thumbprint = Get-PfxThumbprint $settings.CertPath $settings.CertPassword }
    Write-Step 'Login round trip'
    $loggedIn = $false
    try { $loggedIn = Test-LoginRoundTrip $settings.Scheme $settings.Port $thumbprint $password }
    catch { Write-Host "Login round trip failed: $($_.Exception.Message)" -ForegroundColor Red }
    if (-not $loggedIn) { return $false }
    $bak = [string](Get-JsonProperty $journal 'settingsBak')
    if ($bak -and (Test-Path -LiteralPath $bak)) { Remove-Item -LiteralPath $bak -Force }
    Remove-Journal $dataRoot
    return $true
}

# Restores the settings .bak (and removes a new PFX) recorded in a set-password / set-certificate journal,
# restarts, probes with the restored configuration, and clears the journal.
function Undo-SettingsChange($journal, [string]$installDir) {
    $dataRoot = (Get-InstalledSettings $installDir).DataRoot
    Stop-ArkService $ServiceName
    $restored = Restore-ProtectedFileBackup (Get-SettingsPath $installDir)
    if ($restored) { Write-Host 'Previous settings file restored.' }
    $pfxNew = [string](Get-JsonProperty $journal 'pfxNew')
    if ($pfxNew -and (Test-Path -LiteralPath $pfxNew)) { Remove-Item -LiteralPath $pfxNew -Force; Write-Host "Removed $pfxNew" }
    Start-ArkService $ServiceName
    $probe = Invoke-InstalledProbe $installDir
    if ($probe.Success) {
        Remove-Journal $dataRoot
        Write-Host "Service running on the previous configuration (probe ok: $($probe.Url))." -ForegroundColor Green
    }
    else {
        Write-ProbeFailure $probe $installDir "The previous configuration does not answer either. The journal $(Get-JournalPath $dataRoot) is kept; check the event log, then run install.ps1 -Verify or -Rollback."
    }
}

# ---- -SetCertificate ----------------------------------------------------------------------------------

function Invoke-SetCertificate($installation) {
    $installDir = $installation.InstallDir
    $settings = $installation.Settings
    $dataRoot = $settings.DataRoot
    Write-Step "Replace the HTTPS certificate of $ServiceName"
    if ($settings.Scheme -ne 'https') { throw "The installed configuration has no HTTPS endpoint (bind mode $($settings.Bind)); -SetCertificate applies to LanHttps installs." }
    Assert-NoPendingTransaction $dataRoot
    if ($PfxPath -and -not (Test-Path -LiteralPath $PfxPath)) { throw "PFX file not found: $PfxPath" }

    $pfxOld = $settings.CertPath
    $pfxNew = Join-Path (Join-Path $dataRoot 'keys') "web-$(Get-Timestamp).pfx"
    # Not $pfxPassword: that name is the -PfxPassword parameter (PowerShell names are case-insensitive) and
    # assigning to it would destroy the password of the user's own PFX before it is opened.
    $newPfxPassword = New-RandomPassword
    $journal = New-Journal $dataRoot 'set-certificate' $installDir $null $null $null $pfxOld $pfxNew
    if ($PfxPath) {
        Write-Step "Import $PfxPath"
        $thumbprint = Import-UserCertificate $PfxPath $PfxPassword $pfxNew $newPfxPassword
    }
    else {
        Write-Step 'New self-signed certificate'
        $thumbprint = New-SelfSignedWebCertificate $pfxNew $newPfxPassword
    }
    Write-Host "Written $pfxNew (thumbprint $thumbprint)"
    Set-JournalPhase $dataRoot $journal 'copied'

    $raw = $settings.Raw
    $https = Get-JsonProperty (Get-JsonProperty (Get-JsonProperty $raw 'Kestrel') 'Endpoints') 'Https'
    Set-JsonProperty $https 'Certificate' ([pscustomobject][ordered]@{ Path = $pfxNew; Password = $newPfxPassword })
    $bak = Write-ProtectedTextFile $settings.Path (ConvertTo-JsonText $raw)
    Set-JournalValue $dataRoot $journal 'settingsBak' $bak
    Set-JournalPhase $dataRoot $journal 'settings-written'

    Write-Step "Restart $ServiceName"
    Stop-ArkService $ServiceName
    Start-ArkService $ServiceName
    Set-JournalPhase $dataRoot $journal 'started'

    $probe = Invoke-InstalledProbe $installDir
    if ($probe.Success) {
        Set-JournalPhase $dataRoot $journal 'verified'
        Complete-CertificateChange $journal $dataRoot
        Write-Host "Certificate replaced (probe ok with thumbprint $thumbprint)." -ForegroundColor Green
        return
    }
    Write-Host "Probe failed with the new certificate: $($probe.Reason)" -ForegroundColor Red
    Write-EventLogTail $ServiceName
    Write-Note 'Restoring the previous certificate.'
    Undo-SettingsChange $journal $installDir
    exit 1
}

function Complete-CertificateChange($journal, [string]$dataRoot) {
    $bak = [string](Get-JsonProperty $journal 'settingsBak')
    if ($bak -and (Test-Path -LiteralPath $bak)) { Remove-Item -LiteralPath $bak -Force }
    $pfxOld = [string](Get-JsonProperty $journal 'pfxOld')
    $pfxNew = [string](Get-JsonProperty $journal 'pfxNew')
    if ($pfxOld -and (Test-Path -LiteralPath $pfxOld) -and -not (Test-PathEquals $pfxOld $pfxNew)) { Remove-Item -LiteralPath $pfxOld -Force }
    Remove-Journal $dataRoot
}

# ---- -Verify ------------------------------------------------------------------------------------------

function Invoke-Verify($installation) {
    $installDir = $installation.InstallDir
    $dataRoot = $installation.Settings.DataRoot
    $journal = Read-Journal $dataRoot
    $operation = if ($journal) { [string](Get-JsonProperty $journal 'operation') } else { 'none' }
    Write-Step "Verify $ServiceName (pending operation: $operation)"

    $service = Get-Service -Name $ServiceName
    if ("$($service.Status)" -ne 'Running') {
        Write-Host "Starting $ServiceName"
        Start-ArkService $ServiceName
    }

    if ($operation -eq 'set-password') {
        $password = Read-PasswordValue 'The new password (to repeat the login check)'
        if (Complete-PasswordChange $journal $installDir $password) {
            Write-Host 'Password verified: the new password signs in.' -ForegroundColor Green
            return
        }
        Write-Note 'The new password did not sign in. The change is not committed; run install.ps1 -Rollback to restore the old password.'
        exit 1
    }

    $probe = Invoke-InstalledProbe $installDir
    if (-not $probe.Success) {
        Write-ProbeFailure $probe $installDir "Verification failed. Fix the cause and run install.ps1 -Verify again, or install.ps1 -Rollback."
        exit 1
    }
    Write-Host "Probe ok: $($probe.Url)" -ForegroundColor Green
    switch ($operation) {
        'upgrade' { Complete-Upgrade $journal $installation.InstallId $dataRoot }
        'install' { Remove-Journal $dataRoot }
        'set-certificate' { Complete-CertificateChange $journal $dataRoot }
        'none' { }
    }
    if ($journal) { Write-Host "Pending $operation completed." -ForegroundColor Green }
    Write-Summary $installDir (Get-InstalledSettings $installDir)
}

# ---- -Rollback ----------------------------------------------------------------------------------------

function Invoke-Rollback($installation) {
    $installDir = $installation.InstallDir
    $dataRoot = $installation.Settings.DataRoot
    $journal = Read-Journal $dataRoot
    if ($null -eq $journal) { throw "Nothing is pending: $(Get-JournalPath $dataRoot) does not exist." }
    $operation = [string](Get-JsonProperty $journal 'operation')
    Write-Step "Roll back the pending $operation (phase '$(Get-JsonProperty $journal 'phase')')"

    switch ($operation) {
        'install' {
            Write-Note "Removing service $ServiceName and $installDir; DataRoot $dataRoot stays."
            Undo-FirstInstall $installDir $installation.InstallId $dataRoot
            Write-Host 'Rolled back.' -ForegroundColor Green
            return
        }
        'upgrade' {
            Stop-ArkService $ServiceName
            $previousDir = [string](Get-JsonProperty $journal 'previousDir')
            $newDir = "$installDir.new"
            $dbCopyDir = [string](Get-JsonProperty $journal 'dbCopyDir')
            if (Test-PhaseReached $journal 'swapped') {
                if (-not (Test-Path -LiteralPath $previousDir)) { throw "The previous folder $previousDir is gone; nothing to roll back to." }
                Remove-SwapFolder $newDir $installation.InstallId $dataRoot $zipDir
                $current = Read-Marker $installDir
                if ($null -eq $current -or (Get-JsonProperty $current 'installId') -ne $installation.InstallId) { throw "$installDir carries no marker with installId $($installation.InstallId); refusing to move it." }
                Rename-Item -LiteralPath $installDir -NewName (Split-Path $newDir -Leaf)
                Rename-Item -LiteralPath $previousDir -NewName (Split-Path $installDir -Leaf)
                Write-Host "$previousDir is back as $installDir"
            }
            if ((Test-PhaseReached $journal 'db-copied') -and $dbCopyDir -and (Test-Path -LiteralPath $dbCopyDir)) {
                Restore-DatabaseFiles $dataRoot $dbCopyDir
                Write-Host "Database file set restored from $dbCopyDir"
            }
            Remove-SwapFolder $newDir $installation.InstallId $dataRoot $zipDir
            $bak = Get-SettingsPath $installDir
            if (Test-Path -LiteralPath "$bak.bak") { Remove-Item -LiteralPath "$bak.bak" -Force }
            Start-ArkService $ServiceName
            $probe = Invoke-InstalledProbe $installDir
            if (-not $probe.Success) {
                Write-ProbeFailure $probe $installDir "The restored version does not answer either. The journal is kept; check the event log."
                exit 1
            }
            Remove-Journal $dataRoot
            Write-Host "Rolled back; probe ok: $($probe.Url)" -ForegroundColor Green
            Write-Summary $installDir (Get-InstalledSettings $installDir)
            return
        }
        { $_ -in @('set-password', 'set-certificate') } {
            Undo-SettingsChange $journal $installDir
            return
        }
        default { throw "Unknown pending operation '$operation' in $(Get-JournalPath $dataRoot)." }
    }
}

# ---- main ---------------------------------------------------------------------------------------------

$modes = @($SetPassword, $SetCertificate, $Verify, $Rollback | Where-Object { $_ }).Count
if ($modes -gt 1) { throw 'Use only one of -SetPassword, -SetCertificate, -Verify, -Rollback.' }

$mutex = $null
try {
    $mutex = Enter-ArkInstallMutex
    if ($Rollback) { Invoke-Rollback (Assert-Installation) }
    elseif ($Verify) { Invoke-Verify (Assert-Installation) }
    elseif ($SetPassword) { Invoke-SetPassword (Assert-Installation) }
    elseif ($SetCertificate) { Invoke-SetCertificate (Assert-Installation) }
    else {
        $package = Read-PackageInfo $zipDir
        # Before any packaged executable is invoked: the framework-dependent package needs the runtime.
        if (-not $package.SelfContained) { Assert-AspNetCoreRuntime }
        $installation = Get-Installation
        if ($null -eq $installation) { Invoke-FirstInstall $package }
        else { Invoke-Upgrade $package $installation }
    }
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Exit-ArkInstallMutex $mutex
}
