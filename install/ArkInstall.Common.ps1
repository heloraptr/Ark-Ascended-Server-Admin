# Shared functions for install.ps1 and uninstall.ps1 (dot-sourced by both). Windows PowerShell 5.1 and pwsh 7.
#
# Nothing in here prompts; the scripts decide what to ask. Everything that can fail throws, and the scripts
# turn the exception into a message and a non-zero exit code.

$script:MutexName = 'Global\ArkAscendedServerAdmin.Install'
$script:ExeName = 'ArkAscendedServerAdmin.Server.exe'
$script:SettingsFileName = 'appsettings.Production.json'
$script:MarkerFileName = 'install.json'
$script:JournalFileName = 'install-pending.json'
$script:PackageFileName = 'package.json'
$script:FirewallTagFileName = 'firewall.tag'
$script:InstanceRulePrefix = 'ArkAscendedServerAdmin-'
$script:HealthPath = '/healthz'
$script:HealthBody = 'ArkAscendedServerAdmin ok'
$script:AuthCookieName = 'ArkAscendedServerAdmin.Auth'
# The app's event-log source is fixed in Program.cs (AddWindowsService uses the application name); the
# service name given to the installer is added to the lookup so a renamed service still finds its log lines.
$script:EventSources = @('ArkAscendedServerAdmin', 'ArkAscendedServerAdmin.Server')
# Folder names the app manages directly under DataRoot; InstallDir may not be one of them or inside one.
$script:ManagedDataRootNames = @('Server', 'SteamCMD', 'Instances', 'Clusters', 'Backups', 'Archive', 'keys', 'Exports', 'Data')
$script:RuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'
# Journal phases in the order a transaction moves through them; Test-PhaseReached compares positions.
$script:PhaseOrder = @('pending', 'copied', 'stopped', 'db-copied', 'swapped', 'settings-written', 'registered', 'started', 'verified')

$script:SidSystem = 'S-1-5-18'
$script:SidAdministrators = 'S-1-5-32-544'
$script:SidUsers = 'S-1-5-32-545'

# ---- output -------------------------------------------------------------------------------------------

function Write-Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Note([string]$text) { Write-Host $text -ForegroundColor Yellow }

# ---- mutex --------------------------------------------------------------------------------------------

# One installer at a time on the box. The name is fixed on purpose: a second copy with a different service
# name still shares the machine's SCM, firewall, and event log.
function Enter-ArkInstallMutex {
    $created = $false
    $mutex = New-Object System.Threading.Mutex($false, $script:MutexName, [ref]$created)
    $acquired = $false
    try { $acquired = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) {
        $mutex.Dispose()
        throw "Another install.ps1 or uninstall.ps1 is running on this machine (mutex $($script:MutexName)). Wait for it to finish."
    }
    return $mutex
}

function Exit-ArkInstallMutex($mutex) {
    if ($null -eq $mutex) { return }
    try { $mutex.ReleaseMutex() } catch { }
    $mutex.Dispose()
}

# ---- paths --------------------------------------------------------------------------------------------

function ConvertTo-CanonicalPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw 'A path is required.' }
    $full = [System.IO.Path]::GetFullPath($path.Trim())
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\', '/') }
    return $full
}

function Test-PathEquals([string]$a, [string]$b) {
    return [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
}

# True when $child is strictly inside $parent.
function Test-PathInside([string]$child, [string]$parent) {
    $prefix = $parent.TrimEnd('\') + '\'
    return $child.Length -gt $prefix.Length -and $child.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathIsRoot([string]$path) {
    return Test-PathEquals $path ([System.IO.Path]::GetPathRoot($path))
}

function Get-PathAncestors([string]$path) {
    $result = New-Object System.Collections.Generic.List[string]
    $current = $path
    while ($true) {
        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent)) { break }
        $result.Add($parent)
        $current = $parent
    }
    return $result
}

# Folders the installer never touches, with every ancestor (so "C:\" and "C:\Users" are refused too).
function Get-ForbiddenPaths {
    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) { $roots.Add($drive.Name) }
    foreach ($name in 'SystemRoot', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramData', 'Public', 'ProgramW6432') {
        $value = [System.Environment]::GetEnvironmentVariable($name)
        if ($value) { $roots.Add($value) }
    }
    $profilesDir = $null
    try { $profilesDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction Stop).ProfilesDirectory } catch { }
    if (-not $profilesDir) { $profilesDir = Join-Path $env:SystemDrive 'Users' }
    $profilesDir = [System.Environment]::ExpandEnvironmentVariables($profilesDir)
    $roots.Add($profilesDir)
    if (Test-Path $profilesDir) {
        foreach ($profile in Get-ChildItem $profilesDir -Directory -Force -ErrorAction SilentlyContinue) { $roots.Add($profile.FullName) }
    }
    if ($env:USERPROFILE) { $roots.Add($env:USERPROFILE) }
    foreach ($root in $roots) {
        $canonical = $null
        try { $canonical = ConvertTo-CanonicalPath $root } catch { continue }
        [void]$set.Add($canonical)
        foreach ($ancestor in Get-PathAncestors $canonical) { [void]$set.Add($ancestor) }
    }
    # The comma keeps the set from being unrolled into an array on output.
    return , $set
}

