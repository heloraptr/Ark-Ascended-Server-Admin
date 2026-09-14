<#
.SYNOPSIS
    Builds, tests, publishes, and zips the two release packages of Ark Ascended Server Admin.

.DESCRIPTION
    Release plan step B11. Runs under Windows PowerShell 5.1 and pwsh 7. Output goes to artifacts\release\
    (gitignored):

      ArkAscendedServerAdmin-<version>-win-x64.zip       self-contained (no runtime needed on the box)
      ArkAscendedServerAdmin-<version>-win-x64-fdd.zip   framework-dependent (needs the ASP.NET Core 10 runtime)
      SHA256SUMS                                          both hashes, sha256sum format
      TestResults\*.trx                                   the two test runs

    <version> is MinVer's version without build metadata (the git-tag version, `1.0.0-rc.1` for a tag
    `v1.0.0-rc.1`). Each zip carries install.ps1, uninstall.ps1, INSTALL.md, LICENSE, THIRD-PARTY-NOTICES.md,
    and a package.json describing the package.

.PARAMETER SkipTests
    Publish without running the two test executables.

.PARAMETER OutputDir
    Where the zips land. Default: <repo>\artifacts\release.

.OUTPUTS
    The full MinVer version (for example 1.0.0-rc.1) is the last line written to the pipeline, so a caller
    can capture it: $version = (& build\publish-release.ps1)[-1]
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts\release' }
$configuration = 'Release'
$solution = Join-Path $repoRoot 'ArkAscendedServerAdmin.slnx'
$serverProject = Join-Path $repoRoot 'src\ArkAscendedServerAdmin.Server\ArkAscendedServerAdmin.Server.csproj'
$unitTests = Join-Path $repoRoot 'test\ArkAscendedServerAdmin.UnitTests\ArkAscendedServerAdmin.UnitTests.csproj'
$integrationTests = Join-Path $repoRoot 'test\ArkAscendedServerAdmin.Infrastructure.IntegrationTests\ArkAscendedServerAdmin.Infrastructure.IntegrationTests.csproj'
$installDir = Join-Path $repoRoot 'install'
$exeName = 'ArkAscendedServerAdmin.Server.exe'
$packageBaseName = 'ArkAscendedServerAdmin'

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# $ErrorActionPreference does not see native exit codes; every native command goes through here.
function Invoke-Checked([string]$description, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$description failed with exit code $LASTEXITCODE." }
}

# The version without SemVer build metadata (everything from '+' on). Zip names and the tag check use it.
function Remove-BuildMetadata([string]$version) {
    return ($version -replace '\+.*$', '').Trim()
}

function Get-MinVerVersion {
    # -getProperty alone only evaluates the project; MinVer sets MinVerVersion from its target, so the
    # target has to run first. -restore because the MinVer package is what defines the target.
    $output = & dotnet msbuild $serverProject -restore -t:MinVer -getProperty:MinVerVersion -nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dotnet msbuild -t:MinVer failed with exit code ${LASTEXITCODE}:`n$($output -join "`n")"
        return $null
    }
    $lines = @($output | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    if ($lines.Count -eq 0) { return $null }
    $candidate = $lines[-1]
    # SemVer 2.0 core, optional pre-release, optional build metadata.
    if ($candidate -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
        Write-Warning "MinVerVersion came back as '$candidate', which is not a SemVer version."
        return $null
    }
    return $candidate
}

# Fallback: the published exe's ProductVersion carries AssemblyInformationalVersion (MinVer's version plus
# the SDK's '+<sha>' suffix).
function Get-ProductVersion([string]$exePath) {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    return $info.ProductVersion
}

