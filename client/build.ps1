<#
.SYNOPSIS
    Builds the PDC HomeConnect Windows client and its installer.

.DESCRIPTION
    Publishes the service, the tray app and the Claude Code hook helper (self-contained, win-x64)
    into one folder, compiles the Inno Setup installer and writes SHA256SUMS.txt.

    Output (artifacts\):
      publish\                              application files
      PDC-HomeConnect-Setup-<version>.exe   installer
      SHA256SUMS.txt                        checksums (used by the self-update)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 0.2.0
    .\build.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $Version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
}
$Version = $Version.TrimStart('v')
Write-Host "Building PDC HomeConnect $Version" -ForegroundColor Cyan

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish | Out-Null

if (-not $SkipTests) {
    Write-Host 'Running tests' -ForegroundColor Cyan
    Invoke-Checked dotnet @('test', (Join-Path $root 'PdcHomeConnect.slnx'), '-c', $Configuration, '--nologo')
}

# All apps are published self-contained into one folder and share the runtime files.
# The tray app must come last: its Windows Desktop runtime replaces some base runtime
# assemblies (System.Drawing, Microsoft.VisualBasic) with newer versions the other apps can use too.
$projects = @(
    'src\PdcHomeConnect.Service\PdcHomeConnect.Service.csproj',
    'src\PdcHomeConnect.Hook\PdcHomeConnect.Hook.csproj',
    'src\PdcHomeConnect.Agent\PdcHomeConnect.Agent.csproj'
)
foreach ($project in $projects) {
    Write-Host "Publishing $project" -ForegroundColor Cyan
    Invoke-Checked dotnet @(
        'publish', (Join-Path $root $project),
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        "-p:Version=$Version",
        '-o', $publish,
        '--nologo'
    )
}

# Debug symbols are not shipped.
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

if ($SkipInstaller) {
    Write-Host "Done: $publish" -ForegroundColor Green
    return
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

Write-Host 'Compiling installer' -ForegroundColor Cyan
Invoke-Checked $iscc @(
    "/DAppVersion=$Version",
    "/DSourceDir=$publish",
    "/DOutputDir=$artifacts",
    (Join-Path $root 'installer\PdcHomeConnect.iss')
)

$installer = Join-Path $artifacts "PDC-HomeConnect-Setup-$Version.exe"
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Value "$hash  PDC-HomeConnect-Setup-$Version.exe" -Encoding ascii

Write-Host "Done: $installer" -ForegroundColor Green