# Returns the first segment of $path (or an ancestor) that is a reparse point, or $null.
function Get-ReparsePointInChain([string]$path) {
    $root = [System.IO.Path]::GetPathRoot($path)
    $rest = $path.Substring($root.Length).Trim('\')
    $current = $root
    if ($rest.Length -eq 0) { return $null }
    foreach ($segment in $rest.Split('\')) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        $attributes = (Get-Item -LiteralPath $current -Force).Attributes
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $current }
    }
    return $null
}

# Rules that apply to every folder the installer creates, renames, or deletes.
function Assert-ManagedPath([string]$path, [string]$label, [string]$zipDir) {
    if (-not [System.IO.Path]::IsPathRooted($path) -or $path -notmatch '^[A-Za-z]:\\') {
        throw "$label '$path' must be an absolute path on a local drive letter."
    }
    $drive = New-Object System.IO.DriveInfo ([System.IO.Path]::GetPathRoot($path))
    if ($drive.DriveType -ne [System.IO.DriveType]::Fixed) {
        throw "$label '$path' is not on a fixed local drive (drive type: $($drive.DriveType))."
    }
    if (Test-PathIsRoot $path) { throw "$label '$path' is a volume root." }
    $forbidden = Get-ForbiddenPaths
    if ($forbidden.Contains($path) -or (@($forbidden) -contains $path)) {
        throw "$label '$path' is a Windows, Program Files, ProgramData, Public, or user-profile folder (or an ancestor of one)."
    }
    if ($zipDir) {
        if (Test-PathEquals $path $zipDir) { throw "$label '$path' is the folder this script runs from (the extracted zip)." }
        if (Test-PathInside $zipDir $path) { throw "$label '$path' contains the folder this script runs from (the extracted zip)." }
        if (Test-PathInside $path $zipDir) { throw "$label '$path' is inside the folder this script runs from (the extracted zip)." }
    }
    $reparse = Get-ReparsePointInChain $path
    if ($reparse) { throw "$label '$path': '$reparse' is a junction or symbolic link; the installer only works on real folders." }
}

function Assert-InstallPaths([string]$installDir, [string]$dataRoot, [string]$zipDir) {
    Assert-ManagedPath $installDir 'InstallDir' $zipDir
    Assert-ManagedPath $dataRoot 'DataRoot' $zipDir
    if (Test-PathEquals $installDir $dataRoot) { throw "InstallDir and DataRoot are the same folder ('$installDir'); the app must not live inside its own data." }
    if (Test-PathInside $dataRoot $installDir) { throw "DataRoot '$dataRoot' is inside InstallDir '$installDir'; an upgrade replaces InstallDir." }
    if (Test-PathInside $installDir $dataRoot) {
        $relative = $installDir.Substring($dataRoot.TrimEnd('\').Length + 1)
        $first = $relative.Split('\')[0]
        if ($relative.Contains('\')) { throw "InstallDir '$installDir' is more than one level below DataRoot '$dataRoot'; only a direct child such as '$dataRoot\App' is allowed." }
        if ($script:ManagedDataRootNames -contains $first) { throw "InstallDir '$installDir' uses '$first', a folder name the app manages under DataRoot." }
    }
}

# <InstallDir>.new and <InstallDir>.previous-* obey the same rules before they are created or deleted.
function Assert-SwapPath([string]$path, [string]$label, [string]$dataRoot, [string]$zipDir) {
    Assert-ManagedPath $path $label $zipDir
    if (Test-PathEquals $path $dataRoot) { throw "$label '$path' is DataRoot." }
    if (Test-PathInside $dataRoot $path) { throw "$label '$path' contains DataRoot." }
}

# ---- JSON ---------------------------------------------------------------------------------------------

function Read-JsonFile([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    return $text | ConvertFrom-Json
}

function ConvertTo-JsonText($object) {
    return ($object | ConvertTo-Json -Depth 12) + "`n"
}

function Get-JsonProperty($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Set-JsonProperty($object, [string]$name, $value) {
    $object | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
}

function Write-Utf8File([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
}

# ---- services -----------------------------------------------------------------------------------------

function Get-ArkService([string]$serviceName) {
    return Get-CimInstance Win32_Service -Filter "Name = '$($serviceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue
}

# The folder of the service's executable from its (quoted) BinaryPathName.
function Get-ServiceBinaryPath($service) {
    $pathName = [string]$service.PathName
    if ([string]::IsNullOrWhiteSpace($pathName)) { throw "Service '$($service.Name)' has no binary path." }
    $pathName = $pathName.Trim()
    if ($pathName.StartsWith('"')) {
        $end = $pathName.IndexOf('"', 1)
        if ($end -lt 0) { throw "Service '$($service.Name)' has an unbalanced quote in its binary path: $pathName" }
        $exe = $pathName.Substring(1, $end - 1)
    }
    else {
        $exe = $pathName.Split(' ')[0]
    }
    return ConvertTo-CanonicalPath $exe
}

function Get-ServiceBinaryDir($service) {
    return Split-Path (Get-ServiceBinaryPath $service) -Parent
}

function Wait-ServiceState([string]$serviceName, [string]$state, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -eq $service) { throw "Service '$serviceName' disappeared while waiting for state '$state'." }
        if ("$($service.Status)" -eq $state) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Service '$serviceName' did not reach state '$state' within $timeoutSeconds s."
}

function Stop-ArkService([string]$serviceName) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    if ("$($service.Status)" -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
    }
    Wait-ServiceState $serviceName 'Stopped' 120
}

function Start-ArkService([string]$serviceName) {
    Start-Service -Name $serviceName -ErrorAction Stop
    Wait-ServiceState $serviceName 'Running' 60
}

function Get-ServiceProcessId([string]$serviceName) {
    $service = Get-ArkService $serviceName
    if ($null -eq $service) { return 0 }
    return [int]$service.ProcessId
}

# ---- ACLs ---------------------------------------------------------------------------------------------

# The ACL API without the Microsoft.PowerShell.Security module: .NET Framework has the static
# Directory/File methods, .NET (pwsh 7) has FileSystemAclExtensions. Access section only, so the owner is
# never touched.
function Get-FileSystemSecurity([string]$path) {
    $isDirectory = (Get-Item -LiteralPath $path -Force).PSIsContainer
    $sections = [System.Security.AccessControl.AccessControlSections]::Access
    if ($PSVersionTable.PSEdition -eq 'Core') {
        if ($isDirectory) { return [System.IO.FileSystemAclExtensions]::GetAccessControl([System.IO.DirectoryInfo]$path, $sections) }
        return [System.IO.FileSystemAclExtensions]::GetAccessControl([System.IO.FileInfo]$path, $sections)
    }
    if ($isDirectory) { return [System.IO.Directory]::GetAccessControl($path, $sections) }
    return [System.IO.File]::GetAccessControl($path, $sections)
}

function Set-FileSystemSecurity([string]$path, $security) {
    $isDirectory = (Get-Item -LiteralPath $path -Force).PSIsContainer
    if ($PSVersionTable.PSEdition -eq 'Core') {
        if ($isDirectory) { [System.IO.FileSystemAclExtensions]::SetAccessControl([System.IO.DirectoryInfo]$path, $security) }
        else { [System.IO.FileSystemAclExtensions]::SetAccessControl([System.IO.FileInfo]$path, $security) }
        return
    }
    if ($isDirectory) { [System.IO.Directory]::SetAccessControl($path, $security) }
    else { [System.IO.File]::SetAccessControl($path, $security) }
}

function Get-AclRuleSet([string]$path) {
    $acl = Get-FileSystemSecurity $path
    $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    $set = @()
    foreach ($rule in $rules) {
        $set += '{0}|{1}|{2}|{3}|{4}' -f $rule.IdentityReference.Value, [int]$rule.FileSystemRights, [int]$rule.InheritanceFlags, $rule.AccessControlType, $rule.IsInherited
    }
    return [pscustomobject]@{ Protected = $acl.AreAccessRulesProtected; Rules = @($set | Sort-Object) }
}

function New-ExpectedRuleSet([bool]$isDirectory, [bool]$usersRead) {
    $full = [int][System.Security.AccessControl.FileSystemRights]::FullControl
    $readExecute = [int]([System.Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [System.Security.AccessControl.FileSystemRights]::Synchronize)
    $read = [int]([System.Security.AccessControl.FileSystemRights]::Read -bor [System.Security.AccessControl.FileSystemRights]::Synchronize)
    $inherit = if ($isDirectory) { [int]([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit) } else { 0 }
    $rules = @()
    if ($isDirectory) {
        $rules += @{ Sid = $script:SidSystem; Rights = $full }
        $rules += @{ Sid = $script:SidAdministrators; Rights = $full }
        if ($usersRead) { $rules += @{ Sid = $script:SidUsers; Rights = $readExecute } }
    }
    else {
        $rules += @{ Sid = $script:SidSystem; Rights = $read }
        $rules += @{ Sid = $script:SidAdministrators; Rights = $full }
    }
    $expected = @()
    foreach ($rule in $rules) { $expected += '{0}|{1}|{2}|Allow|False' -f $rule.Sid, $rule.Rights, $inherit }
    return @($expected | Sort-Object)
}

function Test-RuleSetsEqual($actual, [string[]]$expected) {
    if (-not $actual.Protected) { return $false }
    if ($actual.Rules.Count -ne $expected.Count) { return $false }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($actual.Rules[$i] -ne $expected[$i]) { return $false }
    }
    return $true
}

# Replaces the DACL with exactly the expected rules, inheritance off, and reads it back. Idempotent: when the
# ACL already matches nothing is written (an inheritable change on DataRoot would otherwise walk the whole
# game install). Directories: SYSTEM and Administrators full control, optionally Users read and execute.
# Files: SYSTEM read, Administrators full control.
function Set-ProtectedAcl([string]$path, [bool]$usersRead) {
    $item = Get-Item -LiteralPath $path -Force
    $isDirectory = $item.PSIsContainer
    $expected = New-ExpectedRuleSet $isDirectory $usersRead
    if (Test-RuleSetsEqual (Get-AclRuleSet $path) $expected) { return }

    $acl = Get-FileSystemSecurity $path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
        [void]$acl.RemoveAccessRuleAll($rule)
    }
    $inheritance = if ($isDirectory) { [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [System.Security.AccessControl.InheritanceFlags]::None }
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    foreach ($entry in $expected) {
        $parts = $entry.Split('|')
        $sid = New-Object System.Security.Principal.SecurityIdentifier $parts[0]
        $rights = [System.Security.AccessControl.FileSystemRights][int]$parts[1]
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $rights, $inheritance, $propagation, $allow)
        $acl.AddAccessRule($rule)
    }
    Set-FileSystemSecurity $path $acl

    $actual = Get-AclRuleSet $path
    if (-not (Test-RuleSetsEqual $actual $expected)) {
        throw "The ACL on '$path' did not read back as written.`nExpected: $($expected -join '; ')`nActual:   $($actual.Rules -join '; ') (protected: $($actual.Protected))"
    }
}

# The ACLs the plan applies on every run. Folders are created when missing.
function Set-InstallAcls([string]$installDir, [string]$dataRoot) {
    foreach ($dir in @($installDir, $dataRoot)) {
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-ProtectedAcl $dir $true
    }
    # Instances and Clusters hold the source INIs, the generated GameUserSettings.ini (admin password, RCON
    # password) and the RCON history, so only SYSTEM and administrators may read them. The service runs as
    # LocalSystem and launches the game servers itself, so nothing needs Users access there.
    foreach ($name in 'keys', 'Data', 'Exports', 'Backups\_app', 'Instances', 'Clusters') {
        $dir = Join-Path $dataRoot $name
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-ProtectedAcl $dir $false
    }
    # Backups itself follows DataRoot (browsable); only _app is locked down.
}

# ---- protected file writes ----------------------------------------------------------------------------

# Writes $bytes to $path through a temp file that carries the final ACL before any content lands in it, then
# File.Replace (atomic on NTFS, the old file becomes $path.bak) or File.Move when $path does not exist yet.
# Returns the .bak path when one was made, otherwise $null.
function Write-ProtectedFile([string]$path, [byte[]]$bytes) {
    $tmp = "$path.tmp"
    $bak = "$path.bak"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    New-Item -ItemType File -Path $tmp -Force | Out-Null
    Set-ProtectedAcl $tmp $false
    [System.IO.File]::WriteAllBytes($tmp, $bytes)
    $madeBackup = $false
    if (Test-Path -LiteralPath $path) {
        if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Force }
        [System.IO.File]::Replace($tmp, $path, $bak)
        $madeBackup = $true
    }
    else {
        [System.IO.File]::Move($tmp, $path)
    }
    # File.Replace keeps the replaced file's DACL; make sure the result carries ours either way.
    Set-ProtectedAcl $path $false
    if ($madeBackup) { return $bak }
    return $null
}

function Write-ProtectedTextFile([string]$path, [string]$text) {
    return Write-ProtectedFile $path ((New-Object System.Text.UTF8Encoding $false).GetBytes($text))
}

function Copy-ProtectedFile([string]$source, [string]$destination) {
    return Write-ProtectedFile $destination ([System.IO.File]::ReadAllBytes($source))
}

# Puts $path.bak back in place of $path (after a failed change) and removes the .bak.
function Restore-ProtectedFileBackup([string]$path) {
    $bak = "$path.bak"
    if (-not (Test-Path -LiteralPath $bak)) { return $false }
    if (Test-Path -LiteralPath $path) { [System.IO.File]::Replace($bak, $path, [NullString]::Value) }
    else { [System.IO.File]::Move($bak, $path) }
    Set-ProtectedAcl $path $false
    return $true
}

# ---- journal (pending transaction) --------------------------------------------------------------------

function Get-JournalPath([string]$dataRoot) { return Join-Path (Join-Path $dataRoot 'keys') $script:JournalFileName }

function Read-Journal([string]$dataRoot) {
    $path = Get-JournalPath $dataRoot
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Read-JsonFile $path
}

function Write-Journal([string]$dataRoot, $journal) {
    $path = Get-JournalPath $dataRoot
    $tmp = "$path.tmp"
    Write-Utf8File $tmp (ConvertTo-JsonText $journal)
    if (Test-Path -LiteralPath $path) { [System.IO.File]::Replace($tmp, $path, [NullString]::Value) }
    else { [System.IO.File]::Move($tmp, $path) }
}

function New-Journal([string]$dataRoot, [string]$operation, [string]$installDir, [string]$previousDir, [string]$dbCopyDir, [string]$settingsBak, [string]$pfxOld, [string]$pfxNew) {
    $journal = [ordered]@{
        operation   = $operation
        startedAt   = (Get-Date).ToUniversalTime().ToString('o')
        phase       = 'pending'
        installDir  = $installDir
        previousDir = $previousDir
        dbCopyDir   = $dbCopyDir
        settingsBak = $settingsBak
        pfxOld      = $pfxOld
        pfxNew      = $pfxNew
    }
    $object = [pscustomobject]$journal
    Write-Journal $dataRoot $object
    return $object
}

function Set-JournalPhase([string]$dataRoot, $journal, [string]$phase) {
    Set-JsonProperty $journal 'phase' $phase
    Write-Journal $dataRoot $journal
}

function Set-JournalValue([string]$dataRoot, $journal, [string]$name, $value) {
    Set-JsonProperty $journal $name $value
    Write-Journal $dataRoot $journal
}

function Remove-Journal([string]$dataRoot) {
    $path = Get-JournalPath $dataRoot
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

# True when the journal's phase is $phase or a later one.
function Test-PhaseReached($journal, [string]$phase) {
    $current = [string](Get-JsonProperty $journal 'phase')
    return [array]::IndexOf($script:PhaseOrder, $current) -ge [array]::IndexOf($script:PhaseOrder, $phase)
}

function Assert-NoPendingTransaction([string]$dataRoot) {
    $journal = Read-Journal $dataRoot
    if ($null -eq $journal) { return }
    throw "A previous '$(Get-JsonProperty $journal 'operation')' started $(Get-JsonProperty $journal 'startedAt') is still pending (phase '$(Get-JsonProperty $journal 'phase')', journal $(Get-JournalPath $dataRoot)).`nFinish it with install.ps1 -Verify, or undo it with install.ps1 -Rollback."
}

# ---- marker -------------------------------------------------------------------------------------------

function Get-MarkerPath([string]$installDir) { return Join-Path $installDir $script:MarkerFileName }

function Read-Marker([string]$installDir) {
    $path = Get-MarkerPath $installDir
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Read-JsonFile $path
}

function Write-Marker([string]$installDir, [string]$installId, [string]$serviceName, [string]$dataRoot, [string]$version) {
    $marker = [ordered]@{
        installId   = $installId
        serviceName = $serviceName
        dataRoot    = $dataRoot
        version     = $version
        installedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-Utf8File (Get-MarkerPath $installDir) (ConvertTo-JsonText ([pscustomobject]$marker))
}

# Deletes a swap folder (.new or .previous-*) only when its marker carries the expected installId.
function Remove-SwapFolder([string]$path, [string]$installId, [string]$dataRoot, [string]$zipDir) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    Assert-SwapPath $path 'Swap folder' $dataRoot $zipDir
    $marker = Read-Marker $path
    if ($null -eq $marker -or (Get-JsonProperty $marker 'installId') -ne $installId) {
        throw "'$path' has no install.json with installId $installId; it is not deleted. Move or delete it by hand."
    }
    Remove-Item -LiteralPath $path -Recurse -Force
}

# ---- settings file ------------------------------------------------------------------------------------

function Get-SettingsPath([string]$installDir) { return Join-Path $installDir $script:SettingsFileName }

# What the installer needs from an installed appsettings.Production.json.
function Get-InstalledSettings([string]$installDir) {
    $path = Get-SettingsPath $installDir
    if (-not (Test-Path -LiteralPath $path)) { throw "The installed settings file is missing: $path" }
    $raw = Read-JsonFile $path
    $ark = Get-JsonProperty $raw 'ArkAdmin'
    $dataRoot = Get-JsonProperty $ark 'DataRoot'
    if ([string]::IsNullOrWhiteSpace($dataRoot)) { throw "ArkAdmin:DataRoot is not set in $path; the installer cannot find the data folder." }
    $dataRoot = ConvertTo-CanonicalPath ([System.Environment]::ExpandEnvironmentVariables($dataRoot))
    $endpoints = Get-JsonProperty (Get-JsonProperty $raw 'Kestrel') 'Endpoints'
    $https = Get-JsonProperty $endpoints 'Https'
    $http = Get-JsonProperty $endpoints 'Http'
    $scheme = 'http'; $port = 5000; $certPath = $null; $certPassword = $null; $bindHost = '127.0.0.1'
    if ($https -and (Get-JsonProperty $https 'Url')) {
        $uri = [Uri](Get-JsonProperty $https 'Url')
        $scheme = 'https'; $port = $uri.Port; $bindHost = $uri.Host
        $certificate = Get-JsonProperty $https 'Certificate'
        $certPath = Get-JsonProperty $certificate 'Path'
        $certPassword = Get-JsonProperty $certificate 'Password'
    }
    elseif ($http -and (Get-JsonProperty $http 'Url')) {
        $uri = [Uri](Get-JsonProperty $http 'Url')
        $port = $uri.Port; $bindHost = $uri.Host
    }
    $bind = if ($scheme -eq 'https') { 'LanHttps' } elseif ($bindHost -in @('127.0.0.1', 'localhost', '::1')) { 'Loopback' } else { 'Proxy' }
    return [pscustomobject]@{
        Path         = $path
        Raw          = $raw
        DataRoot     = $dataRoot
        Scheme       = $scheme
        Port         = $port
        Bind         = $bind
        CertPath     = $certPath
        CertPassword = $certPassword
        KnownProxies = @(Get-JsonProperty $ark 'KnownProxies')
    }
}

# The Logging block from the dev deploy script: the event-log provider defaults to Warning, which hides
# readiness, launches, and stops; provider rules override the global ones, so the framework stays at Warning.
function New-LoggingSettings {
    return [ordered]@{
        LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning'; 'Microsoft.EntityFrameworkCore' = 'Warning' }
        EventLog = [ordered]@{ LogLevel = [ordered]@{ Default = 'Warning'; ArkAscendedServerAdmin = 'Information' } }
    }
}

function New-ProductionSettings([string]$dataRoot, [string]$passwordHash, [string]$bind, [int]$port, [string[]]$knownProxies, [string]$pfxPath, [string]$pfxPassword) {
    $endpoints = [ordered]@{}
    switch ($bind) {
        'Loopback' { $endpoints.Http = [ordered]@{ Url = "http://127.0.0.1:$port" } }
        'Proxy' { $endpoints.Http = [ordered]@{ Url = "http://0.0.0.0:$port" } }
        'LanHttps' {
            # The base appsettings.json declares Http on 127.0.0.1:5000 and configuration merging cannot
            # delete it, so it is set explicitly to the port below the HTTPS one.
            $endpoints.Http = [ordered]@{ Url = "http://127.0.0.1:$($port - 1)" }
            $endpoints.Https = [ordered]@{
                Url         = "https://0.0.0.0:$port"
                Certificate = [ordered]@{ Path = $pfxPath; Password = $pfxPassword }
            }
        }
        default { throw "Unknown bind mode '$bind'." }
    }
    return [pscustomobject][ordered]@{
        ArkAdmin = [ordered]@{
            DataRoot          = $dataRoot
            PasswordHash      = $passwordHash
            AllowInsecureHttp = $false
            KnownProxies      = @($knownProxies)
        }
        Kestrel  = [ordered]@{ Endpoints = $endpoints }
        Logging  = New-LoggingSettings
    }
}

# ---- runtime check ------------------------------------------------------------------------------------

function Assert-AspNetCoreRuntime {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    $runtimes = @()
    if ($dotnet) {
        $runtimes = @(& $dotnet.Source --list-runtimes 2>$null)
    }
    $match = @($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' -and $_ -notmatch '\(x86\)' })
    if ($match.Count -eq 0) {
        throw "This is the framework-dependent package and no x64 ASP.NET Core 10 runtime was found (dotnet --list-runtimes). Install the ASP.NET Core Runtime 10 (x64) from $($script:RuntimeDownloadUrl) or use the self-contained zip."
    }
}

# ---- password hashing ---------------------------------------------------------------------------------

function ConvertTo-PlainText([securestring]$secure) {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# Runs `<exe> --hash-password` with the password on its standard input as UTF-8 bytes plus one '\n'. The
# plaintext is never an argument and never written to disk. StandardInputEncoding is not used because
# .NET Framework's ProcessStartInfo (Windows PowerShell 5.1) does not have it.
function Get-PasswordHash([string]$exePath, [securestring]$password) {
    if (-not (Test-Path -LiteralPath $exePath)) { throw "Executable not found: $exePath" }
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exePath
    $startInfo.Arguments = '--hash-password'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = Split-Path $exePath -Parent
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-PlainText $password) + "`n")
        $stdin = $process.StandardInput.BaseStream
        $stdin.Write($bytes, 0, $bytes.Length)
        $stdin.Flush()
        $stdin.Close()
        [array]::Clear($bytes, 0, $bytes.Length)
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if (-not $process.WaitForExit(60000)) {
            try { $process.Kill() } catch { }
            throw "$($script:ExeName) --hash-password did not finish within 60 s."
        }
        if ($process.ExitCode -ne 0) {
            throw "$($script:ExeName) --hash-password exited with code $($process.ExitCode). $($stderr.Trim())"
        }
    }
    finally {
        $process.Dispose()
    }
    $lines = @($stdout -split "`r?`n" | Where-Object { $_ -ne '' })
    if ($lines.Count -ne 1 -or $lines[0] -notmatch '^pbkdf2\$\d+\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$') {
        throw "$($script:ExeName) --hash-password did not print exactly one pbkdf2`$ line."
    }
    return $lines[0]
}

# ---- certificates -------------------------------------------------------------------------------------

function New-RandomPassword {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

# Every non-loopback, non-link-local IPv4 address on an interface that is up.
function Get-LanIPv4Addresses {
    $result = @()
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne [System.Net.NetworkInformation.OperationalStatus]::Up) { continue }
        if ($nic.NetworkInterfaceType -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback) { continue }
        foreach ($unicast in $nic.GetIPProperties().UnicastAddresses) {
            $address = $unicast.Address
            if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { continue }
            $text = $address.ToString()
            if ($text.StartsWith('169.254.') -or $text.StartsWith('127.')) { continue }
            if ($result -notcontains $text) { $result += $text }
        }
    }
    return $result
}

# Self-signed server certificate for the LanHttps bind: DNS entries for the machine name, IP entries for
# every LAN IPv4 address (IP addresses are not DNS names), one year, RSA 2048. Built in memory with
# CertificateRequest (works the same in Windows PowerShell 5.1 and pwsh 7, and never touches a certificate
# store) and written straight to the PFX. Returns the thumbprint.
function New-SelfSignedWebCertificate([string]$pfxPath, [string]$pfxPassword) {
    $machine = [System.Environment]::MachineName
    $dnsNames = @($machine.ToLowerInvariant())
    try {
        $fqdn = [System.Net.Dns]::GetHostEntry('').HostName
        if ($fqdn -and $dnsNames -notcontains $fqdn.ToLowerInvariant()) { $dnsNames += $fqdn.ToLowerInvariant() }
    }
    catch { }
    $addresses = Get-LanIPv4Addresses

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $subject = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName ("CN=$machine")
        $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
            $subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = New-Object System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
        foreach ($name in $dnsNames) { $san.AddDnsName($name) }
        foreach ($address in $addresses) { $san.AddIpAddress([System.Net.IPAddress]::Parse($address)) }
        $request.CertificateExtensions.Add($san.Build($false))
        $keyUsage = New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment), $true)
        $request.CertificateExtensions.Add($keyUsage)
        $serverAuth = New-Object System.Security.Cryptography.OidCollection
        [void]$serverAuth.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($serverAuth, $false)))
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $false)))
        $notBefore = [System.DateTimeOffset]::UtcNow.AddMinutes(-5)
        $certificate = $request.CreateSelfSigned($notBefore, $notBefore.AddYears(1))
        try {
            $pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword)
            [System.IO.File]::WriteAllBytes($pfxPath, $pfx)
            return $certificate.Thumbprint
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }
}

