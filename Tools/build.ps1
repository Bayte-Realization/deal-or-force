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
# .pck that System.IO cannot see, so the data has to arrive as real files inside the build.
# It does - the .csproj copies it into the published output, which Godot puts in the
# data_DealOrForce_windows_x86_64 folder beside the executable, and the loader walks up from
# its own assembly to find it. But if that ever stops being true the symptom is a game that
# starts, shows an empty world and says nothing, on somebody else's machine. So the build
# refuses to hand you a folder that would do that.

param(
    [string]$Godot = "C:\GodotEngineNet\Godot_v4.7.2-stable_mono_win64.exe",
    [string]$Preset = "Windows Desktop",
    [switch]$Debug,
    [switch]$NoZip,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
# The folder that ships, named the way it should look on somebody else's desktop. It is also
# the folder that goes into the archive whole - an archive whose entries sit at its root
# empties 572 files into whatever directory it is opened next to, which is a poor first
# impression and an easy way for somebody to end up running the game from their Downloads.
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $project "build\Deal or Force" }
$allowedBuildRoot = [IO.Path]::GetFullPath((Join-Path $project "build")) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($out)).StartsWith($allowedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Export output must be a subdirectory of this project build folder.'
}
$exe = Join-Path $out "Deal or Force.exe"

if (-not (Test-Path $Godot)) {
    throw "Godot not found at $Godot. Pass -Godot <path to Godot_v4.7.2-stable_mono_win64.exe>."
}

# Godot's .NET export wants a solution file named after `dotnet/project/assembly_name`, and
# without one it packs the game, reports a warning rather than an error, and hands you a
# build with no managed assemblies in it - which starts and immediately does nothing.
$solution = Join-Path $project "DealOrForce.sln"
if (-not (Test-Path $solution)) {
    throw "No DealOrForce.sln. Godot needs a solution named after dotnet/project/assembly_name " +
          "in project.godot; without it the export silently produces a build with no C# in it."
}

# There are two dotnet installations on a lot of Windows machines and PATH often finds the
# wrong one first: `C:\Program Files (x86)\dotnet` is a 32-bit host with no SDK at all. Godot
# usually resolves the real one through hostfxr anyway, but making it certain costs a line.
$sdk = "C:\Program Files\dotnet"
if (Test-Path (Join-Path $sdk "dotnet.exe")) { $env:PATH = "$sdk;$env:PATH" }

Write-Host "== exporting ($(if ($Debug) { 'debug' } else { 'release' })) ==" -ForegroundColor Cyan

# The build output lands inside the project, which is convenient and would otherwise be a
# trap: Godot scans every folder under the project and would import three hundred and fifty
# megabytes of finished game as though it were source art, then pack the result into the next
# build. A `.gdignore` makes the editor skip the folder entirely, and it has to exist before
# the editor ever sees the folder, so it is written on the way in rather than on the way out.
$buildRoot = Join-Path $project "build"
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$ignore = Join-Path $buildRoot ".gdignore"
if (-not (Test-Path $ignore)) { New-Item -ItemType File -Path $ignore | Out-Null }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Godot will not create the export directory itself: "Prepare Template: The given export
# path doesn't exist."
New-Item -ItemType Directory -Force -Path $out | Out-Null

$mode = if ($Debug) { "--export-debug" } else { "--export-release" }
# Godot writes a good deal to stderr on a perfectly successful export - deprecation notes,
# the "safe save" grumble, its own plugins' chatter - and with ErrorActionPreference set to
# Stop that ends the script before any of the checks run. Its output is information here,
# not failure, so the preference steps aside for exactly this one call.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $Godot --headless --path $project $mode $Preset $exe 2>&1 | Out-Host
$ErrorActionPreference = $previousPreference

# Godot's exit code is not worth believing here: a headless export that finished perfectly
# well leaves $LASTEXITCODE empty, and one that failed to build the C# still reports its own
# failure as a warning. So it is noted and not acted on - what decides whether this build is
# any good is the three checks below, which look at what is actually in the folder.
if ($LASTEXITCODE) { Write-Host "  (Godot exited with $LASTEXITCODE)" -ForegroundColor DarkGray }
if (-not (Test-Path $exe)) { throw "Godot produced no executable at $exe." }

