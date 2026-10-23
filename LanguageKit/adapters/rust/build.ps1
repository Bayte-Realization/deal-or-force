param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CargoExecutable = 'cargo'
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$GodotExecutable = (Resolve-Path -LiteralPath $GodotExecutable).Path
if ((& git -C $SourceDirectory rev-parse HEAD) -ne '0b680b4d59c4790d4673b2293110687d2e5261c7') { throw 'gdext source is not the reviewed pin.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $OutputDirectory 'project'
$crate = Join-Path $OutputDirectory 'crate'
$addon = Join-Path $project 'addons/dof_rust'
New-Item -ItemType Directory -Force -Path $crate,$addon | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src') -Destination $crate -Recurse -Force
$manifest = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Cargo.toml.in') -Raw).Replace('@GDEXT@', $SourceDirectory.Replace('\','/'))
[IO.File]::WriteAllText((Join-Path $crate 'Cargo.toml'), $manifest)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Cargo.lock') -Destination $crate -Force
[IO.File]::WriteAllText((Join-Path $project 'project.godot'), "config_version=5`n[application]`nconfig/name=`"Rust language probe`"`n[rendering]`nrenderer/rendering_method=`"gl_compatibility`"`n")
$env:GDRUST_GODOT_BIN = $GodotExecutable
& $CargoExecutable build --locked --jobs 2 --manifest-path (Join-Path $crate 'Cargo.toml') --target-dir (Join-Path $OutputDirectory 'target')
if ($LASTEXITCODE -ne 0) { throw "Rust compilation failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'target/debug/dof_languagekit_rust.dll') -Destination $addon -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dof_rust.gdextension'),(Join-Path $SourceDirectory 'License.txt') -Destination $addon -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe.gd') -Destination $project -Force
Write-Output "ADDON_ROOT=$addon"
Write-Output "PROBE_PROJECT=$project"
