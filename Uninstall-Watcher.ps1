# Removes the ConnectionWatcher Windows service, firewall rule, and shortcut.
# Application files and measurement data stay. No parameters needed; elevates itself.
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy\windows\common.ps1")

if (-not (Test-IsAdmin)) {
    Invoke-SelfElevated $PSCommandPath
    return
}
& (Join-Path $PSScriptRoot "deploy\windows\uninstall-service.ps1")
