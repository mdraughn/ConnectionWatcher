# Publishes the server to Program Files\DSoftCo\ConnectionWatcher\Watcher and
# installs it as the Windows service (port probe, firewall, recovery,
# shortcuts; data in ProgramData). No parameters needed; elevates itself.
[CmdletBinding()]
param([string]$Root = "")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy\windows\common.ps1")

if (-not (Test-IsAdmin)) {
    Invoke-SelfElevated $PSCommandPath
    return
}
$appDir = Join-Path (Get-InstallRoot $Root) "Watcher"
Publish-WatcherTo $appDir
& (Join-Path $PSScriptRoot "deploy\windows\install-service.ps1") `
    -AppDir $appDir -DataDir (Get-DefaultDataDir $Root)
