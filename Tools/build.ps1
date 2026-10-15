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
    [string]$Godot = $env:GODOT_EXECUTABLE,
    [string]$Preset = "Windows Desktop",
    [switch]$Debug,
    [switch]$NoZip,
    [switch]$IncludeExperimentalNativeLuau,
    [switch]$SkipRuntimeVerification,
    [string]$OutputDirectory,
    [switch]$ReplaceExistingOutput
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'PlayerModPackaging.ps1')
$project = Split-Path -Parent $PSScriptRoot
# The folder that ships, named the way it should look on somebody else's desktop. It is also
# the folder that goes into the archive whole - an archive whose entries sit at its root
# empties 572 files into whatever directory it is opened next to, which is a poor first
# impression and an easy way for somebody to end up running the game from their Downloads.
$stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $project "build\$stamp\Deal or Force" }
$allowedBuildRoot = [IO.Path]::GetFullPath((Join-Path $project "build")) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($out)).StartsWith($allowedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Export output must be a subdirectory of this project build folder.'
}
$exe = Join-Path $out "Deal or Force.exe"

if (-not $Godot -or -not (Test-Path -LiteralPath $Godot)) {
    throw "Pass -Godot <matching Godot .NET executable>, or set GODOT_EXECUTABLE. This branch requires the engine version declared in DealOrForce.csproj."
}
$engineVersion = (& $Godot --version | Select-Object -Last 1).Trim()
$sdkVersion = ([xml](Get-Content (Join-Path $project 'DealOrForce.csproj') -Raw)).Project.Sdk
if (($sdkVersion -match '/(?<major>\d+)\.(?<minor>\d+)') -and
    -not $engineVersion.StartsWith("$($Matches.major).$($Matches.minor).", [StringComparison]::Ordinal)) {
    throw "Engine $engineVersion does not match SDK $sdkVersion. Do not export the 4.8 branch with the 4.7 editor."
}

function Assert-BuildChild([string]$Target) {
    $resolved = [IO.Path]::GetFullPath($Target)
    if (-not $resolved.StartsWith($allowedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the build folder: $resolved"
    }
    return $resolved
}

# Never descend into a private agent checkout, cache or compiled template output.
# Filtering before recursion also prevents these trees leaking into the .pck.
function Copy-PlayerTree([string]$Source, [string]$Destination, [string[]]$ExcludedNames = @()) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -in @('bin', 'obj', 'build') -or $item.Name -in $ExcludedNames -or
            ($item.Name.StartsWith('.') -and $item.Name -ne '.gdignore') -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) { Copy-PlayerTree $item.FullName $target $ExcludedNames }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target }
    }
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

Write-Host '== checking source compilation before copying/exporting ==' -ForegroundColor Cyan
& dotnet build (Join-Path $project 'DealOrForce.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Source compilation failed; no export was started.' }

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

if (Test-Path -LiteralPath $out) {
    if (-not $ReplaceExistingOutput) { throw "Output already exists: $out. Choose a new directory, or explicitly pass -ReplaceExistingOutput." }
    $checkedOutput = Assert-BuildChild $out
    Remove-Item -LiteralPath $checkedOutput -Recurse -Force
}
# Godot will not create the export directory itself: "Prepare Template: The given export
# path doesn't exist."
New-Item -ItemType Directory -Force -Path $out | Out-Null

$mode = if ($Debug) { "--export-debug" } else { "--export-release" }