# Re-packages a user's PFX under a fresh random password (so the settings never hold the user's own) and
# returns the thumbprint.
function Import-UserCertificate([string]$sourcePfx, [string]$sourcePassword, [string]$pfxPath, [string]$pfxPassword) {
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($sourcePfx, $sourcePassword, $flags)
    try {
        if (-not $certificate.HasPrivateKey) { throw "'$sourcePfx' has no private key; Kestrel needs one." }
        $pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword)
        [System.IO.File]::WriteAllBytes($pfxPath, $pfx)
        return $certificate.Thumbprint
    }
    finally {
        $certificate.Dispose()
    }
}

function Get-PfxThumbprint([string]$pfxPath, [string]$pfxPassword) {
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pfxPassword, $flags)
    try { return $certificate.Thumbprint } finally { $certificate.Dispose() }
}

# ---- firewall -----------------------------------------------------------------------------------------

function Get-WebFirewallRuleName([string]$serviceName) { return "$serviceName-Web" }

# Replaces the web rule so a changed port or source takes effect. $remote: 'LocalSubnet', a list of
# addresses, or 'Any'.
function Set-WebFirewallRule([string]$serviceName, [int]$port, [string[]]$remote, [string]$displayName) {
    $name = Get-WebFirewallRuleName $serviceName
    Remove-WebFirewallRule $serviceName
    New-NetFirewallRule -Name $name -DisplayName $displayName -Direction Inbound -Protocol TCP -LocalPort $port `
        -RemoteAddress $remote -Action Allow -Profile Any -ErrorAction Stop | Out-Null
}

function Remove-WebFirewallRule([string]$serviceName) {
    $name = Get-WebFirewallRuleName $serviceName
    if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -Name $name -ErrorAction Stop
    }
}

# The app names each instance's two UDP rules ArkAscendedServerAdmin-<tag>-<instanceId>, where the tag (8
# lowercase hex characters) comes from its DataRoot, and writes that tag to firewall.tag beside its binaries
# on every start. The file is the only way the scripts learn the tag: they never derive it from the settings,
# because they cannot resolve DataRoot the way the service account did, and a wrong guess (or a wildcard)
# would select another installation's rules.
function Test-InstanceFirewallTag([string]$tag) {
    return $tag.Length -eq 8 -and $tag -cmatch '^[0-9a-f]{8}$'
}

# The trimmed text of a firewall.tag, or $null when it is missing or cannot be read.
function Get-InstanceFirewallTagText([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return ([System.IO.File]::ReadAllText($path)).Trim() } catch { return $null }
}

# The tag recorded in $installDir\firewall.tag, or $null with a warning when the file is missing, empty, or
# holds anything but exactly 8 lowercase hex characters.
function Read-InstanceFirewallTag([string]$installDir) {
    $path = Join-Path $installDir $script:FirewallTagFileName
    $tag = Get-InstanceFirewallTagText $path
    if (Test-InstanceFirewallTag $tag) { return $tag }
    $problem = if ($null -eq $tag) { 'is missing or unreadable' } else { 'does not hold an installation tag (8 lowercase hex characters)' }
    Write-Warning "$path $problem, so the instance firewall rules were left in place. They are named $($script:InstanceRulePrefix)<tag>-<instance id>; remove them by hand if you no longer need them."
    return $null
}

# Removes this installation's instance rules, and only those. The name the app sets through the firewall COM
# API is the DisplayName in NetSecurity terms (-Name is the rule id), so the selection is by -DisplayName.
function Remove-InstanceFirewallRules([string]$tag) {
    if (-not (Test-InstanceFirewallTag $tag)) { throw "'$tag' is not an installation tag (8 lowercase hex characters); no firewall rule was removed." }
    $pattern = "$($script:InstanceRulePrefix)$tag-*"
    $lookupErrors = $null
    $rules = @(Get-NetFirewallRule -DisplayName $pattern -ErrorAction SilentlyContinue -ErrorVariable lookupErrors)
    foreach ($lookupError in @($lookupErrors | Where-Object { $null -ne $_ })) {
        if ($lookupError.CategoryInfo.Category -ne 'ObjectNotFound') { throw $lookupError }
    }
    if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -ErrorAction Stop }
    Write-Host "$($rules.Count) instance firewall rule(s) removed ($pattern)."
}

# An upgrade swaps the whole application folder; the tag goes across with the settings so an upgrade with
# -NoStart, or a first start that fails, still leaves uninstall.ps1 its record of which rules to remove.
function Copy-InstanceFirewallTag([string]$fromDir, [string]$toDir) {
    $path = Join-Path $fromDir $script:FirewallTagFileName
    if (-not (Test-Path -LiteralPath $path)) { return }
    $tag = Get-InstanceFirewallTagText $path
    if (-not (Test-InstanceFirewallTag $tag)) {
        Write-Warning "$path does not hold an installation tag (8 lowercase hex characters); it was not copied. The app writes a new one when it starts."
        return
    }
    Write-Utf8File (Join-Path $toDir $script:FirewallTagFileName) "$tag`n"
}

