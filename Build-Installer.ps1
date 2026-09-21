# Builds ConnectionWatcher-Setup-<version>.exe and
# ConnectionWatcher-<version>-win-x64.zip in dist\.
# Publishes conwatch.exe (the command + web host) and conwatch-tray.exe
# self-contained win-x64 into ONE staging folder (they share the runtime),
# then compiles the Inno Setup script and zips the same folder. Requires
# Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# -Version stamps the installer name, the zip name and the binaries inside.
# The release workflow passes the tag's version; by hand it defaults to
# Directory.Build.props' "<prefix>-<suffix>".
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
$app = Join-Path $staging "app"
$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $app, $dist | Out-Null

foreach ($project in "Programs\ConWatchCli", "Programs\ConWatchTray") {
    Write-Host "Publishing $project (self-contained win-x64) into staging\app..."
    dotnet publish (Join-Path $PSScriptRoot $project) -c Release `
        -r win-x64 --self-contained true -o $app "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
}
# The host project (ConWatch, assembly ConWatchHost) rides along as a project
# reference of ConWatchCli; its own apphost is not the command, so drop it.
Remove-Item (Join-Path $app "ConWatchHost.exe") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot "deploy\windows\README.txt") $app
Copy-Item (Join-Path $PSScriptRoot "LICENSE") $app

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DStagingDir=$app" "/O$dist" `
    (Join-Path $PSScriptRoot "deploy\windows\installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$zip = Join-Path $dist "ConnectionWatcher-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip

Write-Host ""
Write-Host "Installer ready: $(Join-Path $dist "ConnectionWatcher-Setup-$Version.exe")" -ForegroundColor Green
Write-Host "Zip ready:       $zip" -ForegroundColor Green
