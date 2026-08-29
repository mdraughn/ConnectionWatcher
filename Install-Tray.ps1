# Publishes the tray companion to Program Files\DSoftCo\ConnectionWatcher\Tray
# and installs it (login autostart, Start Menu entry). If the local
# ConnectionWatcher service is installed, its pinned port becomes the tray's
# default server; otherwise localhost:8080 (change any time in the tray's
# Settings). No parameters needed; elevates itself for the Program Files copy.
[CmdletBinding()]
param([string]$Root = "")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy\windows\common.ps1")

if (-not $Root -and -not (Test-IsAdmin)) {
    Invoke-SelfElevated $PSCommandPath
    return
}
$appDir = Join-Path (Get-InstallRoot $Root) "Tray"
Publish-TrayTo $appDir

$url = Get-InstalledWatcherUrl
if ($url) {
    Write-Host "Detected local ConnectionWatcher service at $url"
    & (Join-Path $PSScriptRoot "deploy\windows\install-tray.ps1") -AppDir $appDir -Url $url
} else {
    & (Join-Path $PSScriptRoot "deploy\windows\install-tray.ps1") -AppDir $appDir
}
