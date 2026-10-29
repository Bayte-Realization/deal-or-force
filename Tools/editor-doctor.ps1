# Why the editor takes as long as it does, and what to do about it.
#
# Three things decide how long Godot takes to open this project:
#
#   1. How many files the editor walks. It scans `res://` recursively, follows directory
#      junctions, and imports anything that is an image. A scratch folder inside the project is
#      scanned like content; a probe project made of junctions back into the real folders makes
#      every start walk the whole project twice. An empty `.gdignore` in a folder makes Godot
#      skip that folder completely.
#   2. The import cache in `.godot/imported`, one or two files per imported asset. Godot checks
#      every entry on start, so a cache full of imports for files that no longer exist is paid
#      for on every open and never cleaned up by itself.
#   3. The C# assembly. If `.godot/mono/temp/bin/Debug/DealOrForce.dll` is older than the newest
#      `.cs`, the editor compiles on start - a minute or more of `dotnet build`.
#
# Usage:
#   Tools\editor-doctor.ps1            report only
#   Tools\editor-doctor.ps1 -Clean     rebuild the import cache and the editor's file cache
#   Tools\editor-doctor.ps1 -Time      measure one headless editor start and print the seconds
#
# -Clean and -Time refuse to run while an editor has the project open: it holds every
# GDExtension DLL, so the run cannot copy them and would measure a broken start.
param(
    [switch]$Clean,
    [switch]$Time,
    [switch]$Force,
    [string]$Godot
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot

function Find-Godot {
    if ($Godot) { return $Godot }
    if ($env:GODOT_EXECUTABLE) { return $env:GODOT_EXECUTABLE }
    $config = Join-Path $project '.mcp.json'
    if (Test-Path -LiteralPath $config) {
        try {
            $path = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).mcpServers.'godot-coding-solo'.env.GODOT_PATH
            if ($path -and (Test-Path -LiteralPath $path)) { return $path }
        } catch { }
    }
    return 'C:\GodotEngineNet 4.8-Beta2\Godot_v4.8-beta2_mono_win64_console.exe'
}

function Count-Files([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $out = cmd /c ("dir /s /b `"{0}`" 2>nul | find /c /v `"`"" -f $path)
    $n = 0
    [int]::TryParse(($out -join '').Trim(), [ref]$n) | Out-Null
    return $n
}

function Get-Editors {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^Godot' } |
        Where-Object { try { [bool]$_.MainWindowTitle } catch { $false } } |
        Select-Object Id, ProcessName, MainWindowTitle
}

$ignored = @()
foreach ($dir in Get-ChildItem -LiteralPath $project -Directory -Force) {
    if (Test-Path -LiteralPath (Join-Path $dir.FullName '.gdignore')) { $ignored += $dir.Name }
}

Write-Host '== what the editor walks ==' -ForegroundColor Cyan
foreach ($name in 'game', 'Scripts', 'Scenes', 'addons', 'art', 'Assets', 'docs', 'Tools') {
    $path = Join-Path $project $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    if ($ignored -contains $name) {
        Write-Host ("  {0,-10} {1,7} files  skipped (has .gdignore)" -f $name, (Count-Files $path))
        continue
    }
    Write-Host ("  {0,-10} {1,7} files" -f $name, (Count-Files $path))
}
# Scratch folders are counted only when they are a problem: counting a folder the editor skips
# would cost more than the answer is worth.
$scratch = @('tmp', 'tmp_logs', 'tmp_playtest', '.fennara') | Where-Object { Test-Path -LiteralPath (Join-Path $project $_) }
foreach ($name in $scratch) {
    if ($ignored -contains $name) {
        Write-Host ("  {0,-10} {1,7}         skipped (has .gdignore)" -f $name, '')
    } else {
        Write-Host ("  {0,-10} {1,7} files  SCANNED - needs a .gdignore" -f $name, (Count-Files (Join-Path $project $name))) -ForegroundColor Yellow
    }
}

$imported = Count-Files (Join-Path $project '.godot\imported')
Write-Host ''
Write-Host '== import cache ==' -ForegroundColor Cyan
Write-Host ("  .godot/imported  {0} files" -f $imported)

$assembly = Join-Path $project '.godot\mono\temp\bin\Debug\DealOrForce.dll'
Write-Host ''
Write-Host '== C# assembly ==' -ForegroundColor Cyan
if (Test-Path -LiteralPath $assembly) {
    $built = (Get-Item -LiteralPath $assembly).LastWriteTime
    $newest = Get-ChildItem -LiteralPath (Join-Path $project 'Scripts') -Recurse -Filter *.cs |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host ("  built       {0:yyyy-MM-dd HH:mm}" -f $built)
    Write-Host ("  newest .cs  {0:yyyy-MM-dd HH:mm}  ({1})" -f $newest.LastWriteTime, $newest.Name)
    if ($newest.LastWriteTime -gt $built) {
        Write-Host '  STALE: the editor will compile on start. Run Tools\dev.ps1 -Build first.' -ForegroundColor Yellow
    } else {
        Write-Host '  current: the editor will not compile.' -ForegroundColor Green
    }
} else {
    Write-Host '  missing: the editor will compile from scratch.' -ForegroundColor Yellow
}

$editors = @(Get-Editors)
Write-Host ''
Write-Host '== running editors ==' -ForegroundColor Cyan
if ($editors) {
    $editors | ForEach-Object { Write-Host ("  {0} (pid {1}) {2}" -f $_.ProcessName, $_.Id, $_.MainWindowTitle) }
} else {
    Write-Host '  none'
}

if ($Clean) {
    if ($editors -and -not $Force) {
        Write-Host ''
        Write-Host 'An editor has the project open. Close it first (Tools\close-godot.ps1 -Editors), or pass -Force.' -ForegroundColor Yellow
        exit 1
    }
    Write-Host ''
    Write-Host '== cleaning ==' -ForegroundColor Cyan
    # `rd /s /q` is several times faster than Remove-Item on a directory with tens of thousands
    # of files, and the cache is disposable by definition.
    foreach ($rel in '.godot\imported', '.godot\editor') {
        $path = Join-Path $project $rel
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $before = if ($rel -eq '.godot\imported') { $imported } else { Count-Files $path }
        cmd /c ("rd /s /q `"{0}`"" -f $path) 2>$null
        Write-Host ("  removed {0} ({1} files)" -f $rel, $before)
    }
    # Sidecars for images inside ignored scratch folders would re-import on the next scan.
    foreach ($name in $scratch) {
        $removed = 0
        Get-ChildItem -LiteralPath (Join-Path $project $name) -Recurse -Filter *.import -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; $removed++ }
        if ($removed) { Write-Host ("  removed {0} .import sidecars under {1}" -f $removed, $name) }
    }
    $exe = Find-Godot
    Write-Host '  rebuilding the import cache (one slow pass; later starts are fast)' -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $exe --headless --path $project --import
    $watch.Stop()
    Write-Host ("  import finished in {0:N1} s" -f ($watch.ElapsedMilliseconds / 1000)) -ForegroundColor Green
}

if ($Time) {
    if ($editors -and -not $Force) {
        Write-Host ''
        Write-Host 'An editor has the project open, so a timed start would fail on locked DLLs. Close it first.' -ForegroundColor Yellow
        exit 1
    }
    $exe = Find-Godot
    Write-Host ''
    Write-Host '== timing one editor start ==' -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $exe --headless --editor --quit --path $project
    $watch.Stop()
    Write-Host ("  {0:N1} s" -f ($watch.ElapsedMilliseconds / 1000)) -ForegroundColor Green
}
