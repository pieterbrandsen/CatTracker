<#
.SYNOPSIS
    Runs CatTracker on this Windows machine against a synthetic cat.

.DESCRIPTION
    No Mac, no AirTag, no Apple account. A replay source generates a plausible cat and, on first
    run, backfills a fortnight of history so the map, timeline and every chart have something
    real to show immediately.

.EXAMPLE
    ./run-local.ps1
    ./run-local.ps1 -Fresh          # throw away the local database first
    ./run-local.ps1 -SeedDays 30
#>
[CmdletBinding()]
param(
    [int] $SeedDays = 14,
    [int] $Port = 5185,
    [switch] $Fresh
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$data = Join-Path $root '.data'

if ($Fresh -and (Test-Path $data)) {
    Remove-Item $data -Recurse -Force
    Write-Host 'Local database removed.' -ForegroundColor Yellow
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:CATTRACKER_CatTracker__DataDirectory = $data
$env:CATTRACKER_CatTracker__Replay__SeedDays = $SeedDays
$env:CATTRACKER_urls = "http://localhost:$Port"

Write-Host ''
Write-Host "CatTracker (replay)  →  http://localhost:$Port" -ForegroundColor Green
Write-Host "Data: $data"
Write-Host ''

dotnet run --project (Join-Path $root 'src\CatTracker.App') --no-launch-profile