# ---- HTTP probe ---------------------------------------------------------------------------------------

# A static validation callback compiled once: a PowerShell script block cannot be invoked as the TLS
# callback (it runs on a thread without a runspace), so the thumbprint pin lives in C#. Only certificates
# whose thumbprint matches the pin pass; the name and the chain are irrelevant for a self-signed
# certificate reached on 127.0.0.1.
function Initialize-CertificatePin {
    if (-not ('ArkInstall.CertificatePin' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ArkInstall
{
    public static class CertificatePin
    {
        public static string Thumbprint;

        public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null || string.IsNullOrEmpty(Thumbprint)) { return false; }
            string actual = new X509Certificate2(certificate).Thumbprint;
            return string.Equals(actual, Thumbprint, StringComparison.OrdinalIgnoreCase);
        }

        public static RemoteCertificateValidationCallback Callback
        {
            get { return new RemoteCertificateValidationCallback(Validate); }
        }
    }
}
'@
    }
}

# One request without automatic redirects. HTTP error statuses are returned, not thrown (WebException with
# a response is unwrapped; -SkipHttpErrorCheck does not exist in Windows PowerShell 5.1). For https the
# per-request validation callback pins the thumbprint; ServicePointManager's process-global callback is
# also set for the duration of the call and reset in finally, because .NET Framework consults it as well.
function Invoke-RawHttp([string]$method, [string]$url, [hashtable]$headers, [string]$body, [string]$thumbprint, [int]$timeoutMs = 5000) {
    $request = [System.Net.HttpWebRequest]::Create($url)
    $request.Method = $method
    $request.AllowAutoRedirect = $false
    $request.Timeout = $timeoutMs
    $request.ReadWriteTimeout = $timeoutMs
    $request.KeepAlive = $false
    $request.UserAgent = 'ArkAscendedServerAdmin-installer'
    if ($headers) { foreach ($key in $headers.Keys) { $request.Headers.Add($key, [string]$headers[$key]) } }
    $previousCallback = $null
    $pinned = $false
    if ($url.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not $thumbprint) { throw "An HTTPS probe needs the certificate thumbprint to pin." }
        Initialize-CertificatePin
        [ArkInstall.CertificatePin]::Thumbprint = $thumbprint
        $request.ServerCertificateValidationCallback = [ArkInstall.CertificatePin]::Callback
        $previousCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = [ArkInstall.CertificatePin]::Callback
        $pinned = $true
    }
    try {
        # A [string] parameter turns $null into ''; only a POST carries a body.
        if ($method -eq 'POST') {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$body)
            $request.ContentType = 'application/x-www-form-urlencoded'
            $request.ContentLength = $bytes.Length
            $stream = $request.GetRequestStream()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        $response = $null
        try {
            $response = $request.GetResponse()
        }
        catch [System.Net.WebException] {
            if ($null -eq $_.Exception.Response) { throw }
            $response = $_.Exception.Response
        }
        try {
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $setCookies = @()
            try { $setCookies = @($response.Headers.GetValues('Set-Cookie')) } catch { }
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Body       = $text
                Location   = [string]$response.Headers['Location']
                SetCookies = $setCookies
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        if ($pinned) {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
            [ArkInstall.CertificatePin]::Thumbprint = $null
        }
    }
}

