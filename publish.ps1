<#
.SYNOPSIS
    Builds a distributable CatTracker release.

.DESCRIPTION
    Optional. Both platforms can install straight from a source checkout — see
    setup/macos/install.sh and setup/windows/install.ps1. This script exists for when you want a
    self-contained archive to carry to a machine that has no .NET SDK.

    Output: out/cattracker-<rid>/ and out/cattracker-<rid>.tar.gz (or .zip on Windows).

.EXAMPLE
    ./publish.ps1                     # macOS, Apple silicon
    ./publish.ps1 -Rid osx-x64        # macOS, Intel
    ./publish.ps1 -Rid win-x64        # Windows
#>
[CmdletBinding()]
param(
    [ValidateSet('osx-arm64', 'osx-x64', 'win-x64', 'win-arm64')]
    [string] $Rid = 'osx-arm64',

    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path $root "out\cattracker-$Rid"
$isMac = $Rid.StartsWith('osx')

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'CatTracker.slnx') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not publishing.' }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "Publishing app for $Rid..." -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\CatTracker.App') `
    -c $Configuration -r $Rid --self-contained true `
    -o (Join-Path $stage 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

if ($isMac) {
    Write-Host "Publishing reader for $Rid..." -ForegroundColor Cyan
    # Single file and trimmed on purpose: Full Disk Access is granted to one binary, and one file
    # is what you drag into System Settings.
    dotnet publish (Join-Path $root 'src\CatTracker.Reader') `
        -c $Configuration -r $Rid --self-contained true `
        -o (Join-Path $stage 'reader') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Reader publish failed.' }

    # Flat layout: install.sh finds the plists beside itself.
    Copy-Item (Join-Path $root 'setup\macos\*') $stage -Recurse -Force
}
else {
    # The reader is macOS-only — there is no Find My cache on Windows to read.
    Copy-Item (Join-Path $root 'setup\windows\*') $stage -Recurse -Force
}

Push-Location (Join-Path $root 'out')
try {
    if ($isMac) {
        $archive = Join-Path $root "out\cattracker-$Rid.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        # bsdtar ships with Windows 10+. Executable bits survive the round trip to macOS.
        tar --create --gzip --file "cattracker-$Rid.tar.gz" "cattracker-$Rid"
        if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
    }
    else {
        $archive = Join-Path $root "out\cattracker-$Rid.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Compress-Archive -Path "cattracker-$Rid" -DestinationPath $archive
    }
}
finally { Pop-Location }

$size = [math]::Round((Get-Item $archive).Length / 1MB, 1)

Write-Host ''
Write-Host "Release ready: $archive ($size MB)" -ForegroundColor Green
Write-Host ''

if ($isMac) {
    Write-Host 'On the Mac:'
    Write-Host "  tar xzf cattracker-$Rid.tar.gz && cd cattracker-$Rid && ./install.sh"
    Write-Host ''
    Write-Host 'Or copy and install in one step:  ./deploy.ps1 -MacHost mac.local -User you'
}
else {
    Write-Host 'On the Windows machine, from an elevated PowerShell:'
    Write-Host "  Expand-Archive cattracker-$Rid.zip; cd cattracker-$Rid; ./install.ps1"
}
