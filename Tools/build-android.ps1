# Deal or Force - Repeatable Android APK / AAB Export & ADB Install Wrapper
#
# Builds an isolated staging copy inside build/ (so development autoloads and desktop-only
# GDExtensions never ship into the APK and never race with an open Godot Editor), pins
# 64-bit .NET SDK + JDK 17 ahead of any incompatible JDK on PATH, exports APK/AAB via
# Godot headless, and optionally installs onto an attached Android device via adb.
#
# Compatible with both Windows PowerShell 5.1 and pwsh 7+.
#
# Examples:
#   powershell -ExecutionPolicy Bypass -File Tools\build-android.ps1 -DryRun
#   powershell -ExecutionPolicy Bypass -File Tools\build-android.ps1 -Godot "C:\GodotEngineNet 4.8-Beta2\Godot_v4.8-beta3_mono_win64.exe" -Install

param(
    [string]$Godot = $env:GODOT_EXECUTABLE,
    [string]$Preset = 'Android (Dev / Debug)',
    [switch]$Release,
    [switch]$Aab,
    [string]$OutputPath,
    [switch]$Install,
    [switch]$Launch,
    [string]$DeviceSerial,
    [switch]$SkipDoctor,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'EngineVersion.ps1')

$expectedPrefix = Get-ExpectedEngineVersionPrefix (Join-Path $projectRoot 'DealOrForce.csproj')
$templateFolderName = $expectedPrefix + 'mono'
$templateDir = Join-Path (Join-Path $env:APPDATA 'Godot\export_templates') $templateFolderName

# ---------------------------------------------------------------------------- 1. Environment pinning
$dotnet64Dir = 'C:\Program Files\dotnet'
if (Test-Path -LiteralPath (Join-Path $dotnet64Dir 'dotnet.exe')) {
    $env:PATH = "$dotnet64Dir;$env:PATH"
}

# Pin JDK 17 ahead of PATH (prevents Gradle failure if JDK 25+ is first on PATH).
$jdk17Home = $null
$jdkCandidates = @()
if ($env:JAVA_HOME) { $jdkCandidates += $env:JAVA_HOME }
foreach ($base in @('C:\Program Files\Microsoft', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Java')) {
    if (Test-Path -LiteralPath $base) {
        Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'jdk-?17' } |
            ForEach-Object { $jdkCandidates += $_.FullName }
    }
}
foreach ($cand in ($jdkCandidates | Select-Object -Unique)) {
    $javaExe = Join-Path $cand 'bin\java.exe'
    if (Test-Path -LiteralPath $javaExe) {
        $prevErr = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $verOut = (& $javaExe -version 2>&1) -join "`n"
        } finally {
            $ErrorActionPreference = $prevErr
        }
        if ($verOut -match 'version\s+"17\.') {
            $jdk17Home = $cand
            break
        }
    }
}
if ($jdk17Home) {
    $env:JAVA_HOME = $jdk17Home
    $env:PATH = "$(Join-Path $jdk17Home 'bin');$env:PATH"
}

# Discover Android SDK + adb
$androidSdk = $null
foreach ($cand in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'), 'C:\Android\Sdk')) {
    if ($cand -and (Test-Path -LiteralPath $cand)) {
        $androidSdk = $cand
        break
    }
}
if ($androidSdk) {
    $env:ANDROID_HOME = $androidSdk
    $env:ANDROID_SDK_ROOT = $androidSdk
    $platformTools = Join-Path $androidSdk 'platform-tools'
    if (Test-Path -LiteralPath $platformTools) {
        $env:PATH = "$platformTools;$env:PATH"
    }
}

# ---------------------------------------------------------------------------- 2. Fast fail-clear preflight
$ext = if ($Aab) { '.aab' } else { '.apk' }
$flavor = if ($Release) { 'release' } else { 'dev' }
$defaultOut = Join-Path $projectRoot ("build\android\DealOrForce-$flavor$ext")
$artifactPath = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { $defaultOut }
$allowedBuildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $artifactPath.StartsWith($allowedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Android output path must remain inside the project build/ folder: $artifactPath"
}

Write-Host '=== Deal or Force: Android Build ===' -ForegroundColor Cyan
Write-Host "  Preset  : $Preset ($(if ($Release) { 'release' } else { 'debug' }))" -ForegroundColor DarkGray
Write-Host "  Output  : $artifactPath" -ForegroundColor DarkGray
Write-Host "  JDK 17  : $(if ($jdk17Home) { $jdk17Home } else { 'MISSING' })" -ForegroundColor DarkGray
Write-Host "  SDK     : $(if ($androidSdk) { $androidSdk } else { 'MISSING' })" -ForegroundColor DarkGray
Write-Host "  Template: $templateDir" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host ''
    Write-Host '[DryRun] Running read-only android-doctor.ps1 preflight...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'android-doctor.ps1') -Godot $Godot
    Write-Host '[DryRun] Preflight complete; no export invoked.' -ForegroundColor Green
    exit 0
}

if (-not $SkipDoctor) {
    & (Join-Path $PSScriptRoot 'android-doctor.ps1') -Godot $Godot -FailOnMissingTooling
    if ($LASTEXITCODE -ne 0) {
        throw 'Android readiness preflight failed. Fix the [FAIL] items above (or pass -SkipDoctor to override).'
    }
}

