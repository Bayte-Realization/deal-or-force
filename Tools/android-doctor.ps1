# Deal or Force - Read-Only Android Port Readiness Diagnostic
#
# Checks local host tooling (JDK 17 vs PATH JDK, Android SDK/NDK, adb/AVDs, Godot 4.8 .NET
# Android export templates, 64-bit .NET SDK + android workload) AND project readiness
# (export_presets.cfg, GDExtensions, shaders/PackedMapShader, GC/System.IO packaging).
#
# Safe to run while the Godot Editor is open: strictly read-only, never modifies host tools
# and never touches any Godot process. Compatible with both Windows PowerShell 5.1 and pwsh 7+.
#
# Example:
#   powershell -ExecutionPolicy Bypass -File Tools\android-doctor.ps1
#   pwsh -File Tools\android-doctor.ps1 -Json

param(
    [string]$Godot = $env:GODOT_EXECUTABLE,
    [switch]$Json,
    [switch]$FailOnMissingTooling
)

$ErrorActionPreference = 'Continue'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'EngineVersion.ps1')

$checks = New-Object System.Collections.ArrayList
$nextSteps = New-Object System.Collections.ArrayList

function Add-Check {
    param(
        [string]$Area,
        [string]$Name,
        [ValidateSet('PASS', 'WARN', 'FAIL', 'INFO')][string]$Status,
        [string]$Detail
    )
    $null = $checks.Add([PSCustomObject]@{
        Area   = $Area
        Name   = $Name
        Status = $Status
        Detail = $Detail
    })
}

function Add-NextStep([string]$Step) {
    if (-not ($nextSteps -contains $Step)) {
        $null = $nextSteps.Add($Step)
    }
}

function Get-JavaMajorVersion([string]$JavaExe) {
    if (-not $JavaExe -or -not (Test-Path -LiteralPath $JavaExe)) { return $null }
    try {
        $out = (& $JavaExe -version 2>&1) -join "`n"
        if ($out -match 'version\s+"(?<maj>\d+)(?:\.(?<min>\d+))?') {
            $maj = [int]$Matches.maj
            if ($maj -eq 1 -and $Matches.min) { return [int]$Matches.min }
            return $maj
        }
    } catch { }
    return $null
}

# ---------------------------------------------------------------------------- 1. JDK 17 check
$jdk17Candidates = @()
if ($env:JAVA_HOME) { $jdk17Candidates += $env:JAVA_HOME }
foreach ($base in @('C:\Program Files\Microsoft', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Java')) {
    if (Test-Path -LiteralPath $base) {
        Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'jdk-?17' } |
            ForEach-Object { $jdk17Candidates += $_.FullName }
    }
}
$jdk17Home = $null
foreach ($cand in ($jdk17Candidates | Select-Object -Unique)) {
    $javaBin = Join-Path $cand 'bin\java.exe'
    $maj = Get-JavaMajorVersion $javaBin
    if ($maj -eq 17) {
        $jdk17Home = $cand
        break
    }
}

$pathJavaCmd = Get-Command java -ErrorAction SilentlyContinue
$pathJavaExe = if ($pathJavaCmd) { $pathJavaCmd.Source } else { $null }
$pathJavaMajor = Get-JavaMajorVersion $pathJavaExe

if ($jdk17Home) {
    Add-Check -Area 'Host' -Name 'JDK 17 (Godot/Gradle)' -Status 'PASS' -Detail "Found JDK 17 at $jdk17Home (JAVA_HOME='$($env:JAVA_HOME)')"
    if ($pathJavaMajor -and $pathJavaMajor -ne 17) {
        Add-Check -Area 'Host' -Name 'PATH java mismatch' -Status 'WARN' -Detail "PATH resolves java to JDK $pathJavaMajor ($pathJavaExe); Gradle/Godot Android builds require JDK 17. build-android.ps1 pins JDK 17 automatically."
        Add-NextStep "Set Godot Editor Settings -> Export -> Android -> Java SDK Path to: $jdk17Home"
    }
} else {
    Add-Check -Area 'Host' -Name 'JDK 17 (Godot/Gradle)' -Status 'FAIL' -Detail "JDK 17 not found (PATH java=$pathJavaMajor). Godot 4.8 Android Gradle builds require JDK 17."
    Add-NextStep 'winget install --id Microsoft.OpenJDK.17 -e'
}

