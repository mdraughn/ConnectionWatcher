# Builds the tray companion (Debug). No parameters needed.
$ErrorActionPreference = "Stop"
dotnet build (Join-Path $PSScriptRoot "Programs\ConTray\ConTray.csproj")
