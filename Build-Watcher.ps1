# Builds the ConnectionWatcher server (Debug). No parameters needed.
$ErrorActionPreference = "Stop"
dotnet build (Join-Path $PSScriptRoot "Programs\ConWatch\ConWatch.csproj")
