param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$NimExecutable = 'nim'
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $SourceDirectory rev-parse HEAD) -ne '15b69a92ba2642f2bea117e01e3bbcd112c9f57d') { throw 'gdext-nim source is not the reviewed pin.' }
$bootstrapPatch = Join-Path $PSScriptRoot 'patches/windows-runtime-bootstrap.patch'
if (-not (Get-Content -LiteralPath (Join-Path $SourceDirectory 'src/gdext.nim') -Raw).Contains('dof_nim_runtime_initialized')) {
    & git -C $SourceDirectory apply --unidiff-zero --check $bootstrapPatch
    if ($LASTEXITCODE -ne 0) { throw 'Nim runtime bootstrap patch does not apply cleanly.' }
    & git -C $SourceDirectory apply --unidiff-zero $bootstrapPatch
    if ($LASTEXITCODE -ne 0) { throw 'Nim runtime bootstrap patch failed.' }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $OutputDirectory 'project'
$src = Join-Path $OutputDirectory 'nim'
$addon = Join-Path $project 'addons/dof_nim'
New-Item -ItemType Directory -Force -Path $src,$addon | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bootstrap.nim'),(Join-Path $PSScriptRoot 'config.nims') -Destination $src -Force
[IO.File]::WriteAllText((Join-Path $project 'project.godot'), "config_version=5`n[application]`nconfig/name=`"Nim language probe`"`n[rendering]`nrenderer/rendering_method=`"gl_compatibility`"`n")
Copy-Item -LiteralPath (Join-Path $project 'project.godot') -Destination $src -Force
Push-Location $src
try {
    # Upstream's generated C interface omits stdint includes; MSVC needs them explicitly.
    # An embedded library must not replace Godot/.NET's process-wide signal handlers.
    & $NimExecutable c "--path:$(Join-Path $SourceDirectory 'src')" --cc:vcc --passC:/FIstdint.h -d:noSignalHandler --nimcache:cache "--out:$(Join-Path $addon 'DofNim.windows.debug.x86_64.dll')" bootstrap.nim
    if ($LASTEXITCODE -ne 0) { throw "Nim compilation failed: $LASTEXITCODE" }
} finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dof_nim.gdextension') -Destination $addon -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe.gd') -Destination $project -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'LICENSE') -Destination $addon -Force
Write-Output "ADDON_ROOT=$addon"
Write-Output "PROBE_PROJECT=$project"
