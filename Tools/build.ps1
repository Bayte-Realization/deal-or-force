# Build a copy of the game somebody else can run.
#
# Exports, then checks the one thing about this project that an export can silently get
# wrong, then zips. Run it from anywhere:
#
#     powershell -ExecutionPolicy Bypass -File Tools\build.ps1
#
# The check is not ceremony. Almost everything the game reads at runtime - every file under
# `game/`, which is the whole economy, the whole map and every scenario - is read through
# System.IO by filesystem path, not through Godot's `res://`. In the editor that works
# because the working directory is the project. In an exported build `res://` lives inside a
# .pck that System.IO cannot see, so the data has to arrive as real files next to the
# executable. It does, because the .csproj copies it - but if that ever stops being true the
# symptom is a game that starts, shows an empty world and says nothing, on somebody else's
# machine. So the build refuses to hand you a folder that would do that.

param(
    [string]$Godot = "C:\GodotEngineNet\Godot_v4.7.2-stable_mono_win64.exe",
    [string]$Preset = "Windows Desktop",
    [switch]$Debug,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$out = Join-Path $project "build\windows"
$exe = Join-Path $out "Deal or Force.exe"

if (-not (Test-Path $Godot)) {
    throw "Godot not found at $Godot. Pass -Godot <path to Godot_v4.7.2-stable_mono_win64.exe>."
}

Write-Host "== exporting ($(if ($Debug) { 'debug' } else { 'release' })) ==" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$mode = if ($Debug) { "--export-debug" } else { "--export-release" }
& $Godot --headless --path $project $mode $Preset $exe
if ($LASTEXITCODE -ne 0) { throw "Godot export failed with exit code $LASTEXITCODE." }
if (-not (Test-Path $exe)) { throw "Godot reported success but produced no executable at $exe." }

# ---------------------------------------------------------------- the check that matters
Write-Host "== checking the data came with it ==" -ForegroundColor Cyan

# Where the game will look: its own folder, then up to seven parents. Anything below the
# build folder counts, because that is what ships.
$needed = @(
    "game\common\goods",
    "game\common\buildings",
    "game\common\production",
    "game\common\defines",
    "game\history\provinces",
    "game\localization\english",
    "game\map_data\definition.csv",
    "game\map_data\default.map"
)

$missing = @()
foreach ($item in $needed) {
    $found = Get-ChildItem -Path $out -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\$item" } | Select-Object -First 1
    if (-not $found) { $missing += $item }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "The build is missing data the game reads by filesystem path:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "This build would start and show an empty world. The data is copied by the" -ForegroundColor Yellow
    Write-Host "<Content Include=`"game\**`" /> items in DealOrForce.csproj; check those first." -ForegroundColor Yellow
    throw "Build incomplete."
}

$size = [math]::Round((Get-ChildItem -Path $out -Recurse -Force | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "  every data folder the game needs is in the build ($size MB)" -ForegroundColor Green

# ---------------------------------------------------------------- something to send
if (-not $NoZip) {
    $stamp = Get-Date -Format "yyyy-MM-dd"
    $zip = Join-Path $project "build\Deal or Force $stamp.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Write-Host "== zipping ==" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
    $zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "  $zip ($zipSize MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Tell whoever you send it to: unzip the whole folder and run the .exe from" -ForegroundColor Cyan
Write-Host "inside it - the game reads its data from the folders beside the executable, so" -ForegroundColor Cyan
Write-Host "moving the .exe out on its own leaves it with no world to load." -ForegroundColor Cyan