# The process listening on the port must be the service's own process (not some other program on the port).
function Test-PortOwnedByProcess([int]$port, [int]$processId) {
    if ($processId -le 0) { return $false }
    $owners = @()
    try {
        $owners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop | ForEach-Object { [int]$_.OwningProcess })
    }
    catch {
        foreach ($line in (& netstat.exe -ano -p TCP 2>$null)) {
            if ($line -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$' -and [int]$Matches[1] -eq $port) { $owners += [int]$Matches[2] }
        }
    }
    return $owners -contains $processId
}

# Polls /healthz until it answers 200 with the expected body from a socket the service owns, or the timeout
# passes. Returns an object with Success and Reason.
function Wait-HealthProbe([string]$scheme, [int]$port, [string]$thumbprint, [string]$serviceName, [int]$timeoutSeconds = 60) {
    $url = "${scheme}://127.0.0.1:$port$($script:HealthPath)"
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $reason = 'no response'
    while ((Get-Date) -lt $deadline) {
        $processId = Get-ServiceProcessId $serviceName
        $result = $null
        try { $result = Invoke-RawHttp 'GET' $url @{ 'X-Forwarded-Proto' = 'https' } $null $thumbprint }
        catch { $reason = $_.Exception.Message }
        if ($result) {
            if ($result.StatusCode -ne 200) { $reason = "HTTP $($result.StatusCode) from $url" }
            elseif ($result.Body.Trim() -ne $script:HealthBody) { $reason = "HTTP 200 from $url but the body is not '$($script:HealthBody)'" }
            elseif (-not (Test-PortOwnedByProcess $port $processId)) { $reason = "port $port answered but is not owned by the service process (pid $processId)" }
            else { return [pscustomobject]@{ Success = $true; Reason = ''; Url = $url } }
        }
        Start-Sleep -Seconds 2
    }
    return [pscustomobject]@{ Success = $false; Reason = "$reason (after $timeoutSeconds s)"; Url = $url }
}