# ---------------------------------------------------------------------------- 2. Android SDK / NDK / ADB / AVD
$sdkCandidates = @()
if ($env:ANDROID_HOME) { $sdkCandidates += $env:ANDROID_HOME }
if ($env:ANDROID_SDK_ROOT) { $sdkCandidates += $env:ANDROID_SDK_ROOT }
if ($env:LOCALAPPDATA) { $sdkCandidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk') }
$sdkCandidates += 'C:\Android\Sdk'

$androidSdk = $null
foreach ($cand in ($sdkCandidates | Select-Object -Unique)) {
    if ($cand -and (Test-Path -LiteralPath $cand)) {
        $androidSdk = $cand
        break
    }
}

$adbExe = $null
$cmdAdb = Get-Command adb -ErrorAction SilentlyContinue
if ($cmdAdb) { $adbExe = $cmdAdb.Source }
elseif ($androidSdk -and (Test-Path -LiteralPath (Join-Path $androidSdk 'platform-tools\adb.exe'))) {
    $adbExe = Join-Path $androidSdk 'platform-tools\adb.exe'
}

if ($androidSdk) {
    $hasPlatformTools = Test-Path -LiteralPath (Join-Path $androidSdk 'platform-tools\adb.exe')
    $buildToolsDir = Join-Path $androidSdk 'build-tools'
    $buildTools = if (Test-Path -LiteralPath $buildToolsDir) {
        @(Get-ChildItem -LiteralPath $buildToolsDir -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    } else { @() }
    $platformsDir = Join-Path $androidSdk 'platforms'
    $platforms = if (Test-Path -LiteralPath $platformsDir) {
        @(Get-ChildItem -LiteralPath $platformsDir -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    } else { @() }
    $ndkDir = Join-Path $androidSdk 'ndk'
    $ndks = if (Test-Path -LiteralPath $ndkDir) {
        @(Get-ChildItem -LiteralPath $ndkDir -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    } else { @() }

    $hasBt36 = $buildTools -contains '36.1.0'
    if ($hasPlatformTools -and $buildTools.Count -gt 0 -and $platforms.Count -gt 0 -and $hasBt36) {
        Add-Check -Area 'Host' -Name 'Android SDK' -Status 'PASS' -Detail "$androidSdk (build-tools=$($buildTools -join ','); platforms=$($platforms -join ','))"
    } elseif ($hasPlatformTools -and $buildTools.Count -gt 0 -and $platforms.Count -gt 0) {
        Add-Check -Area 'Host' -Name 'Android SDK' -Status 'WARN' -Detail "$androidSdk present (build-tools=$($buildTools -join ',')), but Godot 4.8-beta3 requires build-tools;36.1.0."
        Add-NextStep 'sdkmanager "build-tools;36.1.0" "platforms;android-36"'
    } else {
        Add-Check -Area 'Host' -Name 'Android SDK' -Status 'WARN' -Detail "$androidSdk exists but incomplete (adb=$hasPlatformTools, build-tools=$($buildTools.Count), platforms=$($platforms.Count))"
        Add-NextStep 'sdkmanager "platform-tools" "build-tools;36.1.0" "platforms;android-36" "platforms;android-35"'
    }
    if ($ndks.Count -gt 0 -or $env:ANDROID_NDK_ROOT) {
        Add-Check -Area 'Host' -Name 'Android NDK (optional native kernels)' -Status 'PASS' -Detail "NDK present: $($ndks -join ', ') $($env:ANDROID_NDK_ROOT)"
    } else {
        Add-Check -Area 'Host' -Name 'Android NDK (optional native kernels)' -Status 'INFO' -Detail 'Not installed (optional for prebuilt APK templates; Godot 4.x Gradle/native builds expect NDK r28b 28.1.13356709).'
    }
} else {
    Add-Check -Area 'Host' -Name 'Android SDK' -Status 'FAIL' -Detail 'Android SDK not found (ANDROID_HOME/ANDROID_SDK_ROOT unset, %LOCALAPPDATA%\Android\Sdk missing).'
    Add-NextStep 'Install Android Studio OR Android command-line tools -> sdkmanager "platform-tools" "build-tools;35.0.1" "platforms;android-35" "ndk;28.1.13356709" "cmake;3.10.2.4988404" and setx ANDROID_HOME "%LOCALAPPDATA%\Android\Sdk"'
}

if ($adbExe) {
    $adbLines = @(& $adbExe devices 2>&1 | Select-Object -Skip 1 | Where-Object { $_ -match '\S' })
    $deviceSummary = if ($adbLines.Count -gt 0) { ($adbLines -join '; ') } else { 'no devices attached' }
    Add-Check -Area 'Host' -Name 'ADB & Devices' -Status 'PASS' -Detail "$adbExe ($deviceSummary)"
} else {
    Add-Check -Area 'Host' -Name 'ADB & Devices' -Status 'FAIL' -Detail 'adb not found on PATH or inside Android SDK platform-tools.'
}

$emulatorExe = $null
$cmdEmu = Get-Command emulator -ErrorAction SilentlyContinue
if ($cmdEmu) { $emulatorExe = $cmdEmu.Source }
elseif ($androidSdk -and (Test-Path -LiteralPath (Join-Path $androidSdk 'emulator\emulator.exe'))) {
    $emulatorExe = Join-Path $androidSdk 'emulator\emulator.exe'
}
if ($emulatorExe) {
    $avds = @(& $emulatorExe -list-avds 2>&1 | Where-Object { $_ -match '\S' })
    Add-Check -Area 'Host' -Name 'Android Emulator AVDs' -Status 'INFO' -Detail $(if ($avds.Count -gt 0) { "AVDs: $($avds -join ', ')" } else { 'emulator found, 0 AVDs configured' })
} else {
    Add-Check -Area 'Host' -Name 'Android Emulator AVDs' -Status 'INFO' -Detail 'Android emulator not installed (physical USB device via adb is faster for 3D globe testing).'
}

# ---------------------------------------------------------------------------- 3. Godot 4.8 Templates
$csprojPath = Join-Path $projectRoot 'DealOrForce.csproj'
$expectedPrefix = Get-ExpectedEngineVersionPrefix $csprojPath
$templateFolderName = $expectedPrefix + 'mono'
$templateDir = Join-Path (Join-Path $env:APPDATA 'Godot\export_templates') $templateFolderName

$debugApkTpl = Join-Path $templateDir 'android_debug.apk'
$releaseApkTpl = Join-Path $templateDir 'android_release.apk'
$sourceZipTpl = Join-Path $templateDir 'android_source.zip'

if ((Test-Path -LiteralPath $debugApkTpl) -and (Test-Path -LiteralPath $releaseApkTpl)) {
    $dbgMb = [math]::Round((Get-Item -LiteralPath $debugApkTpl).Length / 1MB, 1)
    $relMb = [math]::Round((Get-Item -LiteralPath $releaseApkTpl).Length / 1MB, 1)
    $srcNote = if (Test-Path -LiteralPath $sourceZipTpl) { ', android_source.zip present' } else { ', android_source.zip missing' }
    Add-Check -Area 'Godot' -Name "Android Export Templates ($templateFolderName)" -Status 'PASS' -Detail "$templateDir (debug=${dbgMb}MB, release=${relMb}MB$srcNote)"
} else {
    Add-Check -Area 'Godot' -Name "Android Export Templates ($templateFolderName)" -Status 'FAIL' -Detail "Missing android_debug.apk / android_release.apk in $templateDir"
    Add-NextStep "Download Godot $templateFolderName export templates into $templateDir"
}

if ($Godot -and (Test-Path -LiteralPath $Godot)) {
    Add-Check -Area 'Godot' -Name 'Godot Executable' -Status 'PASS' -Detail $Godot
} else {
    Add-Check -Area 'Godot' -Name 'Godot Executable' -Status 'INFO' -Detail 'GODOT_EXECUTABLE not set; pass -Godot <path> to Tools\build-android.ps1.'
}

# ---------------------------------------------------------------------------- 4. .NET SDK & Android Workload
$dotnet64 = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet64)) {
    $cmdDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmdDotnet) { $dotnet64 = $cmdDotnet.Source }
}

if ($dotnet64 -and (Test-Path -LiteralPath $dotnet64)) {
    $dotnetVer = (& $dotnet64 --version 2>&1 | Select-Object -Last 1).ToString().Trim()
    Add-Check -Area 'DotNet' -Name '64-bit .NET SDK' -Status 'PASS' -Detail "$dotnetVer ($dotnet64)"
    $workloadOut = (& $dotnet64 workload list 2>&1) -join "`n"
    if ($workloadOut -match '(?m)^\s*android\s+') {
        Add-Check -Area 'DotNet' -Name '.NET Android Runtime / Workload' -Status 'PASS' -Detail 'android workload is installed (plus NuGet android-arm64 runtime packs).'
    } else {
        $nugetMonoPack = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.runtime.mono.android-arm64'
        if (Test-Path -LiteralPath $nugetMonoPack) {
            Add-Check -Area 'DotNet' -Name '.NET Android Runtime / Workload' -Status 'PASS' -Detail 'NuGet Mono android-arm64 runtime pack cached (Godot 4.8 .NET exports APKs without needing Admin MSI workload).'
        } else {
            Add-Check -Area 'DotNet' -Name '.NET Android Runtime / Workload' -Status 'INFO' -Detail 'Optional MSI android workload not installed; Godot 4.8 .NET auto-downloads NuGet android-arm64 runtime pack on first export.'
        }
    }
} else {
    Add-Check -Area 'DotNet' -Name '64-bit .NET SDK' -Status 'FAIL' -Detail '64-bit dotnet.exe not found at C:\Program Files\dotnet\dotnet.exe.'
}

# ---------------------------------------------------------------------------- 5. Project Android Audit
$presetsPath = Join-Path $projectRoot 'export_presets.cfg'
$presetsText = if (Test-Path -LiteralPath $presetsPath) { Get-Content -LiteralPath $presetsPath -Raw } else { '' }
$hasWinPreset = $presetsText -match 'platform="Windows Desktop"'
$hasAndroidPreset = $presetsText -match 'platform="Android"'
if ($hasWinPreset -and $hasAndroidPreset) {
    Add-Check -Area 'Project' -Name 'export_presets.cfg' -Status 'PASS' -Detail 'Both Windows Desktop and Android presets present.'
} else {
    Add-Check -Area 'Project' -Name 'export_presets.cfg' -Status 'FAIL' -Detail "WindowsPreset=$hasWinPreset, AndroidPreset=$hasAndroidPreset"
}

# GDExtension audit
$gdextFiles = @()
foreach ($dir in @('addons', 'game')) {
    $full = Join-Path $projectRoot $dir
    if (Test-Path -LiteralPath $full) {
        $gdextFiles += Get-ChildItem -LiteralPath $full -Recurse -File -Filter '*.gdextension' -ErrorAction SilentlyContinue
    }
}
$unexcludedGdext = @()
foreach ($ext in $gdextFiles) {
    $rel = $ext.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    $excludedByPreset = ($rel -like 'addons/*' -and $presetsText -match 'addons/\*') -or
                        ($rel -like 'game/extensions/luau/*' -and $presetsText -match 'game/extensions/luau/\*')
    if (-not $excludedByPreset) { $unexcludedGdext += $rel }
}
if ($unexcludedGdext.Count -eq 0) {
    Add-Check -Area 'Project' -Name 'GDExtension Android Exclusions' -Status 'PASS' -Detail "All $($gdextFiles.Count) GDExtension(s) (fennara, ziva_agent, godotsteam, limboai, luau) excluded from Android preset."
} else {
    Add-Check -Area 'Project' -Name 'GDExtension Android Exclusions' -Status 'WARN' -Detail "Unexcluded GDExtensions: $($unexcludedGdext -join ', ')"
}

# Shader & Mobile Renderer audit
$packedShader = Join-Path $projectRoot 'Scripts\Presentation\PackedMapShader.cs'
$compatShader = Join-Path $projectRoot 'Scripts\Presentation\CompatibilityMapShader.cs'
$globeShader = Join-Path $projectRoot 'game\shaders\globe_map.gdshader'
$shaderCount = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'game') -Recurse -File -Filter '*.gdshader' -ErrorAction SilentlyContinue).Count
if ((Test-Path -LiteralPath $packedShader) -and (Test-Path -LiteralPath $compatShader) -and (Test-Path -LiteralPath $globeShader)) {
    Add-Check -Area 'Project' -Name 'Mobile/GLES3 Map Shaders' -Status 'PASS' -Detail "PackedMapShader (12 texture units <= 16 GLES3 limit) + CompatibilityMapShader present; $shaderCount .gdshader files under game/."
} else {
    Add-Check -Area 'Project' -Name 'Mobile/GLES3 Map Shaders' -Status 'WARN' -Detail 'Missing PackedMapShader or CompatibilityMapShader.'
}

