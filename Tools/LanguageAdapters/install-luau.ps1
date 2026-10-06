param([switch]$Activate, [ValidateRange(1, 32)][int]$Jobs = 8)
$ErrorActionPreference = 'Stop'
$adapterRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceRoot = Join-Path $adapterRoot 'build/godot-luau-script'
$installRoot = Join-Path $adapterRoot 'game/extensions/luau'
$sourceCommit = '7942ce6def8023596ca0cb8d819b032eb40a4bce'
$returnPatch = Join-Path $PSScriptRoot 'patches/builtin-vararg-return.patch'
foreach ($tool in @('git', 'uv')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required tool missing: $tool" }
}
if (-not (Test-Path -LiteralPath $sourceRoot)) {
    & git clone --no-checkout https://github.com/Fumohouse/godot-luau-script.git $sourceRoot
    if ($LASTEXITCODE) { throw 'Source clone failed' }
    & git -C $sourceRoot checkout --detach $sourceCommit
    if ($LASTEXITCODE) { throw 'Pinned source checkout failed' }
}
$actualCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -or $actualCommit -ne $sourceCommit) { throw 'Unexpected source revision; existing checkout preserved' }
$localChanges = @(& git -C $sourceRoot diff --name-only HEAD)
if ($localChanges.Count -eq 0) {
    & git -C $sourceRoot apply --check $returnPatch
    if ($LASTEXITCODE) { throw 'Pinned return-value fix does not match upstream source' }
    & git -C $sourceRoot apply $returnPatch
    if ($LASTEXITCODE) { throw 'Applying return-value fix failed' }
} else {
    if ($localChanges.Count -ne 1 -or $localChanges[0] -ne 'src/core/godot_builtin_bindings.cpp') {
        throw 'Source checkout has unexpected changes; existing files preserved'
    }
    & git -C $sourceRoot apply --reverse --check $returnPatch
    if ($LASTEXITCODE) { throw 'Source changes differ from the recorded return-value fix' }
}
& git -C $sourceRoot submodule update --init --depth 1 extern/godot-cpp extern/luau
if ($LASTEXITCODE) { throw 'Dependency checkout failed' }
Push-Location $sourceRoot
try {
    # uv keeps this SCons installation isolated from the user's Python environment.
    & uv run --with scons==4.11.1 --with tomli scons platform=windows target=editor arch=x86_64 "-j$Jobs"
    if ($LASTEXITCODE) { throw 'Native adapter build failed' }
} finally { Pop-Location }
$binary = Join-Path $sourceRoot 'bin/luau-script/libluau-script.windows.editor.x86_64.dll'
if (-not (Test-Path -LiteralPath $binary)) { throw 'Build produced no adapter DLL' }
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $installRoot
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE.txt') -Destination (Join-Path $installRoot 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'COPYRIGHT.txt') -Destination (Join-Path $installRoot 'COPYRIGHT.txt')
if ($Activate) {
    Copy-Item -LiteralPath (Join-Path $installRoot 'luau_script.gdextension.in') -Destination (Join-Path $installRoot 'luau_script.gdextension')
    Write-Host 'Experimental Luau adapter activated. Restart the Godot editor to expose it in Attach Script.'
} else {
    Write-Host 'DLL built. Use -Activate to install the descriptor; no project setting has been changed.'
}
Get-FileHash -LiteralPath $binary -Algorithm SHA256
