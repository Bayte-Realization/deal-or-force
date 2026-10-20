param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
$result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $ProjectDirectory, '--script', 'probe.gd', '--quit-after', '120')
if ($result.Output -notmatch 'LANGUAGE_KIT_PASS language=fsharp') {
    throw 'F# Godot runtime probe did not reach its success signal.'
}
