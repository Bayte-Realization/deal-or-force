param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
Invoke-KitEditorBootstrap $GodotExecutable $ProjectDirectory
Write-Host 'LANGUAGE_KIT_PASS editor_bootstrap=true warm_import=true'
