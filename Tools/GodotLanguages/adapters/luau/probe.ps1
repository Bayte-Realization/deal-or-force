param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$AddonDirectory,
    [Parameter(Mandatory)][string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
Assert-KitInstallPath $ProjectDirectory
if (Test-Path -LiteralPath (Join-Path $ProjectDirectory 'project.godot')) {
    throw 'Choose a fresh isolated project directory; existing project preserved.'
}
New-Item -ItemType Directory -Path $ProjectDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'probe/*') -Destination $ProjectDirectory -Recurse
$addons = Join-Path $ProjectDirectory 'addons'
New-Item -ItemType Directory -Path $addons -Force | Out-Null
Copy-Item -LiteralPath $AddonDirectory -Destination (Join-Path $addons 'dof_luau') -Recurse
$result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $ProjectDirectory, '--script', 'probe.gd', '--quit-after', '120')
if ($result.Output -notmatch 'LANGUAGE_KIT_PASS language=luau') {
    throw 'Luau Godot runtime probe did not reach its success signal.'
}
