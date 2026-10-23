#requires -Version 7.0
param(
    [string]$EngineDirectory = 'C:/GodotEngineNet 4.8-Beta1',
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot 'projects/AllSix'),
    [string]$JavaHome = 'C:/Program Files/Eclipse Adoptium/jdk-25.0.2.10-hotspot'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'kit-common.ps1')
$engine = Get-KitEngine $EngineDirectory
$lock = Read-KitLock $PSScriptRoot
if ($engine.Version -ne $lock.engine.version) { throw 'Select the reviewed engine version.' }
if (-not (Test-Path -LiteralPath (Join-Path $ProjectDirectory 'project.godot'))) { throw 'Create/install the AllSix workbench first.' }
if (-not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin/server/jvm.dll'))) { throw 'Select a valid Java home with -JavaHome.' }
# Deliberately opens an interactive editor only when the user runs this launcher.
# It never closes, restarts or changes another editor or the original game project.
& $engine.Executable --editor --path $ProjectDirectory "--jvm-path=$JavaHome"
