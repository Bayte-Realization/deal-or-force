param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PythonExecutable = 'python',
    [ValidateRange(1, 16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$version = (& $GodotExecutable --version | Out-String).Trim()
if ($LASTEXITCODE -or $version -notmatch '^4\.8\.beta1\.mono\.') {
    throw "This recipe targets tested Godot 4.8 beta1 .NET, found: $version"
}
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$commit = (& git -C $source rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -or $commit -ne '58cb05e62557741ff646f0096fa4fdc8d09b1ae3') { throw 'Unexpected Lua upstream revision; source preserved.' }
$changes = @(& git -C $source diff --name-only HEAD) | Where-Object { $_ -notin @('src/LuaTable.cpp', 'doc_classes/LuaTable.xml') }
if ($changes.Count) { throw "Unexpected upstream edits preserved: $($changes -join ', ')" }
$patch = Join-Path $PSScriptRoot 'patches/godot48-table-members.patch'
& git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    & git -C $source apply --check $patch
    if ($LASTEXITCODE) { throw 'LuaTable source does not match the recorded 4.8 patch.' }
    & git -C $source apply $patch
    if ($LASTEXITCODE) { throw 'LuaTable compatibility patch failed.' }
}
& git -C $source submodule update --init --depth 1 lib/godot-cpp lib/lua lib/sol2 lib/tree-sitter lib/tree-sitter-lua
if ($LASTEXITCODE) { throw 'Pinned Lua dependency checkout failed.' }
& $PythonExecutable -c 'import sys; assert sys.version_info >= (3, 12), "Binding generator requires Python 3.12+"'
if ($LASTEXITCODE) { throw 'Python 3.12+ is required.' }
$venv = Join-Path $source '.language-kit-python'
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts/python.exe'))) {
    & $PythonExecutable -m venv $venv
    if ($LASTEXITCODE) { throw 'Creating isolated build environment failed.' }
}
$python = Join-Path $venv 'Scripts/python.exe'
& $python -m pip install scons==4.11.1
if ($LASTEXITCODE) { throw 'SCons installation failed.' }
Push-Location $source
try {
    & $python -m SCons platform=windows target=editor arch=x86_64 lua_runtime=lua "-j$Jobs"
    if ($LASTEXITCODE) { throw 'Lua native adapter build failed.' }
} finally { Pop-Location }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $output 'lua.gdextension')) { throw 'Existing addon preserved; choose a fresh output directory.' }
New-Item -ItemType Directory -Path (Join-Path $output 'bin') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'addons/lua-gdextension/build/libluagdextension.windows.editor.x86_64.dll') -Destination (Join-Path $output 'bin')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lua.gdextension.in') -Destination (Join-Path $output 'lua.gdextension')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $output 'LICENSE')
foreach ($item in @(
    @('lib/godot-cpp/LICENSE.md', 'godot-cpp-MIT.txt'),
    @('lib/lua/lua.h', 'Lua-header-with-MIT-notice.txt'),
    @('lib/sol2/LICENSE.txt', 'sol2-MIT.txt'),
    @('lib/tree-sitter/LICENSE', 'tree-sitter-MIT.txt'),
    @('lib/tree-sitter-lua/LICENSE.md', 'tree-sitter-lua-MIT.txt')
)) {
    Copy-Item -LiteralPath (Join-Path $source $item[0]) -Destination (Join-Path $output "licenses/$($item[1])")
}
Write-Host "Lua (not Luau) editor addon built in $output. Release/export targets are not enabled."