# GET /login, then POST the password with the antiforgery cookie and form token echoed. X-Forwarded-Proto
# is sent because loopback is a trusted proxy and the HTTPS guard must see HTTPS. Success is a 302 to /
# with a Set-Cookie for the auth cookie; a re-rendered form (200) means the password was not adopted.
function Test-LoginRoundTrip([string]$scheme, [int]$port, [string]$thumbprint, [securestring]$password) {
    $base = "${scheme}://127.0.0.1:$port"
    $headers = @{ 'X-Forwarded-Proto' = 'https' }
    $get = Invoke-RawHttp 'GET' "$base/login" $headers $null $thumbprint 15000
    if ($get.StatusCode -ne 200) { throw "GET /login answered HTTP $($get.StatusCode) instead of 200." }
    $token = $null
    if ($get.Body -match 'name="__RequestVerificationToken"[^>]*\svalue="([^"]+)"') { $token = $Matches[1] }
    elseif ($get.Body -match 'value="([^"]+)"[^>]*\sname="__RequestVerificationToken"') { $token = $Matches[1] }
    if (-not $token) { throw 'GET /login returned no antiforgery form token; the login form may have changed.' }
    # Cookies are echoed by hand: the antiforgery cookie is marked Secure once the request counts as HTTPS,
    # and a CookieContainer would drop it for an http:// URL.
    $cookies = @()
    foreach ($setCookie in $get.SetCookies) {
        $pair = $setCookie.Split(';')[0].Trim()
        if ($pair -match '^([^=\s]+)=(.*)$' -and $Matches[1].StartsWith('.AspNetCore.Antiforgery', [System.StringComparison]::OrdinalIgnoreCase)) { $cookies += $pair }
    }
    if ($cookies.Count -eq 0) { throw 'GET /login set no antiforgery cookie; the login form may have changed.' }
    $postHeaders = @{ 'X-Forwarded-Proto' = 'https'; 'Cookie' = ($cookies -join '; ') }
    $plain = ConvertTo-PlainText $password
    $body = '_handler=login&__RequestVerificationToken={0}&Input.Password={1}' -f [Uri]::EscapeDataString($token), [Uri]::EscapeDataString($plain)
    $post = Invoke-RawHttp 'POST' "$base/login" $postHeaders $body $thumbprint 15000
    if ($post.StatusCode -ne 302) { return $false }
    $location = $post.Location
    $path = if ($location -match '^https?://') { ([Uri]$location).AbsolutePath } else { $location }
    if ($path -ne '/') { return $false }
    $authCookie = @($post.SetCookies | Where-Object { $_.StartsWith("$($script:AuthCookieName)=", [System.StringComparison]::OrdinalIgnoreCase) })
    return $authCookie.Count -gt 0
}