# ---------------------------------------------------------------- somewhere to put a mod
# The one folder in the build that belongs to the player rather than to us. Content packs
# travel with the data because they are content, but a mod folder buried a level down among
# `data_DealOrForce_windows_x86_64` is a mod folder nobody finds, so this one sits beside the
# executable. The loader looks in both places.
$modsSource = Join-Path $project "mods"
if (Test-Path $modsSource) {
    Copy-Item -Path $modsSource -Destination $out -Recurse -Force
    # A template that somebody once compiled leaves its bin and obj behind; they are not
    # part of the template and they are not small.
    Get-ChildItem -Path (Join-Path $out "mods") -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- content, beside the game
# Content packs sit beside the executable, where somebody looking for them will look.
$dlcSource = Join-Path $project "dlc"
if (Test-Path $dlcSource) { Copy-Item -LiteralPath $dlcSource -Destination $out -Recurse -Force }
& (Join-Path $PSScriptRoot "build-launcher.ps1") -OutputDirectory $out

# ---------------------------------------------------------------- one folder of each
# What .NET leaves behind is a folder of assemblies with the game's entire world inside it,
# because the csproj copies `game/**` into the publish output and Godot puts that output in
# `data_<project>_<platform>`. That is the right place for DealOrForce.dll and the wrong one
# for the map: a player opening the build sees `mods` beside the executable, `dlc` twice, and
# no `game` at all until they go looking inside a folder named after a build target.
#
# So `game` comes up to the root. Nothing in the code has to change for it - the loader looks
# for a root that CONTAINS a `game/` folder and walks up from the assembly to find one - and
# it is what makes the duplicate `dlc` go away rather than merely be deleted: with `game` gone
# from `data_...`, that folder no longer qualifies as an install root, so the copy of `dlc`
# inside it is not somewhere the game would look any more.
$dataDirectory = Get-ChildItem -Path $out -Directory -Filter "data_*" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($dataDirectory) {
    $dataGame = Join-Path $dataDirectory.FullName "game"
    $rootGame = Join-Path $out "game"
    if (Test-Path $dataGame) {
        if (Test-Path $rootGame) { Remove-Item -LiteralPath $rootGame -Recurse -Force }
        Move-Item -LiteralPath $dataGame -Destination $rootGame -Force
        Write-Host "  game moved beside the executable" -ForegroundColor DarkGray
    }
    $dataDlc = Join-Path $dataDirectory.FullName "dlc"
    if ((Test-Path $dataDlc) -and (Test-Path (Join-Path $out "dlc"))) {
        Remove-Item -LiteralPath $dataDlc -Recurse -Force
        Write-Host "  the second copy of dlc removed" -ForegroundColor DarkGray
    }
    $dataMods = Join-Path $dataDirectory.FullName "mods"
    if ((Test-Path $dataMods) -and (Test-Path (Join-Path $out "mods"))) {
        Remove-Item -LiteralPath $dataMods -Recurse -Force
        Write-Host "  the second copy of mods removed" -ForegroundColor DarkGray
    }
}

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
    "game\map_data\default.map",
    # Content packs are read from disk like everything else, so they have to be on disk. The
    # first build shipped without them and the game simply had no content packs at all - it
    # said so quietly in the log and carried on, which is exactly how this goes unnoticed.
    "dlc\entitlements.json",
    "dlc\dof_iron_and_grain\dlc.json",
    "mods"
)

$assemblies = Get-ChildItem -Path $out -Recurse -Force -Filter "DealOrForce.dll" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $assemblies) {
    Write-Host ""
    Write-Host "The build has no managed assembly in it - the game is C# and would start into" -ForegroundColor Red
    Write-Host "nothing. Godot reports this as a warning rather than an error, so read the export" -ForegroundColor Red
    Write-Host "output above: the usual cause is a missing or misnamed .sln." -ForegroundColor Red
    throw "Build has no C#."
}
Write-Host "  managed assembly present: $($assemblies.Directory.Name)" -ForegroundColor Green

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
    Write-Host "<Content Include=`"game\**`" /> items in DealOrForce.csproj and then moved up" -ForegroundColor Yellow
    Write-Host "beside the executable by this script; check the Content items first." -ForegroundColor Yellow
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
    Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
    $zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "  $zip ($zipSize MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. The archive unpacks to a single folder called 'Deal or Force'; the game is" -ForegroundColor Cyan
Write-Host "the .exe inside it. It reads its world from the folders beside itself, so moving" -ForegroundColor Cyan
Write-Host "the .exe out on its own leaves it with nothing to load." -ForegroundColor Cyan
