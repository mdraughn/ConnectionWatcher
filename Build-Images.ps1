# Builds the ConnectionWatcher container image for linux/amd64 AND linux/arm64.
#
# Without -Push: builds each architecture into the local Docker image store as
#   conwatch:<tag>-amd64 and conwatch:<tag>-arm64 for inspection/testing.
# With -Push: builds both architectures as ONE multi-arch manifest and pushes
#   it to the registry (docker must already be logged in). Nothing is pushed
#   without this switch.
#
#   .\Build-Images.ps1
#   .\Build-Images.ps1 -Push -Image ghcr.io/<user>/conwatch -Tag 0.9.0
[CmdletBinding()]
param(
    [string]$Image = "conwatch",
    [string]$Tag = "latest",
    [switch]$Push
)
$ErrorActionPreference = "Stop"

if ($Push) {
    # One tag, two platforms; the manifest list means each machine pulls the
    # right architecture invisibly. Also stamps :latest alongside the tag.
    docker buildx build --platform linux/amd64,linux/arm64 `
        -t "${Image}:${Tag}" -t "${Image}:latest" --push $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw "Multi-arch build/push failed." }
    Write-Host "Pushed ${Image}:${Tag} (linux/amd64 + linux/arm64)" -ForegroundColor Green
} else {
    foreach ($arch in "amd64", "arm64") {
        Write-Host "Building linux/$arch..." -ForegroundColor Cyan
        docker buildx build --platform "linux/$arch" -t "${Image}:${Tag}-$arch" --load $PSScriptRoot
        if ($LASTEXITCODE -ne 0) { throw "linux/$arch build failed." }
    }
    Write-Host ""
    Write-Host "Local images: ${Image}:${Tag}-amd64, ${Image}:${Tag}-arm64" -ForegroundColor Green
    Write-Host "Publish with: .\Build-Images.ps1 -Push -Image <registry>/conwatch -Tag <version>"
}