# ---- event log ----------------------------------------------------------------------------------------

function Get-AppEventLogTail([string]$serviceName, [int]$count = 50) {
    $sources = @($script:EventSources)
    if ($sources -notcontains $serviceName) { $sources += $serviceName }
    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $sources } -MaxEvents $count -ErrorAction Stop)
    }
    catch { return @("(no Application event-log entries from $($sources -join ', '))") }
    $lines = @()
    foreach ($entry in ($events | Sort-Object TimeCreated)) {
        $message = [string]$entry.Message
        $lines += '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f $entry.TimeCreated, $entry.LevelDisplayName, $message
    }
    return $lines
}

function Write-EventLogTail([string]$serviceName) {
    Write-Host "`nLast Application event-log lines from the app:" -ForegroundColor Yellow
    foreach ($line in (Get-AppEventLogTail $serviceName 50)) { Write-Host "  $line" }
}

# ---- upgrade helpers ----------------------------------------------------------------------------------

function Get-Timestamp { return (Get-Date).ToString('yyyyMMdd-HHmmss') }

# Copies the staging folder (this zip) into $destination. robocopy: no ACL copy (the destination's ACL
# inherits down), exit codes below 8 are success.
function Copy-PackageFiles([string]$source, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & robocopy.exe $source $destination /E /NJH /NJS /NDL /NFL /NP /R:2 /W:2 /XF $script:SettingsFileName "$($script:SettingsFileName).bak" "$($script:SettingsFileName).tmp" $script:MarkerFileName $script:FirewallTagFileName | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy from '$source' to '$destination' failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $destination $script:ExeName))) { throw "The copy to '$destination' has no $($script:ExeName)." }
}

