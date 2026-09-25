# Fetches the two NuGet packages that Build-Installer.ps1 -Sign needs and
# prints where signtool.exe and the Artifact Signing dlib landed:
#   Microsoft.Windows.SDK.BuildTools  -> signtool.exe (x64)
#   Microsoft.ArtifactSigning.Client  -> Azure.CodeSigning.Dlib.dll (x64)
# Both are extracted under -ToolsDir (default: <repo>\tools\signing, git-ignored).
# Also needs the .NET 8 runtime on the machine, which the dlib loads; the
# GitHub windows runners have it, and so does any machine with the .NET 8 SDK.
# Idempotent: re-running keeps what is already there.
[CmdletBinding()]
param([string]$ToolsDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "tools\signing"))
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force $ToolsDir | Out-Null
$nuget = Join-Path $ToolsDir "nuget.exe"
if (-not (Test-Path $nuget)) {
    Write-Host "Downloading nuget.exe..."
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nuget
}

foreach ($package in "Microsoft.Windows.SDK.BuildTools", "Microsoft.ArtifactSigning.Client") {
    if (-not (Get-ChildItem $ToolsDir -Directory -Filter "$package.*" -ErrorAction SilentlyContinue)) {
        Write-Host "Installing $package..."
        & $nuget install $package -x -OutputDirectory $ToolsDir -NonInteractive | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "nuget install $package failed." }
    }
}

$signtool = Get-ChildItem $ToolsDir -Recurse -Filter signtool.exe |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
$dlib = Get-ChildItem $ToolsDir -Recurse -Filter Azure.CodeSigning.Dlib.dll |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
if (-not $signtool -or -not $dlib) { throw "signtool.exe or Azure.CodeSigning.Dlib.dll not found under $ToolsDir." }

Write-Host "signtool: $($signtool.FullName)"
Write-Host "dlib:     $($dlib.FullName)"
