<#
.SYNOPSIS
    CatTracker — complete Windows setup. Installs it as a Windows Service.

.DESCRIPTION
    Builds from source (or uses a prebuilt release next to this script), installs to
    Program Files, registers an auto-starting Windows Service, opens the firewall port, and
    verifies the API answers.

    The same command installs and updates. It is idempotent.

    ABOUT THE DATA SOURCE. Windows has no Find My cache — that file only exists on a Mac — so a
    Windows install cannot read a real AirTag on its own. It defaults to the Replay source: a
    synthetic cat, which is genuinely useful for evaluating, developing and demonstrating.
    If you have a Mac running the reader and want the app on Windows instead, point
    -Source Spool at the shared spool folder with -SpoolDirectory.

    Run from an elevated PowerShell (registering a service and a firewall rule needs admin).

.EXAMPLE
    ./setup/windows/install.ps1
    ./setup/windows/install.ps1 -Port 8080
    ./setup/windows/install.ps1 -Source Spool -SpoolDirectory \\mac\CatTracker\spool
#>
[CmdletBinding()]
param(
    [ValidateSet('Replay', 'Spool')]
    [string] $Source = 'Replay',

    [string] $SpoolDirectory = '',
    [int]    $Port = 5185,
    [string] $InstallRoot = "$env:ProgramFiles\CatTracker",
    [string] $DataDirectory = "$env:ProgramData\CatTracker",
    [int]    $SeedDays = 14,
    [switch] $NoFirewall
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'CatTracker'
$scriptDir = $PSScriptRoot

function Say  { param($m) Write-Host $m }
function Head { param($m) Write-Host ''; Write-Host $m -ForegroundColor White }
function Ok   { param($m) Write-Host "  [ok] $m" -ForegroundColor Green }
function Warn { param($m) Write-Host "  [!]  $m" -ForegroundColor Yellow }

$admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    throw 'Run this from an elevated PowerShell — registering a service and a firewall rule needs administrator rights.'
}

Write-Host 'CatTracker - Windows setup' -ForegroundColor White

# ---- 0. find or build the binaries ------------------------------------------------------------

if (Test-Path (Join-Path $scriptDir 'app\cattracker.exe')) {
    $stage = Join-Path $scriptDir 'app'
    Say "  using the prebuilt release in $stage"
}
elseif (Test-Path (Join-Path $scriptDir '..\..\src\CatTracker.App')) {
    $repo = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
    $stage = Join-Path $repo 'out\cattracker-win-x64\app'

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK is not installed. Get it from https://dot.net.'
    }

    Head '0. Building from source (win-x64)'
    Say '  this takes a minute or two the first time'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    dotnet publish (Join-Path $repo 'src\CatTracker.App') `
        -c Release -r win-x64 --self-contained true -o $stage --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Ok "built into $stage"
}
else {
    throw 'Cannot find binaries to install, and no source tree to build from.'
}

$appDir = Join-Path $InstallRoot 'app'
$logDir = Join-Path $DataDirectory 'logs'

Say "  install : $InstallRoot"
Say "  data    : $DataDirectory"

# ---- 1. stop the existing service ---------------------------------------------------------------

Head '1. Stopping the existing service'
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        # The file lock outlives the "Stopped" status by a moment; releasing it is what lets the
        # copy below succeed rather than failing with "file in use".
        $existing.WaitForStatus('Stopped', '00:00:30')
        Start-Sleep -Seconds 2
    }
    Ok 'stopped'
}
else { Say '  not installed yet' }

# ---- 2. files -------------------------------------------------------------------------------------

Head '2. Application files'
New-Item -ItemType Directory -Force -Path $appDir, $DataDirectory, $logDir | Out-Null

# Replace wholesale so a removed file cannot linger from a previous version.
Get-ChildItem $appDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item "$stage\*" $appDir -Recurse -Force

$exe = Join-Path $appDir 'cattracker.exe'
if (-not (Test-Path $exe)) { throw "cattracker.exe is missing from $appDir." }
Ok "installed to $appDir"

# ---- 3. the service ---------------------------------------------------------------------------------

Head '3. Windows Service'

$spool = if ($SpoolDirectory) { $SpoolDirectory } else { Join-Path $DataDirectory 'spool' }
New-Item -ItemType Directory -Force -Path $spool | Out-Null

# Service environment lives in the registry: the service must never depend on the shell that
# installed it, and data must sit outside the install directory so an update cannot touch it.
$environment = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    "CATTRACKER_CatTracker__DataDirectory=$DataDirectory",
    "CATTRACKER_CatTracker__FindMy__Source=$Source",
    "CATTRACKER_CatTracker__FindMy__SpoolDirectory=$spool",
    "CATTRACKER_CatTracker__Replay__SeedDays=$SeedDays",
    "CATTRACKER_urls=http://0.0.0.0:$Port"
)

if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName 'CatTracker' -StartupType Automatic `
        -Description 'Local cat tracking: collector, API and web UI.' | Out-Null
    Ok 'service registered'
}
else {
    # Point the existing service at the (possibly moved) binary rather than re-creating it.
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
    Ok 'service updated'
}

Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment -Value $environment -Type MultiString

# Restart on crash rather than sitting dead until someone notices.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Ok 'auto-restart on failure configured'

# ---- 4. firewall ---------------------------------------------------------------------------------------

if (-not $NoFirewall) {
    Head '4. Firewall'
    $ruleName = "CatTracker ($Port)"
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    # Private profile only: this has no authentication and is meant for your own network.
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Profile Private | Out-Null
    Ok "port $Port opened on private networks"
}

# ---- 5. start and verify ---------------------------------------------------------------------------------

Head '5. Starting'
Start-Service -Name $ServiceName

$health = $null
foreach ($attempt in 1..30) {
    try { $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 3; break }
    catch { Start-Sleep -Seconds 1 }
}

if ($health) {
    Ok "API is up: version $($health.version), schema $($health.schema)"

    # Check the UI separately. A wrong content root leaves the API answering happily while every
    # page and stylesheet 404s — worth catching here rather than on your phone.
    try {
        Invoke-WebRequest "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 10 | Out-Null
        Ok 'Web UI is being served.'
    }
    catch {
        Warn "The API is up but the web UI is not being served. Check $logDir."
    }
}
else {
    Warn "The API did not answer on port $Port within 30s."
    Warn "Look at: $logDir"
    Warn "         Get-Content '$logDir\cattracker-*.log' -Tail 40"
}

# ---- done -----------------------------------------------------------------------------------------------

Head 'CatTracker is at'
Say "  http://localhost:$Port"
Say "  http://$($env:COMPUTERNAME):$Port    (from your phone, on the same network)"
Write-Host ''
Say "  Data:     $DataDirectory"
Say "  Logs:     $logDir"
Say "  Settings: $DataDirectory\config.local.json   (optional; never overwritten by an update)"
Write-Host ''
Say "  Restart:   Restart-Service $ServiceName"
Say "  Stop:      Stop-Service $ServiceName"
Say "  Uninstall: $scriptDir\uninstall.ps1"

if ($Source -eq 'Replay') {
    Write-Host ''
    Warn 'Running the Replay source: a synthetic cat, not a real AirTag.'
    Warn 'Windows has no Find My cache, so real AirTag data needs the macOS setup.'
}
