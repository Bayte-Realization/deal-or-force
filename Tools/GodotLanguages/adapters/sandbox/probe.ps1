[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
$project = (Resolve-Path -LiteralPath $ProjectDirectory).Path
Invoke-KitEditorBootstrap $GodotExecutable $project
$result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $project, '--script', 'res://probe.gd')
if ($result.Output -notmatch 'SANDBOX_PROBE:PASS sum=42 restrictions=true') {
    throw 'Sandbox did not return the expected result.'
}
Write-Output 'SANDBOX_ADAPTER:PASS import=0 runtime=0'
