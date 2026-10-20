param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$EngineDirectory = 'C:/GodotEngineNet 4.8-Beta1',
    [hashtable]$Addons = @{},
    [string]$KotlinProject,
    [string]$JavaHome = $env:JAVA_HOME,
    [switch]$EditorBootstrap,
    [switch]$Probe
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'kit-common.ps1')
$engine = Get-KitEngine $EngineDirectory
$lock = Read-KitLock $PSScriptRoot
if ($engine.Version -ne $lock.engine.version) { throw 'Workbench must be revalidated for this engine version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
Assert-KitInstallPath $output
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) { throw 'Workbench requires a fresh empty directory; existing files preserved.' }
}
foreach ($id in $Addons.Keys) {
    if ($id -notin @('luau', 'rust', 'nim', 'odin', 'lua', 'sandbox')) { throw "Unsupported workbench addon id: $id" }
    if (-not (Test-Path -LiteralPath $Addons[$id] -PathType Container)) { throw "Missing addon artifact: $id" }
}
if ($KotlinProject -and -not (Test-Path -LiteralPath (Join-Path $KotlinProject 'jvm'))) { throw 'Kotlin project must already have compiled JVM artifacts.' }
if ($KotlinProject -and -not $JavaHome) {
    $java = Get-Command java -ErrorAction Stop
    $JavaHome = Split-Path -Parent (Split-Path -Parent $java.Source)
}
if ($KotlinProject -and -not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin/server/jvm.dll'))) { throw 'Select the JDK used to compile the Kotlin sample.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'adapters/fsharp/template/*') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'workbench/combined_probe.gd') -Destination $output
$languages = [Collections.Generic.List[string]]::new()
$languages.Add('fsharp')
foreach ($id in @('luau', 'rust', 'nim', 'odin', 'lua', 'sandbox')) {
    if (-not $Addons.ContainsKey($id)) { continue }
    $addonDestination = Get-KitPath $output "addons/dof_$id"
    if ($id -eq 'sandbox') { $addonDestination = Get-KitPath $output 'addons/godot_sandbox' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $addonDestination) -Force | Out-Null
    Copy-Item -LiteralPath $Addons[$id] -Destination $addonDestination -Recurse
    $languages.Add($id)
    if ($id -eq 'luau') {
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'adapters/luau/probe') -File -Filter '*.luau' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $output }
    }
    if ($id -eq 'lua') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'adapters/lua/probe/probe.lua') -Destination (Join-Path $output 'lua_probe.lua') }
}
if ($KotlinProject) {
    foreach ($relative in @('jvm', 'src', 'addons/jvm', 'gradle', 'gradlew.bat', 'build.gradle.kts', 'settings.gradle.kts', 'gradle.properties', 'godot_jvm_configuration.json')) {
        $source = Join-Path $KotlinProject $relative
        if (-not (Test-Path -LiteralPath $source)) { throw "Incomplete Kotlin sample: $relative" }
        $target = Get-KitPath $output $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Recurse
    }
    $languages.Add('kotlin')
}
& dotnet build (Join-Path $output 'LanguageKitFSharp.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Combined managed project failed to build.' }
if ($Probe -or $EditorBootstrap) {
    $previousJavaHome = $env:JAVA_HOME
    try {
        if ($KotlinProject) { $env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaHome).Path }
        if ($EditorBootstrap) { Invoke-KitEditorBootstrap $engine.Executable $output }
        if ($Probe) {
            $result = Invoke-KitGodot $engine.Executable (@('--headless', '--path', $output, '--script', 'res://combined_probe.gd', '--') + @($languages))
            if ($result.Output -notmatch 'LANGUAGE_WORKBENCH_PASS') { throw 'Combined probe did not reach its success signal.' }
        }
    } finally {
        $env:JAVA_HOME = $previousJavaHome
    }
}
Write-Host "WORKBENCH $output"
Write-Host "LANGUAGES $($languages -join ',')"
Write-Host 'This proves the requested sample checks only, not exports, the complete editor API or production-game safety.'
