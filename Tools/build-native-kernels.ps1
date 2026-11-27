# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param(
    [ValidateSet('all', 'win64', 'android', 'arm64', 'x86_64')]
    [string[]]$Targets = @('all'),

    [switch]$SkipTest,
    [switch]$NoDeploy,

    [string]$AndroidRepo = '',
    [string]$NdkDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$crateDir = Join-Path $repoRoot 'native\dof_kernels'
$manifestPath = Join-Path $crateDir 'Cargo.toml'
$targetRoot = Join-Path $repoRoot 'tmp_logs\rust_target'
$stageRoot = Join-Path $repoRoot 'tmp_logs\native_kernels'

if ([string]::IsNullOrWhiteSpace($AndroidRepo)) {
    $AndroidRepo = Join-Path (Split-Path $repoRoot -Parent) 'deal-or-force-android'
}

$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if (Test-Path $cargoBin) {
    $env:PATH = "$cargoBin;$env:PATH"
}

$cargoExe = Get-Command cargo -ErrorAction SilentlyContinue
$rustupExe = Get-Command rustup -ErrorAction SilentlyContinue
if (-not $cargoExe -or -not $rustupExe) {
    throw "Rust toolchain not found. Expected cargo/rustup in '$cargoBin' or PATH."
}

$wantWin64 = ($Targets -contains 'all') -or ($Targets -contains 'win64')
$wantArm64 = ($Targets -contains 'all') -or ($Targets -contains 'android') -or ($Targets -contains 'arm64')
$wantX86_64 = ($Targets -contains 'all') -or ($Targets -contains 'android') -or ($Targets -contains 'x86_64')

$installedTargets = @(& rustup target list --installed)
$requiredRustTargets = @()
if ($wantWin64) { $requiredRustTargets += 'x86_64-pc-windows-msvc' }
if ($wantArm64) { $requiredRustTargets += 'aarch64-linux-android' }
if ($wantX86_64) { $requiredRustTargets += 'x86_64-linux-android' }
foreach ($t in $requiredRustTargets) {
    if ($installedTargets -notcontains $t) {
        Write-Host "== Adding Rust target $t ==" -ForegroundColor Cyan
        & rustup target add $t
        if ($LASTEXITCODE -ne 0) { throw "rustup target add $t failed (exit $LASTEXITCODE)." }
    }
}

$env:CARGO_TARGET_DIR = $targetRoot
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

function Resolve-AndroidNdkBin {
    param([string]$ExplicitNdkDir)

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ExplicitNdkDir)) {
        $candidates.Add($ExplicitNdkDir)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_NDK_HOME)) {
        $candidates.Add($env:ANDROID_NDK_HOME)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_NDK_ROOT)) {
        $candidates.Add($env:ANDROID_NDK_ROOT)
    }
    $sdkNdkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'
    if (Test-Path $sdkNdkRoot) {
        Get-ChildItem $sdkNdkRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            $candidates.Add($_.FullName)
        }
    }

    foreach ($ndk in $candidates) {
        if (-not (Test-Path $ndk)) { continue }
        $binDir = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin'
        if (Test-Path (Join-Path $binDir 'aarch64-linux-android24-clang.cmd')) {
            return $binDir
        }
    }

    throw 'Android NDK r24+ LLVM toolchain not found (checked ExplicitNdkDir, ANDROID_NDK_HOME/ROOT, and %LOCALAPPDATA%\Android\Sdk\ndk).'
}

if (-not $SkipTest) {
    Write-Host '== Running native Rust unit tests (cargo test --release) ==' -ForegroundColor Cyan
    & cargo test --release --manifest-path $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "cargo test --release failed (exit $LASTEXITCODE)." }
}

$builtArtifacts = New-Object System.Collections.Generic.List[object]