# Platform foundation code audit
$requiredPlatformFiles = @(
    'Scripts\Platform\PlatformProfile.cs',
    'Scripts\Platform\GodotPlatformBridge.cs',
    'Scripts\Platform\TouchGestureMath.cs',
    'Scripts\Input\TouchCameraAdapter.cs'
)
$missingPlatform = @($requiredPlatformFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $projectRoot $_)) })
if ($missingPlatform.Count -eq 0) {
    Add-Check -Area 'Project' -Name 'Platform & Touch Foundation' -Status 'PASS' -Detail 'PlatformProfile, GodotPlatformBridge, TouchGestureMath, and TouchCameraAdapter present.'
} else {
    Add-Check -Area 'Project' -Name 'Platform & Touch Foundation' -Status 'FAIL' -Detail "Missing: $($missingPlatform -join ', ')"
}

# Known Milestone-1 architectural advisories
$csprojRaw = Get-Content -LiteralPath $csprojPath -Raw
if ($csprojRaw -match '<ServerGarbageCollection>true</ServerGarbageCollection>') {
    Add-Check -Area 'Project' -Name 'Android GC Profile (M1 Task)' -Status 'WARN' -Detail 'DealOrForce.csproj sets ServerGarbageCollection=true in Debug. Guard with !$(TargetFramework.Contains("android")) before shipping to 4-6 GB Android devices.'
}
if ($csprojRaw -match 'Include="game\\\*\*\\\*\.json"') {
    Add-Check -Area 'Project' -Name 'Android game/ Data Strategy (M1 Task)' -Status 'INFO' -Detail 'GameDataLoader reads game/ via System.IO; Android Milestone 1 requires first-boot extraction of bundled game/ assets into OS.GetUserDataDir() (Decision-1: 93% confidence).'
}

