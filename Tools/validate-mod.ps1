<#
.SYNOPSIS
    Validate one or more Deal or Force mod directories using the pure-C# ModValidator.
.DESCRIPTION
    Checks manifest.json, scenarios/*/scenario.json, JSON/YAML syntax, patches/*.json,
    inline $op directives, technology prerequisites & cycles, scripted triggers & effects,
    country tags, diplomacy contradictions, era features, and EN/RU localization parity.
    Returns exit code 0 when valid and 1 when validation errors (or warnings under -Strict) are found.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath mods\_templates\historical_1914_era
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath mods\mod_a,mods\mod_b -Strict
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$ModPath,

    [switch]$Strict,

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $repoRoot 'Tools\FormableTests\FormableTests.csproj'
$prebuiltDll = Join-Path $repoRoot 'Tools\FormableTests\bin\Debug\net8.0\FormableTests.dll'

if (-not (Test-Path -LiteralPath $testProj)) {
    Write-Error "Could not locate FormableTests.csproj at '$testProj'."
    exit 2
}

$resolvedPaths = @()
foreach ($raw in $ModPath) {
    $candidate = $raw
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path (Get-Location).Path $candidate
        if (-not (Test-Path -LiteralPath $candidate)) {
            $candidate = Join-Path $repoRoot $raw
        }
    }
    $full = [System.IO.Path]::GetFullPath($candidate)
    $resolvedPaths += $full
}

$cliArgs = @('--validate-mod') + $resolvedPaths
if ($Strict) {
    $cliArgs += '--strict'
}

if ($NoBuild -and (Test-Path -LiteralPath $prebuiltDll)) {
    & dotnet $prebuiltDll @cliArgs
    exit $LASTEXITCODE
}

& dotnet run --project $testProj -- @cliArgs
exit $LASTEXITCODE
