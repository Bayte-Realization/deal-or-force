# Audit the translation tables for the mistakes that are invisible until a player sees them.
#
#     powershell -ExecutionPolicy Bypass -File Tools\audit-localization.ps1
#     powershell -ExecutionPolicy Bypass -File Tools\audit-localization.ps1 -Fail
#
# Three checks, because they are the three ways a table goes wrong quietly:
#
#   1. The same English key translated twice, differently, in two files. The tables load in file
#      order and the last one wins, so "Refuse" can read one way in the movement panel and
#      another in the army panel - same button, two words, no error anywhere. This is the check
#      that matters most in this repository: 610 such keys were found the first time it ran.
#   2. A placeholder mismatch: the key says {0} and the translation drops it, so the number or
#      the name disappears from the sentence - or Format throws on an argument that is not there.
#   3. An empty value, which the loader treats as translated and the player sees as nothing.
#
# Case-sensitive throughout. `Market` and `MARKET` are two different source strings in this game
# (headings force capitals themselves), and a case-insensitive comparison reported 765 phantom
# duplicates before this file learned the difference.
param([switch]$Fail, [string]$Language = 'russian')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path $project "game\localization\$Language"
if (-not (Test-Path -LiteralPath $root)) { throw "no tables at $root" }

$pair = [regex]'"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"'
$seen = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$conflicts = @()
$placeholders = @()
$empty = @()
$keys = 0

foreach ($file in Get-ChildItem -LiteralPath $root -Filter *.json | Sort-Object Name) {
    $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    foreach ($match in $pair.Matches($text)) {
        $key = $match.Groups[1].Value
        $value = $match.Groups[2].Value
        if ($key.Trim().Length -eq 0) { continue }
        $keys++
        if ($seen.ContainsKey($key)) {
            $first = $seen[$key]
            if ($first.Value -cne $value) {
                $conflicts += [pscustomobject]@{
                    Key = $key; First = $first.File; FirstValue = $first.Value
                    Second = $file.Name; SecondValue = $value
                }
            }
        } else {
            $seen[$key] = @{ File = $file.Name; Value = $value }
        }
        if ($value.Trim().Length -eq 0) { $empty += [pscustomobject]@{ Key = $key; File = $file.Name } }

        $slots = ([regex]::Matches($key, '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object -Unique)
        if ($slots.Count -eq 0) { continue }
        $have = ([regex]::Matches($value, '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object -Unique)
        $missing = $slots | Where-Object { $have -notcontains $_ }
        $extra = $have | Where-Object { $slots -notcontains $_ }
        if ($missing -or $extra) {
            $placeholders += [pscustomobject]@{
                Key = $key; File = $file.Name; Missing = ($missing -join ','); Extra = ($extra -join ',')
            }
        }
    }
}

Write-Host ("{0}: {1} entries in {2}" -f $Language, $keys, (Get-ChildItem -LiteralPath $root -Filter *.json).Count)
Write-Host ("  translated twice, differently   {0}" -f $conflicts.Count) -ForegroundColor $(if ($conflicts.Count) { 'Yellow' } else { 'Green' })
Write-Host ("  placeholder mismatches          {0}" -f $placeholders.Count) -ForegroundColor $(if ($placeholders.Count) { 'Yellow' } else { 'Green' })
Write-Host ("  empty values                    {0}" -f $empty.Count) -ForegroundColor $(if ($empty.Count) { 'Yellow' } else { 'Green' })

$report = Join-Path $project 'tmp_logs\localization-audit.csv'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $report) | Out-Null
$conflicts | Export-Csv -NoTypeInformation -Encoding utf8 -Path $report
Write-Host ("  list written to {0}" -f $report) -ForegroundColor DarkGray

foreach ($row in $conflicts | Select-Object -First 10) {
    Write-Host ("    {0}" -f $row.Key) -ForegroundColor DarkGray
    Write-Host ("      {0}: {1}" -f $row.First, $row.FirstValue) -ForegroundColor DarkGray
    Write-Host ("      {0}: {1}" -f $row.Second, $row.SecondValue) -ForegroundColor DarkGray
}

if ($Fail -and ($conflicts.Count -gt 0 -or $placeholders.Count -gt 0 -or $empty.Count -gt 0)) {
    throw "translation tables have $($conflicts.Count + $placeholders.Count + $empty.Count) problem(s)"
}
