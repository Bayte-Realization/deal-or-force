param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    # verify-beta3.ps1 interface: the addon is <OutputDirectory>/addon (from build.ps1), and every
    # run gets a fresh project under <OutputDirectory>/runs/<stamp>.
    [string]$OutputDirectory,
    # Older explicit interface; still accepted.
    [string]$AddonDirectory,
    [string]$ProjectDirectory,
    # Replace the bundled copy of the game's engine-mod template with the real one
    # (for example <game>/mods/_templates/luau_engine/corecode). Used only by dual-suffix builds.
    [string]$EngineModDirectory,
    # Also export the probe with the official template_release and run the exported build.
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
if ($OutputDirectory) {
    $root = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not $AddonDirectory) { $AddonDirectory = Join-Path $root 'addon' }
    if (-not $ProjectDirectory) { $ProjectDirectory = Join-Path $root ('runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
}
if (-not $AddonDirectory -or -not $ProjectDirectory) { throw 'Give -OutputDirectory, or both -AddonDirectory and -ProjectDirectory.' }
if (-not (Test-Path -LiteralPath (Join-Path $AddonDirectory 'luau.gdextension'))) {
    throw "No built addon at $AddonDirectory. Run build.ps1 -OutputDirectory $AddonDirectory first."
}
Assert-KitInstallPath $ProjectDirectory
if (Test-Path -LiteralPath (Join-Path $ProjectDirectory 'project.godot')) {
    throw 'Choose a fresh isolated project directory; existing project preserved.'
}
New-Item -ItemType Directory -Path $ProjectDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'probe/*') -Destination $ProjectDirectory -Recurse
if ($EngineModDirectory) {
    $engineMod = Join-Path $ProjectDirectory 'engine_mod/corecode'
    Remove-Item -LiteralPath $engineMod -Recurse -Force
    New-Item -ItemType Directory -Path $engineMod -Force | Out-Null
    Get-ChildItem -LiteralPath $EngineModDirectory -Recurse -File |
        Where-Object { $_.Extension -in @('.lua', '.luau') } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($EngineModDirectory, $_.FullName)
            $target = Join-Path $engineMod $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
}
$addons = Join-Path $ProjectDirectory 'addons'
New-Item -ItemType Directory -Path $addons -Force | Out-Null
Copy-Item -LiteralPath $AddonDirectory -Destination (Join-Path $addons 'dof_luau') -Recurse

# The probe checks exactly the suffixes the build claims to own.
$suffixes = @('luau')
$infoPath = Join-Path $AddonDirectory 'build-info.json'
if (Test-Path -LiteralPath $infoPath) {
    $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
    $suffixes = @($info.suffixes)
    Write-Host "LUAU_ADDON engine=$($info.engine) suffixes=$($suffixes -join ',') api_sha256=$($info.extension_api_sha256)"
}
[IO.File]::WriteAllText((Join-Path $ProjectDirectory 'probe_expect.json'), (@{ suffixes = $suffixes } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

function Assert-CleanRun([string]$Text, [string]$Build) {
    if ($Text -match '(?i)leaked|still in use|CrashHandler|access violation|segmentation fault|No loader found') {
        throw "Luau $Build run reported a leak, crash or missing loader."
    }
    if ($Text -notmatch "LANGUAGE_KIT_PASS language=luau .*build=$Build") { throw "Luau $Build probe did not reach its success signal." }
    if ($suffixes -contains 'lua' -and $Text -notmatch 'LUAU_ENGINE_MOD_PASS') { throw "Luau $Build probe skipped the engine-mod check." }
}

$result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $ProjectDirectory, '--script', 'res://probe.gd', '--quit-after', '240')
Assert-CleanRun $result.Output 'debug'
Write-Host "LUAU_DEBUG_VERIFIED exit=0 errors=0 suffixes=$($suffixes -join ',')"

if ($Release) {
    $presets = @'
[preset.0]

name="Windows Desktop"
platform="Windows Desktop"
runnable=true
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter="*.lua,*.luau,*.json"
exclude_filter=""
export_path=""

[preset.0.options]

binary_format/embed_pck=false
binary_format/architecture="x86_64"
debug/export_console_wrapper=2
codesign/enable=false
application/modify_resources=false
'@
    [IO.File]::WriteAllText((Join-Path $ProjectDirectory 'export_presets.cfg'), $presets, [Text.UTF8Encoding]::new($false))
    Invoke-KitEditorBootstrap $GodotExecutable $ProjectDirectory
    $exportDirectory = Join-Path $ProjectDirectory 'export'
    New-Item -ItemType Directory -Path $exportDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $exportDirectory '.gdignore'), '')
    $exe = Join-Path $exportDirectory 'luau_probe.exe'
    $null = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $ProjectDirectory, '--export-release', 'Windows Desktop', $exe) -TimeoutSeconds 300
    $dll = Join-Path $exportDirectory 'libluau-script.windows.template_release.x86_64.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'Export did not carry the template_release Luau library.' }
    $console = Join-Path $exportDirectory 'luau_probe.console.exe'
    $run = Invoke-KitGodot $console @('--headless')
    Assert-CleanRun $run.Output 'release'
    Write-Host "LUAU_RELEASE_VERIFIED exit=0 errors=0 library=$((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
}
Write-Host "LUAU_PROBE PASS project=$ProjectDirectory"
