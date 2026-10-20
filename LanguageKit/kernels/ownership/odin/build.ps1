# SPDX-License-Identifier: GPL-3.0-only. See ../../../LICENSE.
param(
    [string]$OutputDirectory,
    [string]$OdinExecutable
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../../..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo '.fennara/languages/ownership-odin/release' }
if (-not $OdinExecutable) { $OdinExecutable = Join-Path $repo '.fennara/languages/native/toolchains/odin/dist/odin.exe' }
$OdinExecutable = (Resolve-Path -LiteralPath $OdinExecutable).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$mac = $PSVersionTable.ContainsKey('OS') -and $PSVersionTable.OS -match 'Darwin|macOS'
$name = if ($windows) { 'dof_graph_kernel.dll' } elseif ($mac) { 'libdof_graph_kernel.dylib' } else { 'libdof_graph_kernel.so' }
$library = Join-Path $OutputDirectory $name
& $OdinExecutable version
if ($LASTEXITCODE -ne 0) { throw 'Odin compiler did not start.' }
$testName = if ($windows) { 'ownership-abi-tests.exe' } else { 'ownership-abi-tests' }
$testArguments = @('run', (Join-Path $PSScriptRoot 'tests'), "-out:$(Join-Path $OutputDirectory $testName)", '-o:speed')
if ($windows) { $testArguments += '-target:windows_amd64' }
& $OdinExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw "Odin ownership ABI tests failed: $LASTEXITCODE" }
$arguments = @('build', (Join-Path $PSScriptRoot 'src'), '-build-mode:dll', "-out:$library", '-o:speed')
if ($windows) { $arguments += '-target:windows_amd64' }
# Keep default bounds checks. This is the same fully validated ABI as Rust.
& $OdinExecutable @arguments
if ($LASTEXITCODE -ne 0) { throw "Odin ownership kernel build failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $library)) { throw "Expected kernel library missing: $library" }
Write-Output "NATIVE_KERNEL_DIRECTORY=$OutputDirectory"
Write-Output "NATIVE_KERNEL_DLL=$library"
Write-Output "NATIVE_KERNEL_SHA256=$((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash)"
