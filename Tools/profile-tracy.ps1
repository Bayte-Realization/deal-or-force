<#
.SYNOPSIS
    Record a Tracy timeline of a campaign save, headless, and print its heaviest zones.

.DESCRIPTION
    SimProfiler adds up where a day's time goes; Tracy shows one day at a time, stage by stage,
    on a timeline. This script makes that one command (docs/PROFILING_2026_11_17_RU.md):

      1. builds SimBench with the Tracy binding into tmp_logs\tracy (never into .godot);
      2. starts tracy-capture, which waits for the game's client on 127.0.0.1;
      3. runs `SimBench --profile-save <save> <days>` with DOF_TRACY=1, so every
         SimProfiler.Measure scope becomes a zone and every finished day a "sim day" frame;
      4. writes the .tracy file and two CSVs from tracy-csvexport: inclusive and self time.

    Open the .tracy file in tracy-profiler.exe (File > Open) to read the timeline.

    The viewer must be the same protocol as the client inside the Tracy-CSharp package
    (0.13.1). Newer viewers (0.15) refuse an older client, so the tools are looked for in
    D:\Godot Projects\deal-or-force-tools\tracy\0.13.1 unless -TracyDirectory says otherwise.

    The save is only read: --profile-save loads it into memory and never writes it back.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\profile-tracy.ps1 -Save tmp_logs\perf\bozhe.bdfsave -Days 30
#>
param(
    [Parameter(Mandatory = $true)][string]$Save,
    [int]$Days = 30,
    [string]$TracyDirectory = 'D:\Godot Projects\deal-or-force-tools\tracy\0.13.1',
    [string]$Label = 'tracy',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
Set-Location $project

$capture = Join-Path $TracyDirectory 'tracy-capture.exe'
$export = Join-Path $TracyDirectory 'tracy-csvexport.exe'
if (-not (Test-Path -LiteralPath $capture)) { throw "tracy-capture.exe not found in $TracyDirectory" }
$savePath = (Resolve-Path -LiteralPath $Save).Path

$artifacts = Join-Path $project "tmp_logs\$Label"
if (-not $NoBuild) {
    & dotnet build Tools\SimBench\SimBench.csproj -c Release -p:DofTracy=true --artifacts-path $artifacts -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'SimBench build with Tracy failed' }
}
$bench = Join-Path $artifacts 'bin\SimBench\release\SimBench.exe'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $project "tmp_logs\tracy-$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$trace = Join-Path $out 'capture.tracy'

# The capture tool first, so it is already listening when the client starts; -f overwrites.
$listener = Start-Process -FilePath $capture -ArgumentList @('-o', "`"$trace`"", '-a', '127.0.0.1', '-f') `
    -RedirectStandardOutput (Join-Path $out 'capture.log') -NoNewWindow -PassThru
Start-Sleep -Seconds 1

# TRACY_NO_EXIT keeps the bench alive at the end until the capture tool has every event;
# without it a short run can exit with its last zones still in the client's queue.
$env:DOF_TRACY = '1'
$env:TRACY_NO_EXIT = '1'
try {
    # Out-File rather than *>: Windows PowerShell 5 writes UTF-16 with *>, which grep cannot read.
    & $bench 1 1 --profile-save $savePath $Days 2>&1 | Out-File -Encoding utf8 (Join-Path $out 'simbench.log')
} finally {
    Remove-Item Env:DOF_TRACY -ErrorAction SilentlyContinue
    Remove-Item Env:TRACY_NO_EXIT -ErrorAction SilentlyContinue
}
$listener.WaitForExit(120000) | Out-Null

& $export $trace | Out-File -Encoding utf8 (Join-Path $out 'zones-inclusive.csv')
& $export -e $trace | Out-File -Encoding utf8 (Join-Path $out 'zones-self.csv')
# Every zone with its start time (for cutting the run to the profiled days) and the plots:
# managed heap, allocation per day and gen2 count, one row per "sim day" frame mark.
& $export -u $trace | Out-File -Encoding utf8 (Join-Path $out 'zones-unwrapped.csv')
& $export -u -p $trace | Select-String -NotMatch 'SimProfiler.Measure' | ForEach-Object Line |
    Out-File -Encoding utf8 (Join-Path $out 'plots.csv')

Write-Host "Trace: $trace"
Write-Host 'Heaviest zones by self time over the whole run (load, warm-up and every probe included):'
Import-Csv (Join-Path $out 'zones-self.csv') |
    Sort-Object { [double]$_.total_ns } -Descending | Select-Object -First 15 |
    ForEach-Object { [string]::Format([cultureinfo]::InvariantCulture, '{0,-36} {1,10:F1} ms  x{2}', $_.name.Trim(), ([double]$_.total_ns / 1e6), $_.counts) }
