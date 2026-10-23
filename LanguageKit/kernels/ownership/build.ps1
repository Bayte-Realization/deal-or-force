# SPDX-License-Identifier: GPL-3.0-only. See ../../LICENSE.
param(
    [string]$OutputDirectory,
    [string]$CargoExecutable,
    [string]$RustupDirectory,
    [string]$CargoDirectory,
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkDirectory = 'C:/Program Files (x86)/Windows Kits/10'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../..')).Path
$privateTools = Join-Path $repo '.fennara/languages/native/toolchains'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo '.fennara/languages/ownership-kernel' }
if (-not $CargoDirectory) { $CargoDirectory = Join-Path $privateTools 'cargo' }
if (-not $RustupDirectory) { $RustupDirectory = Join-Path $privateTools 'rustup' }
if (-not $CargoExecutable) { $CargoExecutable = Join-Path $CargoDirectory 'bin/cargo.exe' }
$CargoExecutable = (Resolve-Path -LiteralPath $CargoExecutable).Path
$RustupDirectory = (Resolve-Path -LiteralPath $RustupDirectory).Path
$CargoDirectory = (Resolve-Path -LiteralPath $CargoDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This recipe configures Windows x64/MSVC. See README.md for an unverified Unix command.'
}

# Resolve the linker and SDK in this child process. No machine/user environment,
# engine directory, live addon, global PATH, or installed Godot binary is changed.
if (-not $VisualStudioDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Pass -VisualStudioDirectory with MSVC C++ tools installed.' }
    $VisualStudioDirectory = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioDirectory) { throw 'MSVC C++ tools were not found.' }
}
$msvc = Get-ChildItem -LiteralPath (Join-Path $VisualStudioDirectory 'VC/Tools/MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk = Get-ChildItem -LiteralPath (Join-Path $WindowsSdkDirectory 'Lib') -Directory |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'um/x64/kernel32.lib')) -and
                   (Test-Path -LiteralPath (Join-Path $_.FullName 'ucrt/x64/ucrt.lib')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $msvc -or -not $sdk) { throw 'A complete MSVC/Windows SDK installation is required.' }
$linker = Join-Path $msvc.FullName 'bin/Hostx64/x64/link.exe'
if (-not (Test-Path -LiteralPath $linker)) { throw "MSVC x64 linker missing: $linker" }
$names = @('PATH', 'RUSTUP_HOME', 'CARGO_HOME', 'LIB', 'CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER')
$before = @{}
foreach ($name in $names) { $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:RUSTUP_HOME = $RustupDirectory
    $env:CARGO_HOME = $CargoDirectory
    $env:PATH = (Join-Path $CargoDirectory 'bin') + ';' + (Split-Path -Parent $linker) + ';' + $env:PATH
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = $linker
    $env:LIB = (Join-Path $msvc.FullName 'lib/x64') + ';' + (Join-Path $sdk.FullName 'um/x64') + ';' +
               (Join-Path $sdk.FullName 'ucrt/x64') + ';' + $env:LIB
    $manifest = Join-Path $PSScriptRoot 'Cargo.toml'
    $target = Join-Path $OutputDirectory 'target'
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    & $CargoExecutable --version
    if ($LASTEXITCODE -ne 0) { throw 'Private Cargo did not start.' }
    & $CargoExecutable test --release --locked --offline --target x86_64-pc-windows-msvc --manifest-path $manifest --target-dir $target
    if ($LASTEXITCODE -ne 0) { throw "Ownership kernel tests failed: $LASTEXITCODE" }
    & $CargoExecutable build --release --locked --offline --target x86_64-pc-windows-msvc --manifest-path $manifest --target-dir $target
    if ($LASTEXITCODE -ne 0) { throw "Ownership kernel build failed: $LASTEXITCODE" }
    $dll = Join-Path $target 'x86_64-pc-windows-msvc/release/dof_graph_kernel.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "Expected kernel DLL missing: $dll" }
    Write-Output "NATIVE_KERNEL_DLL=$dll"
    Write-Output "NATIVE_KERNEL_SHA256=$((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
}
