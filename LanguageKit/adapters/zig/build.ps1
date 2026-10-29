param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ZigExecutable = 'zig'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne 'c1b0cc3c7befc4c2483624d078e1e650b07b2dfa') { throw 'Unexpected gdzig source revision.' }
$zig = (Get-Command $ZigExecutable -ErrorAction Stop).Source
$version = (& $zig version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -ne '0.17.0') { throw "This pin requires Zig 0.17.0, found $version." }
$output = New-KitProbeWorkspace $OutputDirectory $source 'zig'
$crate = Join-Path $output 'crate'
$project = Join-Path $output 'project'
$addon = Join-Path $project 'addons/dof_zig'
New-Item -ItemType Directory -Force -Path $crate,$addon | Out-Null
Copy-KitTemplate (Join-Path $PSScriptRoot 'build.zig.in') (Join-Path $crate 'build.zig')
$relativeSource = [IO.Path]::GetRelativePath($crate, $source).Replace('\','/')
if ([IO.Path]::IsPathRooted($relativeSource)) { throw 'Zig source/output must be on the same drive for a local path dependency.' }
$zon = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'build.zig.zon.in')).Replace('@GDZIG@', $relativeSource)
Copy-KitTemplate -Target (Join-Path $crate 'build.zig.zon') -Content $zon
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'src') -File) { Copy-KitTemplate $file.FullName (Join-Path $crate "src/$($file.Name)") }
$oldPath = $env:PATH
Push-Location -LiteralPath $crate
try {
    # Upstream bindgen invokes `zig fmt` by name even if the compiler was explicit.
    $env:PATH = (Split-Path -Parent $zig) + ';' + $oldPath
    & $zig build -Doptimize=ReleaseSafe "-Dgodot-path=$GodotExecutable" --prefix (Join-Path $output 'prefix')
} finally { $env:PATH = $oldPath; Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "Zig build failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath (Join-Path $addon 'dof_zig.dll'))) { throw 'Zig did not produce its expected DLL.' }
Copy-KitTemplate (Join-Path $PSScriptRoot 'dof_zig.gdextension') (Join-Path $addon 'dof_zig.gdextension')
Copy-KitTemplate (Join-Path $PSScriptRoot 'probe.gd') (Join-Path $project 'probe.gd')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $addon -Force
New-KitGodotProject $GodotExecutable $project 'Zig ABI probe'
Write-Output "PROBE_PROJECT=$project"
