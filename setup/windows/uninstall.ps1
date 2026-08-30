<#
.SYNOPSIS
    Removes the CatTracker Windows Service, its files and its firewall rule.

.DESCRIPTION
    Your data is left alone unless you pass -Purge.

.EXAMPLE
    ./setup/windows/uninstall.ps1
    ./setup/windows/uninstall.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\CatTracker",
    [string] $DataDirectory = "$env:ProgramData\CatTracker",
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'CatTracker'

$admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this from an elevated PowerShell.' }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:30')
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "  removed service $ServiceName"
}
else { Write-Host "  service $ServiceName was not installed" }

Get-NetFirewallRule -DisplayName 'CatTracker (*' -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-NetFirewallRule -Name $_.Name
        Write-Host "  removed firewall rule $($_.DisplayName)"
    }

if (Test-Path $InstallRoot) {
    Remove-Item $InstallRoot -Recurse -Force
    Write-Host "  removed $InstallRoot"
}

if ($Purge) {
    if (Test-Path $DataDirectory) {
        Remove-Item $DataDirectory -Recurse -Force
        Write-Host "  removed $DataDirectory (database, logs and settings)"
    }
}
else {
    Write-Host ''
    Write-Host "  Your data is still at: $DataDirectory"
    Write-Host '  Delete it with: ./uninstall.ps1 -Purge'
}
