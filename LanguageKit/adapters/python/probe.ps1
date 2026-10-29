param([Parameter(Mandatory)][string]$GodotExecutable,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
Test-KitScalarAdapter $GodotExecutable $OutputDirectory 'PYTHON'