if (-not $Godot -or -not (Test-Path -LiteralPath $Godot)) {
    throw 'Pass -Godot <matching Godot 4.8 .NET executable> or set GODOT_EXECUTABLE.'
}
if ([IO.Path]::GetFileName($Godot).EndsWith('_console.exe', [StringComparison]::OrdinalIgnoreCase)) {
    $nativeGodot = $Godot.Substring(0, $Godot.Length - '_console.exe'.Length) + '.exe'
    if (Test-Path -LiteralPath $nativeGodot -PathType Leaf) { $Godot = $nativeGodot }
}
$engineVersion = (& $Godot --version | Select-Object -Last 1).ToString().Trim()
if (-not $engineVersion.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
    throw "Engine $engineVersion does not match expected prefix $expectedPrefix* (template $templateFolderName)."
}

$solution = Join-Path $projectRoot 'DealOrForce.sln'
if (-not (Test-Path -LiteralPath $solution)) {
    throw 'Missing DealOrForce.sln (Godot .NET export requires a solution matching dotnet/project/assembly_name).'
}

$presetsFile = Join-Path $projectRoot 'export_presets.cfg'
$presetsContent = Get-Content -LiteralPath $presetsFile -Raw
if ($presetsContent -notmatch [regex]::Escape("name=""$Preset""")) {
    throw "Export preset '$Preset' not found in export_presets.cfg."
}

# ---------------------------------------------------------------------------- 3. Isolated staging export
$buildRoot = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$gdignore = Join-Path $buildRoot '.gdignore'
if (-not (Test-Path -LiteralPath $gdignore)) { New-Item -ItemType File -Path $gdignore | Out-Null }

$artifactDir = Split-Path -Parent $artifactPath
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

function Copy-AndroidStageTree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -in @('bin', 'obj', 'build', 'tmp', 'tmp_logs', 'tmp_playtest', 'bridge.local.json') -or
            ($item.Name.StartsWith('.') -and $item.Name -ne '.gdignore') -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) { Copy-AndroidStageTree $item.FullName $target }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target }
    }
}

$stage = Join-Path $buildRoot ('.export-android-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null

try {
    Write-Host '== staging clean Android export tree (no desktop addons/GDExtensions) ==' -ForegroundColor Cyan
    foreach ($folder in @('Scripts', 'Scenes', 'game')) {
        Copy-AndroidStageTree (Join-Path $projectRoot $folder) (Join-Path $stage $folder)
    }
    # Strip Windows-only Luau GDExtension from Android stage.
    $stagedLuau = Join-Path $stage 'game\extensions\luau'
    if (Test-Path -LiteralPath $stagedLuau) {
        Remove-Item -LiteralPath $stagedLuau -Recurse -Force
    }
    Copy-AndroidStageTree (Join-Path $projectRoot 'Tools\ExportVerification') (Join-Path $stage 'Tools\ExportVerification')
    foreach ($file in @('DealOrForce.csproj', 'DealOrForce.sln', 'GameVersion.props', 'export_presets.cfg', 'icon.ico', 'icon_small.png')) {
        $src = Join-Path $projectRoot $file
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $stage -Force }
    }

    $stagedPrepare = Join-Path $stage 'Tools\prepare_export_project.gd'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stagedPrepare) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Tools\prepare_export_project.gd') -Destination $stagedPrepare
    [IO.File]::WriteAllText((Join-Path $stage 'project.godot'), "config_version=5`n", [Text.UTF8Encoding]::new($false))

    $mode = if ($Release) { '--export-release' } else { '--export-debug' }
    $prevPref = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Godot --headless --path $stage --script res://Tools/prepare_export_project.gd -- (Join-Path $projectRoot 'project.godot') (Join-Path $stage 'project.godot') 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Failed to generate stripped project.godot for Android stage.' }
        Remove-Item -LiteralPath $stagedPrepare -Force

        & $Godot --headless --editor --path $stage --import 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Android staged project import failed.' }

        Write-Host "== exporting Android package ($mode) ==" -ForegroundColor Cyan
        & $Godot --headless --path $stage $mode $Preset $artifactPath 2>&1 | Out-Host
        $exportExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevPref
    }

    if ($exportExit -ne 0) { throw "Godot Android export exited with code $exportExit." }
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Godot finished without writing expected Android artifact: $artifactPath"
    }
    $sizeMb = [math]::Round((Get-Item -LiteralPath $artifactPath).Length / 1MB, 1)
    Write-Host "  Android artifact ready: $artifactPath ($sizeMb MB)" -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $stage) {
        Write-Host "  staged export folder retained at $stage" -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------- 4. Optional ADB install & launch
if ($Install) {
    if ($Aab) { throw '-Install expects an .apk; use bundletool for .aab installs.' }
    $adb = Get-Command adb -ErrorAction SilentlyContinue
    if (-not $adb) { throw 'adb not found on PATH; cannot run -Install.' }

    $adbArgs = @()
    if ($DeviceSerial) { $adbArgs += @('-s', $DeviceSerial) }
    $adbArgs += @('install', '-r', $artifactPath)

    Write-Host "== installing via adb ($($adb.Source)) ==" -ForegroundColor Cyan
    & $adb.Source @adbArgs
    if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE." }

    if ($Launch) {
        $launchArgs = @()
        if ($DeviceSerial) { $launchArgs += @('-s', $DeviceSerial) }
        $launchArgs += @('shell', 'monkey', '-p', 'com.bayterealization.dealorforce', '-c', 'android.intent.category.LAUNCHER', '1')
        & $adb.Source @launchArgs
    }
}

Write-Host 'Done.' -ForegroundColor Green
exit 0
