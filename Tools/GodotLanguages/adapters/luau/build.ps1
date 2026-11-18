param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    # 'luau': .luau/.mod.luau/init.luau only, so the gilzoide Lua adapter can own .lua (kit workbench).
    # 'lua-and-luau': both suffixes, .luau preferred by require; for the game, which ships no other Lua.
    [ValidateSet('luau', 'lua-and-luau')][string]$Suffixes = 'luau',
    [ValidateSet('editor', 'template_release')][string[]]$Targets = @('editor', 'template_release'),
    [string]$PythonExecutable = 'python',
    [ValidateRange(1, 16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$engine = (Resolve-Path -LiteralPath $GodotExecutable).Path
$version = (& $engine --headless --version | Out-String).Trim()
if ($LASTEXITCODE -or $version -notmatch '^4\.8\.beta[123]\.mono\.') {
    throw "This adapter is verified on Godot 4.8 beta1/beta2/beta3 .NET only, found: $version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $output 'luau.gdextension')) {
    throw 'Existing addon preserved. Build to a new output directory, do not replace a loaded DLL.'
}
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$commit = (& git -C $source rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -or $commit -ne '7942ce6def8023596ca0cb8d819b032eb40a4bce') {
    throw 'Unexpected Luau upstream revision; source preserved.'
}
$unexpected = @(& git -C $source diff --name-only HEAD) | Where-Object {
    $_ -notin @('src/core/godot_builtin_bindings.cpp', 'src/scripting/resource_format_luau_script.cpp',
        'src/scripting/luau_script.cpp', 'src/scripting/luau_script.h', 'src/scripting/luau_cache.cpp',
        'src/scripting/luau_lib.cpp', 'src/analysis/analysis_utils.cpp', 'src/analysis/annotation_class_def.cpp',
        'src/core/extension_api.cpp', 'bindgen/luau_api_generator.py', 'extern/godot-cpp')
}
if ($unexpected.Count) { throw "Unexpected tracked edits preserved: $($unexpected -join ', ')" }

function Use-Patch([string]$Directory, [string]$Name, [string[]]$Conflicts = @()) {
    $patch = Join-Path $PSScriptRoot "patches/$Name"
    & git -C $Directory apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -eq 0) { return }
    foreach ($other in $Conflicts) {
        & git -C $Directory apply --reverse --check (Join-Path $PSScriptRoot "patches/$other") 2>$null
        if ($LASTEXITCODE -eq 0) { throw "Source already carries $other; use a separate checkout for -Suffixes $Suffixes." }
    }
    & git -C $Directory apply --check $patch
    if ($LASTEXITCODE) { throw "Source does not match the recorded patch: $Name" }
    & git -C $Directory apply $patch
    if ($LASTEXITCODE) { throw "Applying patch failed: $Name" }
}
$suffixPatch = if ($Suffixes -eq 'luau') { 'distinct-luau-extension.patch' } else { 'dual-lua-luau-extension.patch' }
$otherPatch = if ($Suffixes -eq 'luau') { 'dual-lua-luau-extension.patch' } else { 'distinct-luau-extension.patch' }
Use-Patch $source 'builtin-vararg-return.patch'
# The 4.8 API adds GDExtensionManager.load_extension_from_function(const GDExtensionInitializationFunction*);
# the binding generator only skipped const void*. Raw pointers are skipped as a class.
Use-Patch $source 'skip-native-pointer-args.patch'
# 4.8 lists the editor-only GDScriptLanguageProtocol among singletons; fetching it outside
# the editor prints an ERROR on every extension load (games, --script runs).
Use-Patch $source 'editor-only-singletons.patch'
Use-Patch $source $suffixPatch @($otherPatch)

& git -C $source submodule update --init --depth 1 extern/godot-cpp extern/luau
if ($LASTEXITCODE) { throw 'Pinned dependency checkout failed.' }
$godotCpp = Join-Path $source 'extern/godot-cpp'
# godot-cpp 714c9e2 (4.4 era) reads typed dictionaries as "K;;V" (4.8 writes "K;V") and would
# redefine the <cstdint> limit macros that 4.8 now exports as global constants.
Use-Patch $godotCpp 'godot-cpp-beta2-api.patch'

# Bind against the API of the engine that will load the library, not the 4.4 dump in godot-cpp.
$dump = Join-Path $source '.language-kit-api'
New-Item -ItemType Directory -Force -Path $dump | Out-Null
Push-Location $dump
try {
    & $engine --headless --dump-extension-api | Out-Host
    if ($LASTEXITCODE) { throw 'Extension API dump failed.' }
} finally { Pop-Location }
$api = Join-Path $dump 'extension_api.json'
$header = (Get-Content -LiteralPath $api -TotalCount 12) -join "`n"
if ($header -notmatch '"version_minor": 8') { throw 'Dumped API is not a 4.8 API.' }
Copy-Item -LiteralPath $api -Destination (Join-Path $godotCpp 'gdextension/extension_api.json') -Force
$apiHash = (Get-FileHash -LiteralPath $api -Algorithm SHA256).Hash
# Generated bindings are not rebuilt when only the generator patches change; key them on
# the API and the two generator patches, and start from empty gen/ folders when that key moves.
$patchHashes = (@('skip-native-pointer-args.patch', 'godot-cpp-beta2-api.patch') |
    ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "patches/$_") -Algorithm SHA256).Hash }) -join ','
