# Publishes (or updates) the server to Program Files\DSoftCo\ConnectionWatcher\Watcher.
# Stops/restarts the Windows service around the copy when it is installed.
# No parameters needed; elevates itself.
[CmdletBinding()]
param([string]$Root = "")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy\windows\common.ps1")

if (-not $Root -and -not (Test-IsAdmin)) {
    Invoke-SelfElevated $PSCommandPath
    return
}
Publish-WatcherTo (Join-Path (Get-InstallRoot $Root) "Watcher")
Write-Host "Watcher published to $(Join-Path (Get-InstallRoot $Root) 'Watcher')" -ForegroundColor Green
