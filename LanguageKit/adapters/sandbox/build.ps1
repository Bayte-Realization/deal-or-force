[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$CompilerBin,
    [ValidateRange(1,16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne 'b43ed1619abc12a669c59cb0aa25c4582873a7b9') {
    throw 'Use the pinned godot-sandbox v0.60 source revision.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $source -or $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Output must be outside the upstream source directory.'
}
$compiler = (Resolve-Path -LiteralPath $CompilerBin).Path
if (-not (Test-Path -LiteralPath (Join-Path $compiler 'g++.exe'))) { throw 'A MinGW-w64 C++ compiler is required.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& git -C $source submodule update --init --depth 1 ext/godot-cpp ext/libriscv
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize pinned sandbox dependencies.' }
$patch = Join-Path $PSScriptRoot 'patches/0001-godot48-language-registration-and-property-api.patch'
& git -C $source apply --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    & git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox compatibility patch could not be applied.' }
} else {
    & git -C $source apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patch does not match this checkout.' }
}
# With core.symlinks=false Git writes the target path into a regular file.
# Materialize only this known, in-repository header used by the native build.
$header = Join-Path $source 'src/syscalls.h'
if ((Get-Content -LiteralPath $header -Raw).Trim() -eq '../program/cpp/docker/api/syscalls.h') {
    Copy-Item -LiteralPath (Join-Path $source 'program/cpp/docker/api/syscalls.h') -Destination $header -Force
}
$oldPath = $env:PATH
try {
    $env:PATH = $compiler + ';' + $oldPath
    & scons -C $source platform=windows target=template_release arch=x86_64 "-j$Jobs" *> (Join-Path $output 'native-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Sandbox build failed; see $output/native-build.log" }
} finally { $env:PATH = $oldPath }
$archive = Join-Path $output 'godot-sandbox-v0.60.zip'
$expected = '6b7f4c6a74d283352036c2cf6baf4672059ab17d4eaf00386a0357eda73ce865'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/libriscv/godot-sandbox/releases/download/v0.60/godot-sandbox.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $expected) { throw 'Sandbox addon archive checksum mismatch.' }
$project = Join-Path $output 'project'
if (-not (Test-Path -LiteralPath $project)) { Expand-Archive -LiteralPath $archive -DestinationPath $project }
Copy-Item -Path (Join-Path $PSScriptRoot 'template/*') -Destination $project -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $project '.godot') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'template/extension_list.cfg.in') -Destination (Join-Path $project '.godot/extension_list.cfg') -Force
$library = Join-Path $source 'bin/addons/godot_sandbox/bin/libgodot_riscv.windows.template_release.x86_64.dll'
Copy-Item -LiteralPath $library -Destination (Join-Path $project 'addons/godot_sandbox/bin/libgodot_riscv.windows.template_release.x86_64.dll') -Force
[pscustomobject]@{
    projectRoot = $project
    addonRoot = Join-Path $project 'addons/godot_sandbox'
    descriptor = 'addons/godot_sandbox/bin/godot-riscv.gdextension'
    probeScript = 'res://probe.gd'
    sourceRevision = 'b43ed1619abc12a669c59cb0aa25c4582873a7b9'
    archiveSha256 = $expected
    librarySha256 = (Get-FileHash -LiteralPath $library).Hash.ToLowerInvariant()
}
