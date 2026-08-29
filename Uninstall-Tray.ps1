# Stops the tray companion and removes its autostart, shortcut, and per-user
# settings. Application files stay. No parameters, no elevation needed.
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "deploy\windows\uninstall-tray.ps1")
