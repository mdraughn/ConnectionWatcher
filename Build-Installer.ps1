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
#
# -Sign code-signs the project's own exes and DLLs, the uninstaller and the
# Setup exe with Azure Artifact Signing (deploy\windows\signing-metadata.json
# names the account and certificate profile). It needs signtool.exe and the
# Artifact Signing dlib under -ToolsDir (deploy\windows\Get-SigningTools.ps1
# fetches them) and an Azure identity that holds the Certificate Profile
# Signer role: `az login` on a workstation, azure/login (OIDC) in the release
# workflow. Without -Sign nothing is signed and nothing else changes.
[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$Sign,
    [string]$ToolsDir = (Join-Path $PSScriptRoot "tools\signing")
)
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

# --- Signing setup (fail early, before the long publish, if tools are missing) ---
$signCommand = $null   # the exact command line Inno will run for the uninstaller and Setup exe
if ($Sign) {
    $signtool = Get-ChildItem $ToolsDir -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
    $dlib = Get-ChildItem $ToolsDir -Recurse -Filter Azure.CodeSigning.Dlib.dll -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
    if (-not $signtool -or -not $dlib) {
        throw "Signing tools not found under $ToolsDir. Run deploy\windows\Get-SigningTools.ps1 first."
    }
    $metadata = Join-Path $PSScriptRoot "deploy\windows\signing-metadata.json"
    if ($staging -match " " ) { throw "The repo path contains a space; Inno's SignTool command cannot be quoted safely: $staging" }

    # Inno Setup runs its SignTool command through the shell with $f as the
    # file, so keep every quoted path inside a wrapper script and give Inno
    # a single unquoted path to call.
    $wrapper = Join-Path $staging "sign.cmd"
    @(
        "@echo off",
        "`"$($signtool.FullName)`" sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib `"$($dlib.FullName)`" /dmdf `"$metadata`" %*"
    ) | Set-Content $wrapper -Encoding ascii
    $signCommand = "$wrapper `$f"
    Write-Host "Signing enabled: $($signtool.FullName)"
}

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

# --- Sign the project's own binaries (the runtime files are Microsoft-signed already) ---
if ($Sign) {
    # Case-sensitive on purpose: conwatch*.exe/dll and the Con<Pascal> project
    # assemblies, never a third-party "con..." library that happens to ship unsigned.
    $ours = @(Get-ChildItem $app -File -Include *.exe, *.dll -Recurse |
        Where-Object { $_.Name -cmatch "^(conwatch|Con[A-Z])" } |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne "Valid" })
    if ($ours.Count -eq 0) { throw "No files under $app matched the signing name pattern." }
    Write-Host "Signing $($ours.Count) file(s): $($ours.Name -join ', ')"
    & (Join-Path $staging "sign.cmd") @($ours.FullName)
    if ($LASTEXITCODE -ne 0) { throw "Signing failed (exit $LASTEXITCODE)." }
    $unsigned = $ours | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne "Valid" }
    if ($unsigned) { throw "Still unsigned after signing: $($unsigned.Name -join ', ')" }
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

$isccArgs = @("/DAppVersion=$Version", "/DStagingDir=$app", "/O$dist")
if ($Sign) { $isccArgs += @("/DSign", "/Ssigntool=$signCommand") }
& $iscc @isccArgs (Join-Path $PSScriptRoot "deploy\windows\installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Join-Path $dist "ConnectionWatcher-Setup-$Version.exe"
if ($Sign -and (Get-AuthenticodeSignature $setup).Status -ne "Valid") { throw "Setup exe is not validly signed: $setup" }

$zip = Join-Path $dist "ConnectionWatcher-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip

Write-Host ""
Write-Host "Installer ready: $setup" -ForegroundColor Green
Write-Host "Zip ready:       $zip" -ForegroundColor Green
if ($Sign) { Write-Host "Signed with Azure Artifact Signing." -ForegroundColor Green }