if ($wantWin64) {
    Write-Host '== Building Win64 x86_64-pc-windows-msvc Release dof_kernels.dll ==' -ForegroundColor Cyan
    $prevRustFlags = $env:RUSTFLAGS
    try {
        # Baseline x86-64 SSE2 with runtime AVX2 feature dispatch avoids illegal instructions on older CPUs.
        $env:RUSTFLAGS = ''
        & cargo build --release --manifest-path $manifestPath --target x86_64-pc-windows-msvc
        if ($LASTEXITCODE -ne 0) { throw "Win64 cargo build failed (exit $LASTEXITCODE)." }
    }
    finally {
        $env:RUSTFLAGS = $prevRustFlags
    }

    $srcDll = Join-Path $targetRoot 'x86_64-pc-windows-msvc\release\dof_kernels.dll'
    if (-not (Test-Path $srcDll)) { throw "Missing built DLL: $srcDll" }

    $stageWinDir = Join-Path $stageRoot 'win-x64'
    New-Item -ItemType Directory -Force -Path $stageWinDir | Out-Null
    $stageDll = Join-Path $stageWinDir 'dof_kernels.dll'
    Copy-Item -Force $srcDll $stageDll
    $builtArtifacts.Add([PSCustomObject]@{ Target = 'win-x64'; Path = $stageDll; Bytes = (Get-Item $stageDll).Length })

    if (-not $NoDeploy) {
        $deployDirs = New-Object System.Collections.Generic.List[string]
        foreach ($cfg in @('Debug', 'Release')) {
            $monoDir = Join-Path $repoRoot ".godot\mono\temp\bin\$cfg"
            if (Test-Path $monoDir) { $deployDirs.Add($monoDir) }
            foreach ($toolName in @('FormableTests', 'KernelBench', 'SimBench')) {
                $toolBinCfg = Join-Path $repoRoot "Tools\$toolName\bin\$cfg"
                if (Test-Path $toolBinCfg) {
                    Get-ChildItem $toolBinCfg -Directory -Filter 'net*' -ErrorAction SilentlyContinue | ForEach-Object {
                        $deployDirs.Add($_.FullName)
                    }
                }
            }
        }
        foreach ($isoBin in @('tmp_logs\kernelbench_bin', 'tmp_logs\formable_bin')) {
            $isoDir = Join-Path $repoRoot $isoBin
            if (Test-Path $isoDir) { $deployDirs.Add($isoDir) }
        }
        $buildRoot = Join-Path $repoRoot 'build'
        if (Test-Path $buildRoot) {
            if (Test-Path (Join-Path $buildRoot 'DealOrForce.dll')) { $deployDirs.Add($buildRoot) }
            Get-ChildItem $buildRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                if (Test-Path (Join-Path $_.FullName 'DealOrForce.dll')) { $deployDirs.Add($_.FullName) }
                Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    if (Test-Path (Join-Path $_.FullName 'DealOrForce.dll')) { $deployDirs.Add($_.FullName) }
                }
            }
        }
        $tmpBuildBin = Join-Path $repoRoot 'tmp_logs\build\bin'
        New-Item -ItemType Directory -Force -Path $tmpBuildBin | Out-Null
        $deployDirs.Add($tmpBuildBin)

        foreach ($dir in ($deployDirs | Select-Object -Unique)) {
            $dest = Join-Path $dir 'dof_kernels.dll'
            try {
                Copy-Item -Force $stageDll $dest
                Write-Host "  deployed -> $dest" -ForegroundColor DarkGray
            }
            catch {
                Write-Warning "Could not copy dof_kernels.dll to '$dest' (may be locked by running process): $_"
            }
        }
    }
}

