# Publishes (or updates) the tray companion to Program Files\DSoftCo\ConnectionWatcher\Tray.
# Stops/restarts a running tray around the copy. No parameters needed; elevates itself.
[CmdletBinding()]
param([string]$Root = "")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy\windows\common.ps1")

if (-not $Root -and -not (Test-IsAdmin)) {
    Invoke-SelfElevated $PSCommandPath
    return
}
Publish-TrayTo (Join-Path (Get-InstallRoot $Root) "Tray")
Write-Host "Tray published to $(Join-Path (Get-InstallRoot $Root) 'Tray')" -ForegroundColor Green
