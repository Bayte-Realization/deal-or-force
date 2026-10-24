# SPDX-License-Identifier: GPL-3.0-only. Uses an explicitly supplied portable compiler.
param([Parameter(Mandatory=$true)][string]$OdinExecutable, [string]$Save, [switch]$World)
$ErrorActionPreference = 'Stop'
$taskRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$taskOut = Join-Path $taskRepo 'build/exposure-kernel-bench'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
& $OdinExecutable version
if ($LASTEXITCODE -ne 0) { throw 'Odin did not start' }
& $OdinExecutable build (Join-Path $taskRepo 'Tools/GodotLanguages/kernels/exposure/odin/src') '-build-mode:dll' "-out:$taskOut/dof_exposure_kernel.dll" '-o:speed' '-target:windows_amd64'
if ($LASTEXITCODE -ne 0) { throw 'Odin exposure kernel did not compile' }
Write-Output "KERNEL_SHA256=$((Get-FileHash -LiteralPath "$taskOut/dof_exposure_kernel.dll").Hash)"
dotnet build (Join-Path $PSScriptRoot 'ExposureKernelBench.csproj') -c Release -o "$taskOut/host" -v q -nologo -m:1
if ($LASTEXITCODE -ne 0) { throw 'Exposure bench did not compile' }
$taskArgs = @("$taskOut/host/ExposureKernelBench.dll", "$taskOut/dof_exposure_kernel.dll")
if ($World) { $taskArgs += '--world' }
if ($Save) { $taskArgs += @('--save', [IO.Path]::GetFullPath($Save)) }
Push-Location $taskRepo
try {
    & dotnet @taskArgs
    if ($LASTEXITCODE -ne 0) { throw 'Exposure benchmark failed' }
} finally { Pop-Location }
