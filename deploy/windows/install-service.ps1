<#
.SYNOPSIS
    Installs ConnectionWatcher as a Windows service from a published output.

.DESCRIPTION
    Run elevated, from (or pointing at) a `dotnet publish` output folder.
    Probes for a free port ONCE (default 8080, walking upward, or use -Port),
    pins it in the service definition, opens the Windows Firewall for it,
    configures restart-on-failure, creates a Start Menu shortcut, registers
    the tray companion to start at login (if present), and opens the
    dashboard. Idempotent: re-running reconfigures in place.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -AppDir C:\ConWatch -Port 9090
#>
[CmdletBinding()]
param(
    [string]$AppDir = $PSScriptRoot,
    [int]$Port = 0,
    [string]$ServiceName = "ConnectionWatcher",
    # Where the SQLite database lives. Default keeps data beside the app;
    # Windows-like installs pass a ProgramData path instead.
    [string]$DataDir = "",
    # Set by the Setup.exe installer: it owns shortcuts, tray, and launching.
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

$exe = Join-Path $AppDir "ConWatch.exe"
if (-not (Test-Path $exe)) {
    throw "ConWatch.exe not found in '$AppDir'. Point -AppDir at a 'dotnet publish' output."
}

# --- Pick the port: honor -Port, else probe 8080 upward. Pinned forever. ---
function Test-PortFree([int]$candidate) {
    -not (Get-NetTCPConnection -State Listen -LocalPort $candidate -ErrorAction SilentlyContinue)
}
if ($Port -eq 0) {
    $Port = 8080
    while (-not (Test-PortFree $Port)) { $Port++ }
} elseif (-not (Test-PortFree $Port)) {
    Write-Warning "Port $Port is currently in use; installing anyway since you chose it explicitly."
}
$url = "http://0.0.0.0:$Port"
$dashboard = "http://localhost:$Port"

# --- (Re)create the service with the pinned URL baked into its arguments. ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service exists - reconfiguring."
    if ($existing.Status -eq "Running") { Stop-Service $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
if (-not $DataDir) { $DataDir = Join-Path $AppDir "data" }
New-Item -ItemType Directory -Force $DataDir | Out-Null
$dbPath = Join-Path $DataDir "conwatch.db"
$binPath = "`"$exe`" --urls $url --Persistence:DbPath `"$dbPath`""
# New-Service passes the path string straight to the service API. sc.exe create
# cannot be used here: PowerShell re-quotes arguments containing spaces without
# escaping the embedded quotes, and sc.exe receives a mangled binPath.
New-Service -Name $ServiceName -BinaryPathName $binPath -StartupType Automatic `
    -DisplayName "ConnectionWatcher" `
    -Description "Home internet connectivity monitor (dashboard at $dashboard)" | Out-Null
# Restart on failure: 5s, 30s, 60s; reset the failure counter daily.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure configuration failed (exit $LASTEXITCODE)." }

# --- Firewall: one rule, tied to this port; replace any previous one. ---
Get-NetFirewallRule -DisplayName "ConnectionWatcher" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName "ConnectionWatcher" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $Port -Profile Private,Domain | Out-Null

# --- Start Menu shortcut. ---
$shortcutDir = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDir "ConnectionWatcher Dashboard.lnk"))
$shortcut.TargetPath = $dashboard
$shortcut.Description = "Open the ConnectionWatcher dashboard"
$shortcut.Save()

# --- Tray companion: point it at the service and start it at login. ---
$tray = Join-Path $AppDir "ConTray.exe"
if ($NoLaunch) {
    # Setup.exe runs configure-tray.ps1 itself.
} elseif (Test-Path $tray) {
    @{ url = $dashboard } | ConvertTo-Json | Set-Content (Join-Path $AppDir "contray.json") -Encoding utf8
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name "ConnectionWatcherTray" -Value "`"$tray`""
    Start-Process $tray
    Write-Host "Tray companion installed (starts at login)."
} else {
    Write-Host "ConTray.exe not found next to the app - skipping tray companion."
}

Start-Service $ServiceName
Write-Host ""
Write-Host "ConnectionWatcher is installed and running at $dashboard" -ForegroundColor Green
Write-Host "  Service:  $ServiceName (auto-start, restart-on-failure)"
Write-Host "  Data:     $dbPath"
Write-Host "  Firewall: TCP $Port (Private/Domain profiles)"
Write-Host "  Change the port later: re-run this script with -Port <n>"
if (-not $NoLaunch) {
    Start-Process $dashboard
}
