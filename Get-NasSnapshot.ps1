# Fetches the sanitized full snapshot from a running ConnectionWatcher and
# saves it for the real-data test suite (RealDataSnapshotTests picks it up
# automatically; without it those tests skip). Secrets are redacted
# server-side (SettingKeys.SecretKeys) and never reach this machine.
[CmdletBinding()]
param([string]$Url = "http://nas.example.com:8080")
$ErrorActionPreference = "Stop"

$out = Join-Path $PSScriptRoot "testdata\nas-snapshot.json"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
Invoke-WebRequest "$Url/api/v1/export/snapshot" -OutFile $out -TimeoutSec 60

$snapshot = Get-Content $out -Raw | ConvertFrom-Json
Write-Host ("Snapshot saved: {0:N0} KB — {1} targets, {2} transitions, {3} rollups, {4} glitches (redacted: {5})" -f `
    ((Get-Item $out).Length / 1KB), $snapshot.config.targets.Count, $snapshot.transitions.Count, `
    $snapshot.rollups.Count, $snapshot.glitches.Count, ($snapshot.redactedSettings -join ", ")) -ForegroundColor Green
