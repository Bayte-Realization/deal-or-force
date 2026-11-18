<#
.SYNOPSIS
  Build the F# corecode template against an exported game and load it the way the game does.

.DESCRIPTION
  1. Compiles mods/_templates/fsharp_mod/corecode/ExampleFSharpMod.fsproj with
     -p:GameAssembly=<exported DealOrForce.dll> into a scratch mod package laid out as the
     manifest says (corecode/bin/Debug/net10.0/), and copies the template's manifest.json there.
  2. Builds Tools/ManagedModTests against the same exported DealOrForce.dll / GodotSharp.dll.
  3. Runs it with --corecode-load-only: manifest path through ModCodePaths, a private
     ManagedModLoadContext, OnLoad / OnCampaignCreated / OnDayAdvanced, two host commands,
     Unload, and a check that the collectible context is actually collected.

  Never compiles DealOrForce.csproj and never starts Godot, so it is safe while an editor has
  the project open. All build output (bin and obj) goes under -Scratch.
#>
param(
    [string]$GameAssembly,
    [string]$Scratch
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Scratch) { $Scratch = Join-Path $root 'tmp_logs\languages\corecode-load' }
if (-not $GameAssembly) {
    $found = Get-ChildItem (Join-Path $root 'build') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_' } | Sort-Object Name -Descending |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter DealOrForce.dll -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*data_DealOrForce_*' } } | Select-Object -First 1
    if (-not $found) { throw 'No exported build\<stamp>\...\data_DealOrForce_*\DealOrForce.dll found; pass -GameAssembly.' }
    $GameAssembly = $found.FullName
}
$GameAssembly = (Resolve-Path $GameAssembly).Path
Write-Host "GAME_ASSEMBLY $GameAssembly"

$package = Join-Path $Scratch 'example_fsharp_mod'
$modOut = Join-Path $package 'corecode\bin\Debug\net10.0'
$template = Join-Path $root 'mods\_templates\fsharp_mod'
New-Item -ItemType Directory -Force -Path $modOut | Out-Null
Copy-Item (Join-Path $template 'manifest.json') (Join-Path $package 'manifest.json') -Force

& dotnet build (Join-Path $template 'corecode\ExampleFSharpMod.fsproj') -c Debug -nologo -v:minimal `
    "-p:GameAssembly=$GameAssembly" "-p:BaseIntermediateOutputPath=$(Join-Path $Scratch 'obj\mod')\" -o $modOut
if ($LASTEXITCODE) { throw 'F# corecode template did not build against the exported game.' }

$testOut = Join-Path $Scratch 'test'
& dotnet build (Join-Path $PSScriptRoot 'ManagedModTests.csproj') -c Release -nologo -v:minimal `
    "-p:GameAssembly=$GameAssembly" "-p:BaseIntermediateOutputPath=$(Join-Path $Scratch 'obj\test')\" -o $testOut
if ($LASTEXITCODE) { throw 'ManagedModTests did not build against the exported game.' }

& dotnet (Join-Path $testOut 'ManagedModTests.dll') --corecode-load-only --package $package
if ($LASTEXITCODE) { throw "corecode load test failed (exit $LASTEXITCODE)." }