# Compress-Archive is used when it handles the folder; [IO.Compression.ZipFile] is the fallback (Windows
# PowerShell 5.1's Compress-Archive has limits on large inputs and long paths). Either way the archive holds
# the folder's contents at the root, not the folder itself.
function New-Zip([string]$sourceDir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    try {
        Compress-Archive -Path (Join-Path $sourceDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    }
    catch {
        Write-Warning "Compress-Archive failed ($($_.Exception.Message)); falling back to System.IO.Compression.ZipFile."
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($sourceDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    }

    # Read the archive back: every file in the staging folder must be an entry, with forward-slash names.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = @(Get-ChildItem $sourceDir -File -Recurse | ForEach-Object { $_.FullName.Substring($sourceDir.Length).TrimStart('\', '/') -replace '\\', '/' })
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }
    $missing = @($expected | Where-Object { $entries -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Zip $zipPath is missing $($missing.Count) file(s), for example '$($missing[0])'."
    }
}

function New-Package([string]$name, [string[]]$publishArgs, [bool]$selfContained, [string]$version) {
    $staging = Join-Path $OutputDir "staging-$name"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    Step "Publish $name to $staging"
    # --no-build is not used: the runtime identifier differs from the solution build.
    Invoke-Checked "dotnet publish ($name)" {
        dotnet publish $serverProject -c $configuration -o $staging --nologo -v q @publishArgs
    }

    $exe = Join-Path $staging $exeName
    if (-not (Test-Path $exe)) { throw "Publish output has no $exeName in $staging." }

    # The published output carries the repo's appsettings.Development.json; the service never runs in Development.
    Remove-Item (Join-Path $staging 'appsettings.Development.json') -ErrorAction SilentlyContinue

    foreach ($file in 'install.ps1', 'uninstall.ps1', 'ArkInstall.Common.ps1', 'INSTALL.md') {
        $source = Join-Path $installDir $file
        if (-not (Test-Path $source)) { throw "Required installer file is missing: $source" }
        Copy-Item $source (Join-Path $staging $file)
    }
    foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        $source = Join-Path $repoRoot $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $staging $file) }
        else { Write-Warning "$file is not in the repository root; the package ships without it." }
    }

    # Describes the package; it is not the installation marker (install.ps1 writes install.json).
    $packageJson = [ordered]@{ version = $version; selfContained = $selfContained } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $staging 'package.json'), $packageJson + "`n", (New-Object System.Text.UTF8Encoding $false))

    $zip = Join-Path $OutputDir "$packageBaseName-$version-$name.zip"
    Step "Zip $zip"
    New-Zip $staging $zip
    Remove-Item $staging -Recurse -Force
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "$zip ($size MB)"
    return $zip
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Get-ChildItem $OutputDir -Filter "$packageBaseName-*.zip" | Remove-Item -Force
    Remove-Item (Join-Path $OutputDir 'SHA256SUMS') -ErrorAction SilentlyContinue

    Step 'Version (MinVer)'
    $fullVersion = Get-MinVerVersion
    if ($fullVersion) { Write-Host "MinVerVersion: $fullVersion" }
    else { Write-Warning 'MinVerVersion is empty; the published exe ProductVersion is used instead.' }

    Step "Build ($configuration)"
    Invoke-Checked 'dotnet build' { dotnet build $solution -c $configuration --nologo -v q }

    if (-not $SkipTests) {
        $testResults = Join-Path $OutputDir 'TestResults'
        if (Test-Path $testResults) { Remove-Item $testResults -Recurse -Force }
        # The test projects are Microsoft.Testing.Platform executables; `dotnet test` finds zero tests with this SDK.
        Step 'Unit tests'
        Invoke-Checked 'unit tests' { dotnet run --project $unitTests -c $configuration --no-build -- --report-trx --results-directory $testResults }
        Step 'Integration tests'
        Invoke-Checked 'integration tests' { dotnet run --project $integrationTests -c $configuration --no-build -- --report-trx --results-directory $testResults }
    }

    if (-not $fullVersion) {
        # Publish once to a probe folder just to read the version off the exe, then package for real.
        $probe = Join-Path $OutputDir 'staging-version-probe'
        if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
        Invoke-Checked 'dotnet publish (version probe)' { dotnet publish $serverProject -c $configuration -o $probe --nologo -v q }
        $fullVersion = Get-ProductVersion (Join-Path $probe $exeName)
        Remove-Item $probe -Recurse -Force
        if (-not $fullVersion) { throw 'Neither MinVer nor the exe ProductVersion produced a version.' }
        Write-Host "ProductVersion: $fullVersion"
    }
    $version = Remove-BuildMetadata $fullVersion
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$version' is not a usable version." }
    Write-Host "Package version: $version"

    $zips = @()
    $zips += New-Package 'win-x64' @('-r', 'win-x64', '--self-contained', 'true') $true $version
    $zips += New-Package 'win-x64-fdd' @('-r', 'win-x64', '--self-contained', 'false') $false $version

    Step 'SHA256SUMS'
    $sums = foreach ($zip in $zips) {
        $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $zip -Leaf)"
    }
    $sumsPath = Join-Path $OutputDir 'SHA256SUMS'
    [System.IO.File]::WriteAllText($sumsPath, ($sums -join "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
    Get-Content $sumsPath | Write-Host

    Write-Host "`nRelease files in $OutputDir" -ForegroundColor Green
    # The version is the script's pipeline output so callers (the release workflow) can capture it.
    $version
}
finally {
    Pop-Location
}