# ------------------------------------------------ the editor's tooling stays in the editor
# Godot AI and Fennara register runtime helpers as autoloads in project.godot whenever their
# plugins are enabled, so deleting those lines by hand does not hold - the plugin writes them
# straight back. The scripts they point at live under addons/, which export_presets excludes,
# and the result is a shipped build carrying autoloads whose scripts are not in it: the
# player's log opens with a load failure on every single run. Development hooks also have no
# business running inside somebody else's copy of the game.
#
# Export a dedicated staging copy. Live editors can keep saving project.godot;
# there is no temporary mutation/restoration race and Godot owns serialization.
$stage = Assert-BuildChild (Join-Path $buildRoot (".export-" + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($folder in @('Scripts', 'Scenes', 'game')) {
    Copy-PlayerTree (Join-Path $project $folder) (Join-Path $stage $folder)
}
# The export-verification probe is game code that lives under Tools/ (see DealOrForce.csproj).
# It is compiled into the assembly, so the staged copy the export builds from has to carry it,
# or the staged project would not compile and the release gate would fail for the wrong reason.
Copy-PlayerTree (Join-Path $project 'Tools\ExportVerification') (Join-Path $stage 'Tools\ExportVerification')
foreach ($file in @('DealOrForce.csproj', 'DealOrForce.sln', 'export_presets.cfg', 'icon.ico', 'icon_small.png')) {
    Copy-Item -LiteralPath (Join-Path $project $file) -Destination $stage
}
foreach ($file in @('NuGet.Config', 'nuget.config', 'global.json', 'Directory.Build.props', 'Directory.Build.targets')) {
    $source = Join-Path $project $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $stage -Force }
}
# The locally installed editor Luau DLL can load in a release template but
# corrupt its heap during shutdown. Keep it available to the live editor, not
# implicitly enabled in everybody's build. Workers/managed mods are unaffected.
if (-not $IncludeExperimentalNativeLuau) {
    $stagedLuau = Join-Path $stage 'game\extensions\luau'
    if (Test-Path -LiteralPath $stagedLuau) {
        Get-ChildItem -LiteralPath $stagedLuau -File -Force |
            Where-Object { $_.Name -in @('luau_script.gdextension', 'luau_script.gdextension.uid') -or $_.Extension -eq '.dll' -or $_.Name.StartsWith('~') } |
            ForEach-Object {
                $checkedAdapterCopy = Assert-BuildChild $_.FullName
                Remove-Item -LiteralPath $checkedAdapterCopy -Force
            }
        Write-Host '  experimental editor Luau binaries omitted from staging; live adapter unchanged' -ForegroundColor DarkGray
    }
}
# Import remaps are identical because resource paths are identical. Reuse the
# imported source assets but never editor/plugin state or native extension lists.
$imported = Join-Path $project '.godot\imported'
if (Test-Path -LiteralPath $imported) {
    New-Item -ItemType Directory -Path (Join-Path $stage '.godot') | Out-Null
    $stagedImports = Join-Path $stage '.godot\imported'
    New-Item -ItemType Directory -Path $stagedImports | Out-Null
    # Copy only cache files referenced by the staged assets. A developer's cache also
    # contains tens of thousands of addon imports that the player project never uses.
    $requiredImports = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($settings in Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.import') {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($settings.FullName), 'res://\.godot/imported/([^"/\\]+)')) {
            $cacheName = $match.Groups[1].Value
            $null = $requiredImports.Add($cacheName)
            $null = $requiredImports.Add([IO.Path]::GetFileNameWithoutExtension($cacheName) + '.md5')
        }
    }
    foreach ($cacheName in $requiredImports) {
        $sourceCache = Join-Path $imported $cacheName
        if (Test-Path -LiteralPath $sourceCache -PathType Leaf) {
            Copy-Item -LiteralPath $sourceCache -Destination $stagedImports
        }
    }
    Write-Host "  reused $($requiredImports.Count) required import-cache entries, not the editor addon cache" -ForegroundColor DarkGray
}

# Godot writes a good deal to stderr on a perfectly successful export - deprecation notes,
# the "safe save" grumble, its own plugins' chatter - and with ErrorActionPreference set to
# Stop that ends the script before any of the checks run. Its output is information here,
# not failure, so the preference steps aside for exactly this one call.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & $Godot --headless --path $project --script res://Tools/prepare_export_project.gd -- (Join-Path $stage 'project.godot') 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Unable to serialize the isolated export configuration.' }
    & $Godot --headless --editor --path $stage --import 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Isolated project import failed.' }
    & $Godot --headless --path $stage $mode $Preset $exe 2>&1 | Out-Host
    $exportExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
    Write-Host "  isolated export project retained at $stage" -ForegroundColor DarkGray
}

# Reject a failed engine process AND inspect the actual output. Godot may still
# report an unsuccessful managed publish as an export warning rather than an error.
if ($exportExitCode -ne 0) { throw "Godot export failed with exit code $exportExitCode." }
if (-not (Test-Path $exe)) { throw "Godot produced no executable at $exe." }

# ---------------------------------------------------------------- somewhere to put a mod
# The one folder in the build that belongs to the player rather than to us. Content packs
# travel with the data because they are content, but a mod folder buried a level down among
# `data_DealOrForce_windows_x86_64` is a mod folder nobody finds, so this one sits beside the
# executable. The loader looks in both places.
$modsSource = Join-Path $project "mods"
if (Test-Path $modsSource) {
    New-Item -ItemType Directory -Path (Join-Path $out 'mods') -Force | Out-Null
    foreach ($taskModFolder in Get-BaseBuildModFolders $modsSource) {
        Copy-PlayerTree $taskModFolder.FullName (Join-Path (Join-Path $out 'mods') $taskModFolder.Name)
    }
    # A template that somebody once compiled leaves its bin and obj behind; they are not
    # part of the template and they are not small.
    Get-ChildItem -Path (Join-Path $out "mods") -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" } |
        ForEach-Object {
            $checkedTemplateOutput = Assert-BuildChild $_.FullName
            Remove-Item -LiteralPath $checkedTemplateOutput -Recurse -Force -ErrorAction SilentlyContinue
        }
}

# ---------------------------------------------------------------------- no unreleased content
# `dlc/` is deliberately not copied. A content pack is paid content, and the first public
# alpha is free: shipping one inactive means handing every player the pack plus the
# entitlements.json that switches it on, which is a paywall anybody can read their way past.
#
# The loader is untouched and still works - DlcLoader returns quietly when the folder is not
# there - so the day there is something to sell, one line here puts it back.
& (Join-Path $PSScriptRoot "build-launcher.ps1") -OutputDirectory $out

