#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Ark Ascended Server Admin Windows service and its program folder. DataRoot is left alone.

.DESCRIPTION
    Refuses unless InstallDir\install.json exists and the service's binary path points into InstallDir.
    Stops the service, removes the web firewall rule and this installation's instance firewall rules, deletes
    the service, then deletes InstallDir and every InstallDir.previous-* whose marker carries the same
    installId. The data folder (worlds, backups, keys, database, certificate) stays; it is yours.

    The instance rules are found by the tag the app records in InstallDir\firewall.tag. When that file is
    missing or invalid the instance rules are left in place with a warning; other installations' rules are
    never touched. If the firewall step fails, the service is stopped but still registered, so running the
    script again finishes the job.

.PARAMETER InstallDir
    Optional; derived from the service when omitted, and must match it when given.

.PARAMETER Quiet
    No confirmation prompt.

.PARAMETER ServiceName
    Testing parameter: the Windows service name (default ArkAscendedServerAdmin).

.PARAMETER SimulateFirewallFailure
    Testing parameter: throws inside the firewall step, after the service is stopped and before it is deleted,
    so the stop-then-retry path can be exercised on a scratch install.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Quiet,
    [string]$ServiceName = 'ArkAscendedServerAdmin',
    [switch]$SimulateFirewallFailure
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArkInstall.Common.ps1')

$zipDir = ConvertTo-CanonicalPath $PSScriptRoot
$mutex = $null
try {
    $mutex = Enter-ArkInstallMutex

    $service = Get-ArkService $ServiceName
    if ($null -eq $service) { throw "Service '$ServiceName' is not installed on this machine." }
    # Variable names are case-insensitive in PowerShell, so the local $installDir below IS the -InstallDir
    # parameter; what the caller asked for has to be captured before the service's own folder overwrites it.
    $requestedInstallDir = $InstallDir
    $installDir = Get-ServiceBinaryDir $service
    if ($requestedInstallDir -and -not (Test-PathEquals (ConvertTo-CanonicalPath $requestedInstallDir) $installDir)) {
        throw "Service '$ServiceName' runs from '$installDir', not from '$(ConvertTo-CanonicalPath $requestedInstallDir)'."
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
    $firewallTag = Read-InstanceFirewallTag $installDir
    if ($firewallTag) { Write-Host "Firewall: $(Get-WebFirewallRuleName $ServiceName) and the instance rules $($script:InstanceRulePrefix)$firewallTag-*" }
    else { Write-Host "Firewall: $(Get-WebFirewallRuleName $ServiceName) only" }
    if (-not $Quiet) {
        $answer = Read-Host -Prompt 'Remove the service and the app folder(s) above? DataRoot is kept. (y/N)'
        if ($answer -notin @('y', 'Y', 'yes')) { Write-Host 'Nothing removed.'; return }
    }

    Write-Step "Stop service $ServiceName"
    Stop-ArkService $ServiceName

    # Before the service is deleted: if this step fails, the service is still registered and a second run
    # of this script passes the check at the top and finishes the job.
    Write-Step 'Firewall rules'
    if ($SimulateFirewallFailure) { throw 'Simulated firewall failure (-SimulateFirewallFailure). The service is stopped but still registered; run uninstall.ps1 again without the switch.' }
    Remove-WebFirewallRule $ServiceName
    if ($firewallTag) { Remove-InstanceFirewallRules $firewallTag }

    Write-Step "Delete service $ServiceName"
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete returned exit code $LASTEXITCODE." }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }

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
