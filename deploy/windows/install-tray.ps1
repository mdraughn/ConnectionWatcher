<#
.SYNOPSIS
    Installs the ConnectionWatcher tray companion as a standalone app.

.DESCRIPTION
    Run from (or pointing at) a published ConTray output. No elevation needed —
    everything is per-user. Registers login autostart and a Start Menu entry,
    optionally writes the installed-default server URL, and starts the tray.
    The user can still override the URL any time via the tray's Settings.

.EXAMPLE
    .\install-tray.ps1 -AppDir C:\ConTray -Url http://nas:8080
#>
[CmdletBinding()]
param(
    [string]$AppDir = $PSScriptRoot,
    [string]$Url = ""
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $AppDir "ConTray.exe"
if (-not (Test-Path $exe)) {
    throw "ConTray.exe not found in '$AppDir'. Point -AppDir at a 'dotnet publish' output of Programs/ConTray."
}

if ($Url) {
    @{ url = $Url.TrimEnd('/') } | ConvertTo-Json | Set-Content (Join-Path $AppDir "contray.json") -Encoding utf8
    Write-Host "Installed default server: $Url"
} elseif (-not (Test-Path (Join-Path $AppDir "contray.json"))) {
    Write-Host "No -Url given and no contray.json present - the tray will default to http://localhost:8080 (change it in Settings)."
}

Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "ConnectionWatcherTray" -Value "`"$exe`""

$shortcutDir = [Environment]::GetFolderPath("Programs")
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDir "ConnectionWatcher Tray.lnk"))
$shortcut.TargetPath = $exe
$shortcut.Description = "ConnectionWatcher status in your system tray"
$shortcut.Save()

Get-Process -Name "ConTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process $exe
Write-Host "Tray companion installed: starts at login, running now. Right-click the dot for Settings." -ForegroundColor Green
