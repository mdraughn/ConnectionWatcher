<#
.SYNOPSIS
    Removes the standalone tray companion's autostart, shortcut, and per-user
    settings, and stops it. Application files are left in place.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Get-Process -Name "ConTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "ConnectionWatcherTray" -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "ConnectionWatcher Tray.lnk") `
    -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("ApplicationData")) "DSoftCo\ConnectionWatcher\contray-settings.json") `
    -ErrorAction SilentlyContinue

Write-Host "Tray companion removed (application files left in place)."
