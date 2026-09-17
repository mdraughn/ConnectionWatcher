# Builds ConnectionWatcher-Setup-<version>.exe in dist\.
# Publishes self-contained win-x64 binaries (no .NET install needed on the
# target machine), then compiles the Inno Setup script. Requires Inno Setup 6
# (winget install JRSoftware.InnoSetup).
#
# -Version stamps BOTH the installer name and the binaries inside it (the
# header and /api/v1/health). The release workflow passes the tag's version;
# by hand it defaults to Directory.Build.props' "<prefix>-<suffix>".
[CmdletBinding()]
param([string]$Version = "")
$ErrorActionPreference = "Stop"

if (-not $Version) {
    $props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props") -Raw
    $prefix = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
    $suffix = [regex]::Match($props, "<VersionSuffix>([^<]+)</VersionSuffix>").Groups[1].Value
    if (-not $prefix) { throw "VersionPrefix not found in Directory.Build.props" }
    $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
}

$staging = Join-Path $PSScriptRoot "staging"
$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging, $dist | Out-Null

Write-Host "Publishing Watcher (self-contained win-x64)..."
dotnet publish (Join-Path $PSScriptRoot "Programs\ConWatch") -c Release `
    -r win-x64 --self-contained true -o (Join-Path $staging "Watcher") "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Watcher publish failed." }

Write-Host "Publishing Tray (self-contained win-x64)..."
dotnet publish (Join-Path $PSScriptRoot "Programs\ConTray") -c Release `
    -r win-x64 --self-contained true -o (Join-Path $staging "Tray") "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed." }

New-Item -ItemType Directory -Force (Join-Path $staging "deploy") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "deploy\windows\*.ps1") (Join-Path $staging "deploy")
Copy-Item (Join-Path $PSScriptRoot "LICENSE") $staging

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DStagingDir=$staging" "/O$dist" `
    (Join-Path $PSScriptRoot "deploy\windows\installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

Write-Host ""
Write-Host "Installer ready: $(Join-Path $dist "ConnectionWatcher-Setup-$Version.exe")" -ForegroundColor Green
