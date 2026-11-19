[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$RemoveDeployed,
    [switch]$SkipTest,
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Join-Path $repoRoot 'native\dof_kernels\Cargo.toml'
$targetDir = Join-Path $repoRoot 'tmp_logs\rust_target'
$profile = if ($DebugBuild) { 'debug' } else { 'release' }

if (-not (Test-Path $manifest)) {
    throw "Cargo manifest not found: $manifest"
}

$defaultRustup = 'D:\Godot Projects\dof-fw-data\tmp_logs\rust\rustup'
$defaultCargo = 'D:\Godot Projects\dof-fw-data\tmp_logs\rust\cargo'
$cargoExe = Join-Path $defaultCargo 'bin\cargo.exe'
if (-not (Test-Path $cargoExe)) {
    $cmd = Get-Command cargo -ErrorAction SilentlyContinue
    if ($null -eq $cmd) {
        throw "Rust cargo executable not found at $cargoExe or on PATH."
    }
    $cargoExe = $cmd.Source
}
else {
    $env:RUSTUP_HOME = $defaultRustup
    $env:CARGO_HOME = $defaultCargo
}

$env:CARGO_TARGET_DIR = $targetDir
if ([string]::IsNullOrWhiteSpace($env:RUSTFLAGS)) {
    $env:RUSTFLAGS = '-C target-cpu=x86-64-v3'
}

if ($RemoveDeployed) {
    $deployed = Get-ChildItem -Path (Join-Path $repoRoot 'tmp_logs\build') -Filter 'dof_kernels.dll' -Recurse -ErrorAction SilentlyContinue
    foreach ($file in $deployed) {
        Remove-Item $file.FullName -Force
        Write-Host "Removed $($file.FullName)"
    }
    $godotBins = Get-ChildItem -Path (Join-Path $repoRoot '.godot\mono\temp\bin') -Filter 'dof_kernels.dll' -Recurse -ErrorAction SilentlyContinue
    foreach ($file in $godotBins) {
        Remove-Item $file.FullName -Force
        Write-Host "Removed $($file.FullName)"
    }
    if (-not $Deploy) {
        exit 0
    }
}

$cargoArgs = @('--manifest-path', $manifest, '--target-dir', $targetDir)
if (-not $DebugBuild) {
    $cargoArgs += '--release'
}

if (-not $SkipTest) {
    Write-Host "Running Rust unit tests ($profile, RUSTFLAGS='$($env:RUSTFLAGS)')..."
    & $cargoExe test @cargoArgs
    if ($LASTEXITCODE -ne 0) {
        throw "cargo test failed with exit code $LASTEXITCODE"
    }
}

Write-Host "Building dof_kernels ($profile)..."
& $cargoExe build @cargoArgs
if ($LASTEXITCODE -ne 0) {
    throw "cargo build failed with exit code $LASTEXITCODE"
}

$dllPath = Join-Path $targetDir "$profile\dof_kernels.dll"
if (-not (Test-Path $dllPath)) {
    throw "Expected output DLL missing: $dllPath"
}

$dllItem = Get-Item $dllPath
Write-Host "Built: $($dllItem.FullName) ($($dllItem.Length) bytes)"

if ($Deploy) {
    $destDirs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $buildBin = Join-Path $repoRoot 'tmp_logs\build\bin'
    if (Test-Path $buildBin) {
        Get-ChildItem -Path $buildBin -Directory | ForEach-Object {
            Get-ChildItem -Path $_.FullName -Directory | ForEach-Object {
                $targetSub = $_.FullName
                if (Test-Path (Join-Path $targetSub 'net8.0')) {
                    [void]$destDirs.Add((Join-Path $targetSub 'net8.0'))
                }
                elseif (Test-Path (Join-Path $targetSub 'net10.0')) {
                    [void]$destDirs.Add((Join-Path $targetSub 'net10.0'))
                }
                else {
                    [void]$destDirs.Add($targetSub)
                }
            }
        }
    }
    $godotBinRoot = Join-Path $repoRoot '.godot\mono\temp\bin'
    if (Test-Path $godotBinRoot) {
        Get-ChildItem -Path $godotBinRoot -Directory | ForEach-Object {
            [void]$destDirs.Add($_.FullName)
        }
    }

    if ($destDirs.Count -eq 0) {
        Write-Warning "No built .NET binary directories found yet under tmp_logs\build\bin or .godot\mono\temp\bin."
    }
    foreach ($dir in $destDirs) {
        Copy-Item -Path $dllPath -Destination (Join-Path $dir 'dof_kernels.dll') -Force
        Write-Host "Deployed dof_kernels.dll -> $dir"
    }
}
