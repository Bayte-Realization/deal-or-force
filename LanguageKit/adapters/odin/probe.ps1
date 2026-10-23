param([Parameter(Mandatory)][string]$GodotExecutable,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
$project = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'project'
Invoke-KitEditorBootstrap $GodotExecutable $project
Write-Output 'ODIN_IMPORT_EXIT=0'
$runtimeLog = & $GodotExecutable --headless --path $project --script res://probe.gd --quit-after 240 2>&1
$runtimeCode = $LASTEXITCODE
$runtimeText = $runtimeLog -join "`n"
$runtimeLog | Write-Output
Write-Output "ODIN_RUNTIME_EXIT=$runtimeCode"
if ($runtimeCode -ne 0 -or $runtimeText -match 'ERROR:|SCRIPT ERROR:|CrashHandlerException' -or $runtimeText -notmatch 'ODIN_PROBE PASS') { throw 'odin runtime probe failed.' }
