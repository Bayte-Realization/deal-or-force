# SPDX-License-Identifier: GPL-3.0-only. See ../../../../LICENSE.
param(
    [ValidateSet('all', 'abi2', 'status3', 'malformed')][string]$Case = 'all',
    [string]$OutputDirectory,
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkDirectory = 'C:/Program Files (x86)/Windows Kits/10'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../../../..')).Path
$privateTools = Join-Path $repo '.fennara/languages/native/toolchains'
$cargoDirectory = (Resolve-Path -LiteralPath (Join-Path $privateTools 'cargo')).Path
$rustupDirectory = (Resolve-Path -LiteralPath (Join-Path $privateTools 'rustup')).Path
$cargo = (Resolve-Path -LiteralPath (Join-Path $cargoDirectory 'bin/cargo.exe')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo '.fennara/languages/ownership-kernel-fixtures' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Fixture recipe currently targets Windows x64/MSVC.' }
if (-not $VisualStudioDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
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
    $env:RUSTUP_HOME = $rustupDirectory
    $env:CARGO_HOME = $cargoDirectory
    $env:PATH = (Join-Path $cargoDirectory 'bin') + ';' + (Split-Path -Parent $linker) + ';' + $env:PATH
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = $linker
    $env:LIB = (Join-Path $msvc.FullName 'lib/x64') + ';' + (Join-Path $sdk.FullName 'um/x64') + ';' +
               (Join-Path $sdk.FullName 'ucrt/x64') + ';' + $env:LIB
    $cases = if ($Case -eq 'all') { @('abi2', 'status3', 'malformed') } else { @($Case) }
    foreach ($fixture in $cases) {
        $target = Join-Path $OutputDirectory $fixture
        & $cargo build --release --locked --offline --features $fixture --target x86_64-pc-windows-msvc --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --target-dir $target
        if ($LASTEXITCODE -ne 0) { throw "Fixture $fixture failed to build: $LASTEXITCODE" }
        $directory = Join-Path $target 'x86_64-pc-windows-msvc/release'
        $dll = Join-Path $directory 'dof_graph_kernel.dll'
        if (-not (Test-Path -LiteralPath $dll)) { throw "Expected fixture DLL missing: $dll" }
        Write-Output "OWNERSHIP_FIXTURE_$($fixture.ToUpperInvariant())=$directory"
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
}
