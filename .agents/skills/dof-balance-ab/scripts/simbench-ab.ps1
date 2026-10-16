<#
.SYNOPSIS
    A/B balance run for Deal or Force: the same seeds, with and without a set of --define
    overrides, and a table of the SimBench checks whose verdict changed.

.DESCRIPTION
    SimBench takes `<days> <seed> [--define key=value]...` (see Tools/SimBench/Program.cs).
    One seed proves nothing about balance, so this runs several and reports per seed:
      - checks that flipped ok -> FAIL or FAIL -> ok between baseline and variant
      - the FAIL count of each side
    Logs go to .fennara/simbench/<label>/ so a later pass can diff them again.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .agents\skills\dof-balance-ab\scripts\simbench-ab.ps1 `
        -Days 365 -Seeds 1,555,730 -Define 'WarExhaustionRate=0.8' -Label war-exhaustion

.EXAMPLE
    # Only compare two folders that already exist.
    ... simbench-ab.ps1 -CompareOnly -Label war-exhaustion
#>
param(
    [int]$Days = 365,
    [int[]]$Seeds = @(1, 555, 730),
    [string[]]$Define = @(),
    [string]$Label = ('ab-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [switch]$CompareOnly
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
Set-Location $project
$bench = 'Tools/SimBench/SimBench.csproj'
$root = Join-Path $project ".fennara/simbench/$Label"
New-Item -ItemType Directory -Force (Join-Path $root 'baseline'), (Join-Path $root 'variant') | Out-Null

function Read-Checks([string]$path) {
    $map = [ordered]@{}
    if (-not (Test-Path $path)) { return $map }
    foreach ($line in Get-Content $path -Encoding UTF8) {
        if ($line -match '^\s*\[\s*(ok|FAIL)\s*\]\s*(.+?)\s*$') {
            # A failing check appends its live numbers after ": "; the name before it is stable.
            $name = ($Matches[2] -split ': ', 2)[0]
            $map[$name] = $Matches[1]
        }
    }
    return $map
}

if (-not $CompareOnly) {
    Write-Host "Building SimBench (Release)..."
    dotnet build -c Release $bench -v quiet -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SimBench build failed. Run: dotnet build -c Release $bench" }

    $defineArgs = @()
    foreach ($d in $Define) { $defineArgs += @('--define', $d) }

    foreach ($seed in $Seeds) {
        foreach ($side in 'baseline', 'variant') {
            $out = Join-Path $root "$side/seed-$seed.log"
            $extra = if ($side -eq 'variant') { $defineArgs } else { @() }
            Write-Host ("{0,-8} seed {1,-6} {2} day(s) {3}" -f $side, $seed, $Days, ($extra -join ' '))
            & dotnet run -c Release --no-build --project $bench -- $Days $seed @extra *> $out
        }
    }
}

$totalFlips = 0
foreach ($seed in $Seeds) {
    $a = Read-Checks (Join-Path $root "baseline/seed-$seed.log")
    $b = Read-Checks (Join-Path $root "variant/seed-$seed.log")
    $failA = @($a.Values | Where-Object { $_ -eq 'FAIL' }).Count
    $failB = @($b.Values | Where-Object { $_ -eq 'FAIL' }).Count
    Write-Host ""
    Write-Host ("seed {0}: baseline {1} checks / {2} FAIL   variant {3} checks / {4} FAIL" -f $seed, $a.Count, $failA, $b.Count, $failB)
    foreach ($name in (@($a.Keys) + @($b.Keys) | Select-Object -Unique)) {
        $va = if ($a.Contains($name)) { $a[$name] } else { '-' }
        $vb = if ($b.Contains($name)) { $b[$name] } else { '-' }
        if ($va -ne $vb) { $totalFlips++; Write-Host ("  {0,-4} -> {1,-4} {2}" -f $va, $vb, $name) }
    }
}
Write-Host ""
Write-Host "FLIPPED=$totalFlips  logs: $root"
