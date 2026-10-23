[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ProjectDirectory,
    [Parameter(Mandatory)][string]$JavaHome
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../kit-common.ps1')
$project = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaHome).Path
    Invoke-KitEditorBootstrap $GodotExecutable $project
    $result = Invoke-KitGodot $GodotExecutable @('--headless', '--path', $project, "--jvm-path=$JavaHome", '--script', 'res://probe.gd')
    if ($result.Output -notmatch 'KOTLIN_PROBE:PASS sum=42 backend=Kotlin/JVM') {
        throw 'Kotlin did not return the expected probe result.'
    }
} finally {
    $env:JAVA_HOME = $previousJavaHome
}
Write-Output 'KOTLIN_ADAPTER:PASS import=0 runtime=0'