# An opt-in native Luau adapter is exported by Godot, not by the .NET
# Content globs. Its redistribution notices must travel with the binary too.
if (Test-Path -LiteralPath (Join-Path $out 'libluau-script.windows.editor.x86_64.dll')) {
    $adapterNotices = Join-Path $out 'licenses\godot-luau-script'
    New-Item -ItemType Directory -Force -Path $adapterNotices | Out-Null
    foreach ($notice in @('LICENSE.txt', 'COPYRIGHT.txt')) {
        $noticeSource = Join-Path $project "game\extensions\luau\$notice"
        if (-not (Test-Path -LiteralPath $noticeSource)) { throw "Native adapter is missing its redistribution notice: $notice" }
        Copy-Item -LiteralPath $noticeSource -Destination $adapterNotices -Force
    }
}

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
        if (Test-Path $rootGame) { throw 'Unexpected duplicate game folder in fresh export.' }
        $null = Assert-BuildChild $dataGame
        $null = Assert-BuildChild $rootGame
        Move-Item -LiteralPath $dataGame -Destination $rootGame -Force
        Write-Host "  game moved beside the executable" -ForegroundColor DarkGray
    }
    # Nothing copies dlc beside the executable any more, but the csproj could start publishing
    # it again the moment somebody restores that Content line, so the sweep stays and is now
    # unconditional: whatever put a dlc folder in here, it does not ship.
    $dataDlc = Join-Path $dataDirectory.FullName "dlc"
    if (Test-Path $dataDlc) {
        $null = Assert-BuildChild $dataDlc
        Remove-Item -LiteralPath $dataDlc -Recurse -Force
        Write-Host "  dlc removed from the build" -ForegroundColor DarkGray
    }
    $dataMods = Join-Path $dataDirectory.FullName "mods"
    if ((Test-Path $dataMods) -and (Test-Path (Join-Path $out "mods"))) {
        $null = Assert-BuildChild $dataMods
        Remove-Item -LiteralPath $dataMods -Recurse -Force
        Write-Host "  the second copy of mods removed" -ForegroundColor DarkGray
    }
}

Write-Host "== checking the data came with it ==" -ForegroundColor Cyan

# Author permissions and third-party provenance are player-facing redistribution notices.
foreach ($notice in @('LICENSE', 'LICENSE-EXCEPTIONS.md', 'README.md', 'README.ru.md')) {
    Copy-Item -LiteralPath (Join-Path $project $notice) -Destination $out
}
Copy-PlayerTree (Join-Path $project 'docs') (Join-Path $out 'docs') @('LAUNCHER_RU.md')

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
    "LICENSE",
    "LICENSE-EXCEPTIONS.md",
    "docs\THIRD_PARTY_MEDIA.md",
    "README.md",
    "README.ru.md",
    "mods"
)

# And the other half of a check: things that must NOT be in the build. A missing file is loud
# - the game shows an empty world - but a file that should not have shipped is silent, which
# is why it needs asking about explicitly rather than being left to whoever reads the folder.
$forbidden = @(
    @{ Path = "dlc"; Why = "unreleased content pack and its entitlement switch" },
    @{ Path = "LAUNCHER_RU.md"; Why = "internal documentation, and Russian-only at that" },
    @{ Path = ".kilo"; Why = "private nested source checkouts" },
    @{ Path = ".fennara"; Why = "private editor tooling and logs" },
    @{ Path = ".git"; Why = "repository metadata" }
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
    if (-not (Test-Path -LiteralPath (Join-Path $out $item))) { $missing += $item }
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

$leaked = @()
foreach ($item in $forbidden) {
    $found = Get-ChildItem -Path $out -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\$($item.Path)" } | Select-Object -First 1
    if ($found) { $leaked += $item }
}

if ($leaked.Count -gt 0) {
    Write-Host ""
    Write-Host "The build contains things a player should not be given:" -ForegroundColor Red
    $leaked | ForEach-Object { Write-Host "    $($_.Path)  -  $($_.Why)" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Nothing here is fatal to the game, which is the problem: it ships quietly." -ForegroundColor Yellow
    throw "Build carries material that must not ship."
}

$size = [math]::Round((Get-ChildItem -Path $out -Recurse -Force | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "  every data folder the game needs is in the build ($size MB)" -ForegroundColor Green
Write-Host "  nothing in the build that should not be there" -ForegroundColor Green

if (-not $SkipRuntimeVerification) {
    Write-Host '== verifying the actual exported executable ==' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'verify-build.ps1') -Executable $exe -LogDirectory (Join-Path $stage 'verification')
}

# ---------------------------------------------------------------- something to send
if (-not $NoZip) {
    $zip = Join-Path $project "build\Deal or Force $stamp.zip"
    if (Test-Path -LiteralPath $zip) { throw "Archive already exists: $zip" }
    Write-Host "== zipping ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'package-build.ps1') -OutputDirectory $out -Archive $zip
}

Write-Host ""
if ($NoZip) {
    Write-Host "Done. Verified game folder: $out" -ForegroundColor Cyan
} else {
    Write-Host "Done. The archive unpacks to a single folder called 'Deal or Force'." -ForegroundColor Cyan
}
Write-Host 'Run the .exe inside that folder; keep the data folders beside it.' -ForegroundColor Cyan