$stamp = Join-Path $dump 'generated-with.txt'
$key = "$apiHash;$patchHashes"
if (-not (Test-Path -LiteralPath $stamp) -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $key) {
    foreach ($generated in @((Join-Path $godotCpp 'gen'), (Join-Path $source 'gen'))) {
        if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated -Recurse -Force }
    }
}

& $PythonExecutable -c 'import sys; assert sys.version_info >= (3, 12), "Luau binding generator requires Python 3.12+"'
if ($LASTEXITCODE) { throw 'Python 3.12+ is required.' }
$venv = Join-Path $source '.language-kit-python'
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts/python.exe'))) {
    & $PythonExecutable -m venv $venv
    if ($LASTEXITCODE) { throw 'Creating isolated Python environment failed.' }
}
$python = Join-Path $venv 'Scripts/python.exe'
& $python -m pip install scons==4.11.1 tomli==2.4.0
if ($LASTEXITCODE) { throw 'Isolated build dependencies failed.' }
Push-Location $source
try {
    # SCons does not track bindgen/*.py as inputs of gen/luau_api.json: regenerate it every
    # time, so a changed generator patch or API dump cannot leave stale bindings behind.
    Remove-Item -LiteralPath (Join-Path $source 'gen/luau_api.json') -Force -ErrorAction SilentlyContinue
    foreach ($target in $Targets) {
        & $python -m SCons platform=windows "target=$target" arch=x86_64 "-j$Jobs"
        if ($LASTEXITCODE) { throw "Native Luau adapter build failed for $target." }
    }
    [IO.File]::WriteAllText($stamp, $key)
} finally { Pop-Location }

New-Item -ItemType Directory -Path (Join-Path $output 'bin') -Force | Out-Null
$libraries = [ordered]@{}
foreach ($target in $Targets) {
    $name = "libluau-script.windows.$target.x86_64.dll"
    Copy-Item -LiteralPath (Join-Path $source "bin/luau-script/$name") -Destination (Join-Path $output 'bin')
    $libraries[$name] = (Get-FileHash -LiteralPath (Join-Path $output "bin/$name") -Algorithm SHA256).Hash
}
$descriptor = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'luau.gdextension.in') -Raw
if ($Targets -notcontains 'template_release') {
    $descriptor = ($descriptor -split "`n" | Where-Object { $_ -notmatch '^windows\.release\.' }) -join "`n"
}
[IO.File]::WriteAllText((Join-Path $output 'luau.gdextension'), $descriptor, [Text.UTF8Encoding]::new($false))
foreach ($name in @('LICENSE.txt', 'COPYRIGHT.txt')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $output $name)
}
$info = [ordered]@{
    schema = 1
    upstream = $commit
    engine = $version
    extension_api_sha256 = $apiHash
    suffixes = [string[]]$(if ($Suffixes -eq 'luau') { 'luau' } else { 'luau', 'lua' })
    patches = @('builtin-vararg-return.patch', 'skip-native-pointer-args.patch', 'editor-only-singletons.patch', $suffixPatch, 'godot-cpp-beta2-api.patch')
    libraries = $libraries
    built = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
}
[IO.File]::WriteAllText((Join-Path $output 'build-info.json'), ($info | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host "LUAU_BUILD_PASS suffixes=$Suffixes targets=$($Targets -join ',') output=$output"