# The database file set (.db, -wal, -shm) copied while the service is stopped; only then is a raw copy consistent.
function Copy-DatabaseFiles([string]$dataRoot, [string]$destination) {
    $data = Join-Path $dataRoot 'Data'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-ProtectedAcl $destination $false
    if (-not (Test-Path -LiteralPath $data)) { return 0 }
    $files = @(Get-ChildItem -LiteralPath $data -File)
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force }
    return $files.Count
}

# Puts a copied database file set back, whole: any newer -wal/-shm is deleted first so SQLite does not pair
# an old .db with a newer journal.
function Restore-DatabaseFiles([string]$dataRoot, [string]$source) {
    if (-not (Test-Path -LiteralPath $source)) { return }
    $data = Join-Path $dataRoot 'Data'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Get-ChildItem -LiteralPath $data -File | Where-Object { $_.Name -like '*.db-wal' -or $_.Name -like '*.db-shm' -or $_.Name -like '*.db' } | Remove-Item -Force
    foreach ($file in Get-ChildItem -LiteralPath $source -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $data $file.Name) -Force }
}

# Deletes every key-*.xml in DataRoot\keys that is not DPAPI-protected (no encryptedSecret element); a
# protected key ring is issued by the app on start. Returns the number deleted.
function Remove-UnprotectedKeys([string]$dataRoot) {
    $keys = Join-Path $dataRoot 'keys'
    if (-not (Test-Path -LiteralPath $keys)) { return 0 }
    $deleted = 0
    foreach ($file in Get-ChildItem -LiteralPath $keys -File -Filter 'key-*.xml') {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -notmatch '<encryptedSecret') {
            Remove-Item -LiteralPath $file.FullName -Force
            $deleted++
        }
    }
    return $deleted
}

# Older recovery sets are pruned only after a transaction verified; the newest of each kind is kept.
function Remove-OldRecoverySets([string]$installDir, [string]$installId, [string]$dataRoot, [string]$zipDir, [string]$keepPrevious, [string]$keepDbCopy) {
    $parent = Split-Path $installDir -Parent
    $leaf = Split-Path $installDir -Leaf
    $previous = @(Get-ChildItem -LiteralPath $parent -Directory -Filter "$leaf.previous-*" -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($folder in $previous) {
        if ($keepPrevious -and (Test-PathEquals $folder.FullName $keepPrevious)) { continue }
        $marker = Read-Marker $folder.FullName
        if ($null -eq $marker -or (Get-JsonProperty $marker 'installId') -ne $installId) { continue }
        Write-Host "Removing older recovery copy $($folder.FullName)"
        Remove-SwapFolder $folder.FullName $installId $dataRoot $zipDir
    }
    $appBackups = Join-Path (Join-Path $dataRoot 'Backups') '_app'
    if (Test-Path -LiteralPath $appBackups) {
        foreach ($folder in @(Get-ChildItem -LiteralPath $appBackups -Directory | Sort-Object Name)) {
            if ($keepDbCopy -and (Test-PathEquals $folder.FullName $keepDbCopy)) { continue }
            Write-Host "Removing older database copy $($folder.FullName)"
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force
        }
    }
}

# ---- environment override check ----------------------------------------------------------------------

# ArkAdmin__Password / ArkAdmin__PasswordHash in the service's Environment registry value or the machine
# environment override the settings file, so a password written to the file would not take effect.
function Get-PasswordEnvironmentOverride([string]$serviceName) {
    $names = @('ArkAdmin__Password', 'ArkAdmin__PasswordHash', 'ArkAdmin:Password', 'ArkAdmin:PasswordHash')
    foreach ($name in $names) {
        if ([System.Environment]::GetEnvironmentVariable($name, 'Machine')) { return "machine environment variable $name" }
    }
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $environment = @()
    try { $environment = @((Get-ItemProperty -Path $key -Name 'Environment' -ErrorAction Stop).Environment) } catch { }
    foreach ($entry in $environment) {
        foreach ($name in $names) {
            if ($entry -like "$name=*") { return "the service's Environment registry value ($name)" }
        }
    }
    return $null
}

# ---- package ------------------------------------------------------------------------------------------

function Read-PackageInfo([string]$zipDir) {
    $path = Join-Path $zipDir $script:PackageFileName
    if (-not (Test-Path -LiteralPath $path)) { throw "package.json not found next to this script; run install.ps1 from the extracted release zip." }
    $package = Read-JsonFile $path
    $version = [string](Get-JsonProperty $package 'version')
    if (-not $version) { throw "package.json has no version." }
    $selfContained = [bool](Get-JsonProperty $package 'selfContained')
    if (-not (Test-Path -LiteralPath (Join-Path $zipDir $script:ExeName))) { throw "$($script:ExeName) not found next to this script; run install.ps1 from the extracted release zip." }
    return [pscustomobject]@{ Version = $version; SelfContained = $selfContained }
}

function Get-ExeProductVersion([string]$exePath) {
    try {
        $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
        if ($version) { return ($version -replace '\+.*$', '') }
    }
    catch { }
    return 'unknown'
}
