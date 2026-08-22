<#
.SYNOPSIS
    Removes the ConnectionWatcher Windows service, firewall rule, shortcut,
    and tray autostart. The application folder and its data are left intact.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "ConnectionWatcher"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq "Running") { Stop-Service $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed."
} else {
    Write-Host "Service not found - nothing to remove."
}

Get-NetFirewallRule -DisplayName "ConnectionWatcher" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\ConnectionWatcher Dashboard.lnk") `
    -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "ConnectionWatcherTray" -ErrorAction SilentlyContinue
Get-Process -Name "ConTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Done. Application files and measurement data were NOT deleted."