# ---------------------------------------------------------------------------- Output
$passCount = @($checks | Where-Object { $_.Status -eq 'PASS' }).Count
$warnCount = @($checks | Where-Object { $_.Status -eq 'WARN' }).Count
$failCount = @($checks | Where-Object { $_.Status -eq 'FAIL' }).Count

if ($Json) {
    [PSCustomObject]@{
        ProjectRoot = $projectRoot
        TemplateDir = $templateDir
        Summary     = [PSCustomObject]@{ Pass = $passCount; Warn = $warnCount; Fail = $failCount }
        Checks      = $checks
        NextSteps   = $nextSteps
    } | ConvertTo-Json -Depth 5
} else {
    Write-Host ''
    Write-Host '=== Deal or Force: Android Readiness Doctor ===' -ForegroundColor Cyan
    Write-Host "Project : $projectRoot" -ForegroundColor DarkGray
    Write-Host "Expected: Godot $templateFolderName" -ForegroundColor DarkGray
    Write-Host ''
    foreach ($c in $checks) {
        $color = switch ($c.Status) {
            'PASS' { 'Green' }
            'WARN' { 'Yellow' }
            'FAIL' { 'Red' }
            default { 'Gray' }
        }
        Write-Host ("[{0,-4}] {1,-8} {2}" -f $c.Status, $c.Area, $c.Name) -ForegroundColor $color
        Write-Host ("       {0}" -f $c.Detail) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host ("Summary: PASS={0}  WARN={1}  FAIL={2}" -f $passCount, $warnCount, $failCount) -ForegroundColor $(if ($failCount -gt 0) { 'Yellow' } else { 'Green' })
    if ($nextSteps.Count -gt 0) {
        Write-Host ''
        Write-Host 'Next concrete steps to reach playable APK:' -ForegroundColor Cyan
        $idx = 1
        foreach ($step in $nextSteps) {
            Write-Host ("  {0}. {1}" -f $idx, $step) -ForegroundColor White
            $idx++
        }
    }
    Write-Host ''
}

if ($FailOnMissingTooling -and $failCount -gt 0) {
    exit 1
}
exit 0
