<#
.SYNOPSIS
  Regenerates Libraries/ConInfrastructure/Discovery/oui.tsv from the IEEE MA-L (OUI) registry.
.DESCRIPTION
  Downloads https://standards-oui.ieee.org/oui/oui.csv (the IEEE Registration Authority's public MA-L
  listing, about 4 MB) and writes a compact "AABBCC<TAB>Vendor" file, one line per 24-bit OUI, sorted.
  Only the MA-L registry is used; the smaller MA-M/MA-S blocks are not. Run it by hand when you want
  fresher vendor names; the result is committed and embedded in the binary — the app never touches the
  network for this. See THIRD-PARTY-NOTICES.md for the data's source.
#>
[CmdletBinding()]
param(
    [string]$Url = 'https://standards-oui.ieee.org/oui/oui.csv',
    [string]$Out = (Join-Path $PSScriptRoot '..\Libraries\ConInfrastructure\Discovery\oui.tsv')
)
$ErrorActionPreference = 'Stop'

$temp = Join-Path ([IO.Path]::GetTempPath()) "oui-$([Guid]::NewGuid()).csv"
try {
    Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    # Columns: Registry, Assignment (6 hex digits), Organization Name, Organization Address.
    $lines = foreach ($row in (Import-Csv -Path $temp -Encoding UTF8)) {
        if ($row.Registry -ne 'MA-L') { continue }
        $oui = "$($row.Assignment)".Trim().ToUpperInvariant()
        if ($oui -notmatch '^[0-9A-F]{6}$') { continue }
        # A handful of registry names carry HTML entities ("R&amp;M AG"); decode them.
        $name = ([Net.WebUtility]::HtmlDecode("$($row.'Organization Name')").Trim() -replace '\s+', ' ')
        if ($name.Length -eq 0) { continue }
        # About a fifth of the registry is ALL CAPS; shouting names read badly in the UI. Recase those
        # (and only those): each letter that follows a non-letter starts a word, apostrophes excepted,
        # so "HUAWEI TECHNOLOGIES CO.,LTD" -> "Huawei Technologies Co.,Ltd" and "3COM" -> "3Com".
        if ($name -cnotmatch '\p{Ll}') {
            $name = [regex]::Replace($name.ToLowerInvariant(), "(?<![\p{L}'])\p{L}", { $args[0].Value.ToUpperInvariant() })
        }
        if ($name.Length -gt 48) { $name = $name.Substring(0, 48).TrimEnd() }
        "$oui`t$name"
    }
    $sorted = $lines | Sort-Object -Unique
    if ($sorted.Count -lt 10000) { throw "Only $($sorted.Count) entries parsed from $Url — refusing to overwrite $Out." }
    [IO.File]::WriteAllLines($Out, [string[]]$sorted, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $($sorted.Count) entries to $Out"
}
finally {
    Remove-Item -Path $temp -ErrorAction SilentlyContinue
}
