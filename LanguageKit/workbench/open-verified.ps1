param(
    [ValidateSet('rust','zig','go')][string]$Language='rust',
    [string]$EngineDirectory='C:/GodotEngineNet 4.8-Beta2'
)
$ErrorActionPreference='Stop'
$kit=Split-Path -Parent $PSScriptRoot
. (Join-Path $kit 'kit-common.ps1')
$engine=Get-KitEngine $EngineDirectory
if($engine.Version -ne (Read-KitLock $kit).engine.version){throw 'Engine version differs from this kit target.'}
$project=Join-Path $kit ".cache/verified-beta2/$Language/project"
if(-not(Test-Path (Join-Path $project 'project.godot'))){throw "Build the $Language adapter first: $project"}
# Interactive only when the user explicitly runs this launcher. Never stops another editor.
& $engine.Executable --editor --path $project
