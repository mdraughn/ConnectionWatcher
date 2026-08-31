<#
.SYNOPSIS
    Points the tray companion at a ConnectionWatcher server and registers it
    to start at login. Called by Setup.exe; harmless to run by hand.

.DESCRIPTION
    -Url may be omitted when the Watcher service is installed on this machine:
    the pinned port is read from the service definition. Falls back to
    http://localhost:8080.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TrayDir,
    [string]$Url = ""
)

$ErrorActionPreference = "Stop"

if (-not $Url) {
    $service = Get-CimInstance Win32_Service -Filter "Name='ConnectionWatcher'" -ErrorAction SilentlyContinue
    $Url = if ($service -and $service.PathName -match "--urls\s+\S*:(\d+)") {
        "http://localhost:$($Matches[1])"
    } else {
        "http://localhost:8080"
    }
}

@{ url = $Url } | ConvertTo-Json | Set-Content (Join-Path $TrayDir "contray.json") -Encoding utf8

$exe = Join-Path $TrayDir "ConTray.exe"
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "ConnectionWatcherTray" -Value "`"$exe`""

Get-Process -Name "ConTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process $exe
Write-Host "Tray companion configured for $Url (starts at login)."
