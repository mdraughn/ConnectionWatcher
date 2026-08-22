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
    [string]$ServiceName = "ConnectionWatcher"
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
$binPath = "`"$exe`" --urls $url"
& sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ConnectionWatcher" | Out-Null
& sc.exe description $ServiceName "Home internet connectivity monitor (dashboard at $dashboard)" | Out-Null
# Restart on failure: 5s, 30s, 60s; reset the failure counter daily.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

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
if (Test-Path $tray) {
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
Write-Host "  Data:     $(Join-Path $AppDir 'data\conwatch.db')"
Write-Host "  Firewall: TCP $Port (Private/Domain profiles)"
Write-Host "  Change the port later: re-run this script with -Port <n>"
Start-Process $dashboard
