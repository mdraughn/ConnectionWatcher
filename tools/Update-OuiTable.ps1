<#
.SYNOPSIS
  Regenerates Libraries/ConInfrastructure/Discovery/oui.tsv from the Wireshark "manuf" vendor list.
.DESCRIPTION
  Downloads https://www.wireshark.org/download/automated/data/manuf (public data derived from the IEEE
  registries, a few MB) and writes a compact "AABBCC<TAB>Vendor" file, one line per 3-octet OUI, sorted.
  Sub-assignments (xx:xx:xx:xx:xx:xx/28 and similar) are skipped. Run it by hand when you want fresher
  vendor names; the result is committed and embedded in the binary — the app never touches the network
  for this. (IEEE's own oui.csv host refused connections when this was first written, and its
  registration-authority endpoint returns only the first 10,000 rows.)
#>
[CmdletBinding()]
param(
    [string]$Url = 'https://www.wireshark.org/download/automated/data/manuf',
    [string]$Out = (Join-Path $PSScriptRoot '..\Libraries\ConInfrastructure\Discovery\oui.tsv')
)
$ErrorActionPreference = 'Stop'

$temp = Join-Path ([IO.Path]::GetTempPath()) "manuf-$([Guid]::NewGuid()).txt"
try {
    Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    $lines = foreach ($line in [IO.File]::ReadLines($temp)) {
        if ($line -notmatch '^([0-9A-Fa-f]{2}):([0-9A-Fa-f]{2}):([0-9A-Fa-f]{2})\s') { continue }
        $oui = ($Matches[1] + $Matches[2] + $Matches[3]).ToUpperInvariant()
        $cols = $line -split "`t"
        $name = if ($cols.Count -ge 3 -and $cols[2].Trim().Length -gt 0) { $cols[2] } elseif ($cols.Count -ge 2) { $cols[1] } else { '' }
        $name = ($name.Trim() -replace '\s+', ' ')
        if ($name.Length -eq 0) { continue }
        if ($name.Length -gt 48) { $name = $name.Substring(0, 48).TrimEnd() }
        "$oui`t$name"
    }
    $sorted = $lines | Sort-Object -Unique
    [IO.File]::WriteAllLines($Out, $sorted, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $($sorted.Count) entries to $Out"
}
finally {
    Remove-Item -Path $temp -ErrorAction SilentlyContinue
}
