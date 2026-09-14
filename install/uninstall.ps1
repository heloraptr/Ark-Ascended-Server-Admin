#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Ark Ascended Server Admin Windows service and its program folder. DataRoot is left alone.

.DESCRIPTION
    Refuses unless InstallDir\install.json exists and the service's binary path points into InstallDir.
    Stops and deletes the service, removes the web firewall rule, deletes InstallDir and every
    InstallDir.previous-* whose marker carries the same installId. The data folder (worlds, backups, keys,
    database, certificate) stays; it is yours.

.PARAMETER InstallDir
    Optional; derived from the service when omitted, and must match it when given.

.PARAMETER Quiet
    No confirmation prompt.

.PARAMETER ServiceName
    Testing parameter: the Windows service name (default ArkAscendedServerAdmin).
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Quiet,
    [string]$ServiceName = 'ArkAscendedServerAdmin'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArkInstall.Common.ps1')

$zipDir = ConvertTo-CanonicalPath $PSScriptRoot
$mutex = $null
try {
    $mutex = Enter-ArkInstallMutex

    $service = Get-ArkService $ServiceName
    if ($null -eq $service) { throw "Service '$ServiceName' is not installed on this machine." }
    $installDir = Get-ServiceBinaryDir $service
    if ($InstallDir -and -not (Test-PathEquals (ConvertTo-CanonicalPath $InstallDir) $installDir)) {
        throw "Service '$ServiceName' runs from '$installDir', not from '$(ConvertTo-CanonicalPath $InstallDir)'."
    }
    $marker = Read-Marker $installDir
    if ($null -eq $marker) { throw "$installDir has no install.json; this installation was not made by install.ps1. Remove it by hand (sc.exe delete $ServiceName)." }
    if ((Get-JsonProperty $marker 'serviceName') -ne $ServiceName) { throw "$installDir\install.json belongs to service '$(Get-JsonProperty $marker 'serviceName')', not '$ServiceName'." }
    $installId = [string](Get-JsonProperty $marker 'installId')
    $dataRoot = [string](Get-JsonProperty $marker 'dataRoot')
    try { $dataRoot = (Get-InstalledSettings $installDir).DataRoot } catch { }
    if ($dataRoot) { $dataRoot = ConvertTo-CanonicalPath $dataRoot }
    Assert-ManagedPath $installDir 'InstallDir' $zipDir

    $parent = Split-Path $installDir -Parent
    $leaf = Split-Path $installDir -Leaf
    $previous = @(Get-ChildItem -LiteralPath $parent -Directory -Filter "$leaf.previous-*" -ErrorAction SilentlyContinue | Where-Object {
            $m = Read-Marker $_.FullName
            $null -ne $m -and (Get-JsonProperty $m 'installId') -eq $installId
        })

    Write-Host "Service : $ServiceName ($($service.State))"
    Write-Host "App     : $installDir"
    foreach ($folder in $previous) { Write-Host "          $($folder.FullName) (previous version)" }
    Write-Host "DataRoot: $dataRoot (kept)"
    if (-not $Quiet) {
        $answer = Read-Host -Prompt 'Remove the service and the app folder(s) above? DataRoot is kept. (y/N)'
        if ($answer -notin @('y', 'Y', 'yes')) { Write-Host 'Nothing removed.'; return }
    }

    Write-Step "Stop and delete service $ServiceName"
    Stop-ArkService $ServiceName
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete returned exit code $LASTEXITCODE." }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }

    Write-Step 'Firewall rule'
    Remove-WebFirewallRule $ServiceName

    Write-Step "Delete $installDir"
    Remove-Item -LiteralPath $installDir -Recurse -Force
    foreach ($folder in $previous) {
        Write-Host "Delete $($folder.FullName)"
        Remove-SwapFolder $folder.FullName $installId $dataRoot $zipDir
    }
    if ($dataRoot -and (Test-Path -LiteralPath (Get-JournalPath $dataRoot))) { Remove-Journal $dataRoot }

    Write-Host ''
    Write-Host "Uninstalled. $dataRoot was left in place: the game install, instances, backups, keys, database, and certificate are yours to keep or delete." -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Exit-ArkInstallMutex $mutex
}