if ($wantArm64 -or $wantX86_64) {
    $ndkBin = Resolve-AndroidNdkBin -ExplicitNdkDir $NdkDir
    $llvmAr = Join-Path $ndkBin 'llvm-ar.exe'
    $llvmStrip = Join-Path $ndkBin 'llvm-strip.exe'
    $llvmReadObj = Join-Path $ndkBin 'llvm-readobj.exe'
    Write-Host "Using Android NDK LLVM bin: $ndkBin" -ForegroundColor DarkCyan

    $androidJobs = @()
    if ($wantArm64) {
        $androidJobs += @{
            Abi = 'arm64-v8a'
            RustTarget = 'aarch64-linux-android'
            Clang = Join-Path $ndkBin 'aarch64-linux-android24-clang.cmd'
            EnvSuffix = 'AARCH64_LINUX_ANDROID'
            CcSuffix = 'aarch64_linux_android'
            ExpectedMachine = 'EM_AARCH64'
        }
    }
    if ($wantX86_64) {
        $androidJobs += @{
            Abi = 'x86_64'
            RustTarget = 'x86_64-linux-android'
            Clang = Join-Path $ndkBin 'x86_64-linux-android24-clang.cmd'
            EnvSuffix = 'X86_64_LINUX_ANDROID'
            CcSuffix = 'x86_64_linux_android'
            ExpectedMachine = 'EM_X86_64'
        }
    }

    foreach ($job in $androidJobs) {
        Write-Host "== Building Android $($job.Abi) ($($job.RustTarget)) Release libdof_kernels.so ==" -ForegroundColor Cyan
        $linkerVar = "CARGO_TARGET_$($job.EnvSuffix)_LINKER"
        $arVar = "CARGO_TARGET_$($job.EnvSuffix)_AR"
        $flagsVar = "CARGO_TARGET_$($job.EnvSuffix)_RUSTFLAGS"
        $ccVar = "CC_$($job.CcSuffix)"

        $savedEnv = @{
            Linker = [Environment]::GetEnvironmentVariable($linkerVar)
            Ar = [Environment]::GetEnvironmentVariable($arVar)
            Flags = [Environment]::GetEnvironmentVariable($flagsVar)
            Cc = [Environment]::GetEnvironmentVariable($ccVar)
            RustFlags = $env:RUSTFLAGS
        }

        try {
            $env:RUSTFLAGS = ''
            [Environment]::SetEnvironmentVariable($linkerVar, $job.Clang)
            [Environment]::SetEnvironmentVariable($arVar, $llvmAr)
            [Environment]::SetEnvironmentVariable($ccVar, $job.Clang)
            # Enforce Android 15/16 16KB ELF PT_LOAD alignment + explicit DT_SONAME.
            [Environment]::SetEnvironmentVariable($flagsVar, '-C link-arg=-Wl,-z,max-page-size=16384 -C link-arg=-Wl,-soname,libdof_kernels.so')

            & cargo build --release --manifest-path $manifestPath --target $job.RustTarget
            if ($LASTEXITCODE -ne 0) { throw "Android $($job.Abi) cargo build failed (exit $LASTEXITCODE)." }
        }
        finally {
            [Environment]::SetEnvironmentVariable($linkerVar, $savedEnv.Linker)
            [Environment]::SetEnvironmentVariable($arVar, $savedEnv.Ar)
            [Environment]::SetEnvironmentVariable($flagsVar, $savedEnv.Flags)
            [Environment]::SetEnvironmentVariable($ccVar, $savedEnv.Cc)
            $env:RUSTFLAGS = $savedEnv.RustFlags
        }

        $rawSo = Join-Path $targetRoot "$($job.RustTarget)\release\libdof_kernels.so"
        if (-not (Test-Path $rawSo)) { throw "Missing built Android SO: $rawSo" }

        $stageAbiDir = Join-Path $stageRoot "android\$($job.Abi)"
        New-Item -ItemType Directory -Force -Path $stageAbiDir | Out-Null
        $stageSo = Join-Path $stageAbiDir 'libdof_kernels.so'
        Copy-Item -Force $rawSo $stageSo

        if (Test-Path $llvmStrip) {
            & $llvmStrip --strip-unneeded $stageSo
            if ($LASTEXITCODE -ne 0) { throw "llvm-strip failed on $stageSo (exit $LASTEXITCODE)." }
        }

        if (Test-Path $llvmReadObj) {
            $elfInfo = (& $llvmReadObj -h -l $stageSo | Out-String)
            if ($elfInfo -notmatch $job.ExpectedMachine) {
                throw "ELF machine mismatch for '$stageSo' (expected $($job.ExpectedMachine))."
            }
            if ($elfInfo -notmatch 'Alignment:\s*16384') {
                throw "ELF PT_LOAD 16KB alignment missing in '$stageSo'."
            }
        }

        $builtArtifacts.Add([PSCustomObject]@{ Target = "android-$($job.Abi)"; Path = $stageSo; Bytes = (Get-Item $stageSo).Length })

        if (-not $NoDeploy -and (Test-Path $AndroidRepo)) {
            $androidAbiDir = Join-Path $AndroidRepo "native_libs\$($job.Abi)"
            New-Item -ItemType Directory -Force -Path $androidAbiDir | Out-Null
            $androidDest = Join-Path $androidAbiDir 'libdof_kernels.so'
            Copy-Item -Force $stageSo $androidDest
            Write-Host "  deployed -> $androidDest" -ForegroundColor DarkGray
        }
    }
}

Write-Host "`nNative kernel build summary:" -ForegroundColor Green
$builtArtifacts | Format-Table -AutoSize
