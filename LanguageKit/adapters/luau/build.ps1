param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PythonExecutable = 'python',
    [ValidateRange(1, 16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$engine = (Resolve-Path -LiteralPath $GodotExecutable).Path
$version = (& $engine --version | Out-String).Trim()
if ($LASTEXITCODE -or $version -notmatch '^4\.8\.beta1\.mono\.') {
    throw "This experimental adapter was tested only on Godot 4.8 beta1 .NET, found: $version"
}
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$commit = (& git -C $source rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -or $commit -ne '7942ce6def8023596ca0cb8d819b032eb40a4bce') {
    throw 'Unexpected Luau upstream revision; source preserved.'
}
$unexpected = @(& git -C $source diff --name-only HEAD) | Where-Object {
    $_ -notin @('src/core/godot_builtin_bindings.cpp', 'src/scripting/resource_format_luau_script.cpp',
        'src/scripting/luau_script.cpp', 'src/scripting/luau_script.h', 'src/scripting/luau_cache.cpp',
        'src/scripting/luau_lib.cpp', 'src/analysis/analysis_utils.cpp', 'src/analysis/annotation_class_def.cpp')
}
if ($unexpected.Count) { throw "Unexpected tracked edits preserved: $($unexpected -join ', ')" }
foreach ($name in @('builtin-vararg-return.patch', 'distinct-luau-extension.patch')) {
    $patch = Join-Path $PSScriptRoot "patches/$name"
    & git -C $source apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -eq 0) { continue }
    & git -C $source apply --check $patch
    if ($LASTEXITCODE) { throw "Source does not match the recorded patch: $name" }
    & git -C $source apply $patch
    if ($LASTEXITCODE) { throw "Applying patch failed: $name" }
}
& git -C $source submodule update --init --depth 1 extern/godot-cpp extern/luau
if ($LASTEXITCODE) { throw 'Pinned dependency checkout failed.' }
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
    & $python -m SCons platform=windows target=editor arch=x86_64 "-j$Jobs"
    if ($LASTEXITCODE) { throw 'Native Luau adapter build failed.' }
} finally { Pop-Location }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $output 'luau.gdextension')) {
    throw 'Existing addon preserved. Build to a new output directory, do not replace a loaded DLL.'
}
New-Item -ItemType Directory -Path (Join-Path $output 'bin') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'bin/luau-script/libluau-script.windows.editor.x86_64.dll') -Destination (Join-Path $output 'bin')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'luau.gdextension.in') -Destination (Join-Path $output 'luau.gdextension')
foreach ($name in @('LICENSE.txt', 'COPYRIGHT.txt')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $output $name)
}
Write-Host "Experimental Luau editor addon built in $output. No release/export target is enabled."
