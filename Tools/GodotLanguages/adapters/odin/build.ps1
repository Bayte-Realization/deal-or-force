param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$OdinExecutable = 'odin'
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $SourceDirectory rev-parse HEAD) -ne '93395901a76f62a9f2adff76c2eb38a185d91513') { throw 'odin-godot source is not the reviewed pin.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $OutputDirectory 'project'
$addon = Join-Path $project 'addons/dof_odin'
New-Item -ItemType Directory -Force -Path $addon | Out-Null
& $OdinExecutable build (Join-Path $PSScriptRoot 'src') "-collection:godot=$SourceDirectory" -build-mode:dll '-target:windows_amd64' "-out:$(Join-Path $addon 'dof_odin.dll')" -o:speed
if ($LASTEXITCODE -ne 0) { throw "Odin compilation failed: $LASTEXITCODE" }
[IO.File]::WriteAllText((Join-Path $project 'project.godot'), "config_version=5`n[application]`nconfig/name=`"Odin language probe`"`n[rendering]`nrenderer/rendering_method=`"gl_compatibility`"`n")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe.gd') -Destination $project -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dof_odin.gdextension'),(Join-Path $SourceDirectory 'LICENSE') -Destination $addon -Force
Write-Output "ADDON_ROOT=$addon"
Write-Output "PROBE_PROJECT=$project"
