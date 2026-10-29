param(
    [string]$OdinExe = "odin",
    [string]$OutDir = "$PSScriptRoot/bin"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outFile = Join-Path $OutDir "dof_raster_kernel.dll"
& $OdinExe build "$PSScriptRoot/src" -build-mode:dll -out:$outFile -o:speed -no-bounds-check
if ($LASTEXITCODE -ne 0) { throw "Odin raster kernel build failed with exit code $LASTEXITCODE" }
Write-Host "Built Odin raster kernel: $outFile"
