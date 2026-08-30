<#
.SYNOPSIS
    Optional convenience: build on Windows, copy to the Mac, install or update, verify.

.DESCRIPTION
    You do not need this. The Mac can install itself entirely from a source checkout with
    setup/macos/install.sh — see docs/SETUP-MACOS.md. This script is for when you would rather
    drive the Mac from your Windows box.

    The same command does a first install and every later update. Requires Remote Login (SSH)
    enabled on the Mac: System Settings → General → Sharing → Remote Login.

.EXAMPLE
    ./deploy.ps1 -MacHost mac.local -User pieter
    ./deploy.ps1 -MacHost 192.168.1.40 -User pieter -Rid osx-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MacHost,
    [Parameter(Mandatory)] [string] $User,

    [ValidateSet('osx-arm64', 'osx-x64')]
    [string] $Rid = 'osx-arm64',

    [int] $Port = 5185,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = "$User@$MacHost"
$name = "cattracker-$Rid"
$archive = Join-Path $root "out\$name.tar.gz"

& (Join-Path $root 'publish.ps1') -Rid $Rid -SkipTests:$SkipTests
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host ''
Write-Host "Copying to $target..." -ForegroundColor Cyan
scp $archive "${target}:~/$name.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Could not copy to $target. Is Remote Login enabled on the Mac?" }

Write-Host "Installing on $MacHost..." -ForegroundColor Cyan
# Unpack into a fresh directory each time so a half-written previous release cannot leak in.
$remote = @"
set -e
rm -rf ~/$name
tar xzf ~/$name.tar.gz -C ~
chmod +x ~/$name/*.sh ~/$name/app/cattracker ~/$name/reader/cattracker-reader
cd ~/$name && ./install.sh
"@

ssh $target $remote
if ($LASTEXITCODE -ne 0) { throw 'Remote install failed. See the output above.' }

Write-Host ''
Write-Host 'Verifying...' -ForegroundColor Cyan
try {
    $health = Invoke-RestMethod "http://${MacHost}:$Port/api/health" -TimeoutSec 15
    Write-Host ("  version {0}, schema {1}" -f $health.version, $health.schema) -ForegroundColor Green
    Write-Host ''
    Write-Host "CatTracker is live at http://${MacHost}:$Port" -ForegroundColor Green
}
catch {
    Write-Warning "Installed, but http://${MacHost}:$Port/api/health did not answer from here."
    Write-Warning 'That is usually a firewall on the Mac, not a failed install. Check on the Mac itself.'
}
