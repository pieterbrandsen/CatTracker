<#
.SYNOPSIS
    Runs the test suite with coverage and fails if it drops below the threshold.

.EXAMPLE
    ./coverage.ps1
    ./coverage.ps1 -Threshold 85 -ShowGaps
#>
[CmdletBinding()]
param(
    [double] $Threshold = 80,
    [switch] $ShowGaps
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$results = Join-Path $root 'tests\CatTracker.Tests\TestResults'

if (Test-Path $results) { Remove-Item $results -Recurse -Force }

Write-Host 'Running tests with coverage...' -ForegroundColor Cyan
dotnet test (Join-Path $root 'CatTracker.slnx') --nologo --collect:"XPlat Code Coverage"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$report = Get-ChildItem $results -Recurse -Filter 'coverage.cobertura.xml' |
          Sort-Object LastWriteTime -Descending |
          Select-Object -First 1

if (-not $report) { throw 'No coverage report was produced.' }

[xml] $xml = Get-Content $report.FullName
$line = [double] $xml.coverage.'line-rate' * 100
$branch = [double] $xml.coverage.'branch-rate' * 100

Write-Host ''
Write-Host ('Line coverage   : {0:N1}%' -f $line)
Write-Host ('Branch coverage : {0:N1}%' -f $branch)
Write-Host ''

foreach ($package in $xml.coverage.packages.package) {
    Write-Host ('  {0,-24} {1,6:N1}%' -f $package.name, ([double] $package.'line-rate' * 100))
}

if ($ShowGaps) {
    Write-Host ''
    Write-Host 'Classes below the threshold:' -ForegroundColor Yellow

    foreach ($package in $xml.coverage.packages.package) {
        foreach ($class in $package.classes.class) {
            $rate = [double] $class.'line-rate' * 100
            if ($class.lines.line -and $rate -lt $Threshold) {
                Write-Host ('  {0,6:N1}%  {1}' -f $rate, $class.name)
            }
        }
    }
}

Write-Host ''
if ($line -lt $Threshold) {
    Write-Host ('FAIL: line coverage {0:N1}% is below the {1:N0}% threshold.' -f $line, $Threshold) -ForegroundColor Red
    exit 1
}

Write-Host ('PASS: line coverage {0:N1}% meets the {1:N0}% threshold.' -f $line, $Threshold) -ForegroundColor Green
