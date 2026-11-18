# SPDX-License-Identifier: GPL-3.0-only
# Builds every native contender LanguageBench can load, into one scratch directory outside
# res:// (tmp_logs is .gdignore'd). A contender whose toolchain is absent is reported as
# MISSING and skipped; nothing is downloaded or installed here.
#
#   lb_rust.dll       Rust, release, baseline x86-64 (what would ship)
#   lb_rust_v3.dll    the same source with -C target-cpu=x86-64-v3 (AVX2), labelled so
#   lb_zig.dll        Zig ReleaseFast, -mcpu baseline
#   lb_odin.dll       Odin -o:speed            (if odin.exe is found)
#   lb_nim.dll        Nim -d:release --cc:vcc  (if nim.exe is found)
#   ownership-rust\dof_graph_kernel.dll   the existing ownership kernel, built by its own recipe
#   ownership-odin\dof_graph_kernel.dll   the existing Odin ownership kernel (if odin.exe is found)
#
# Toolchains default to the installed Beta3 LanguageKit's private cache
# (C:\GodotEngineNet 4.8-Beta3\LanguageKit\.cache\toolchains, filled by
# workbench\install-portable-toolchains.ps1 from toolchains.windows.lock.json).
param(
    [string]$OutputDirectory,
    [string]$ToolchainDirectory = 'C:/GodotEngineNet 4.8-Beta3/LanguageKit/.cache/toolchains',
    [string]$OdinExecutable,
    [string]$NimExecutable,
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkDirectory = 'C:/Program Files (x86)/Windows Kits/10'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'tmp_logs/languages/langbench/native' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Report([string]$name, [string]$path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Write-Output "BUILT $name $path sha256=$hash"
}

# ---------------------------------------------------------------- MSVC linker for Rust/Nim
if (-not $VisualStudioDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $VisualStudioDirectory = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$msvc = Get-ChildItem -LiteralPath (Join-Path $VisualStudioDirectory 'VC/Tools/MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk = Get-ChildItem -LiteralPath (Join-Path $WindowsSdkDirectory 'Lib') -Directory |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'um/x64/kernel32.lib')) -and
                   (Test-Path -LiteralPath (Join-Path $_.FullName 'ucrt/x64/ucrt.lib')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$linker = Join-Path $msvc.FullName 'bin/Hostx64/x64/link.exe'

$names = @('PATH', 'RUSTUP_HOME', 'CARGO_HOME', 'LIB', 'INCLUDE', 'CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER', 'RUSTFLAGS')
$before = @{}
foreach ($name in $names) { $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:LIB = (Join-Path $msvc.FullName 'lib/x64') + ';' + (Join-Path $sdk.FullName 'um/x64') + ';' +
               (Join-Path $sdk.FullName 'ucrt/x64') + ';' + $env:LIB
    $include = Join-Path $WindowsSdkDirectory ('Include/' + $sdk.Name)
    $env:INCLUDE = (Join-Path $msvc.FullName 'include') + ';' + (Join-Path $include 'ucrt') + ';' +
                   (Join-Path $include 'um') + ';' + (Join-Path $include 'shared') + ';' + $env:INCLUDE
    $env:PATH = (Split-Path -Parent $linker) + ';' + $env:PATH

    # ------------------------------------------------------------ Rust
    $rustRoot = Join-Path $ToolchainDirectory 'rust'
    $cargo = Join-Path $rustRoot 'cargo/bin/cargo.exe'
    if (Test-Path -LiteralPath $cargo) {
        $env:RUSTUP_HOME = Join-Path $rustRoot 'rustup'
        $env:CARGO_HOME = Join-Path $rustRoot 'cargo'
        $env:PATH = (Join-Path $rustRoot 'cargo/bin') + ';' + $env:PATH
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = $linker
        Write-Output ("TOOLCHAIN rust " + (& $cargo --version) + " / " + (& (Join-Path $rustRoot 'cargo/bin/rustc.exe') --version))
        $manifest = Join-Path $PSScriptRoot 'native/rust/Cargo.toml'
        foreach ($variant in @(@{ Name = 'lb_rust'; Flags = '' }, @{ Name = 'lb_rust_v3'; Flags = '-C target-cpu=x86-64-v3' })) {
            $env:RUSTFLAGS = $variant.Flags
            $target = Join-Path $OutputDirectory ('target-' + $variant.Name)
            & $cargo build --release --offline --target x86_64-pc-windows-msvc --manifest-path $manifest --target-dir $target
            if ($LASTEXITCODE -ne 0) { throw "Rust build failed ($($variant.Name)): $LASTEXITCODE" }
            $dll = Join-Path $OutputDirectory ($variant.Name + '.dll')
            Copy-Item -LiteralPath (Join-Path $target 'x86_64-pc-windows-msvc/release/lb_rust.dll') -Destination $dll -Force
            Report $variant.Name $dll
        }
        $env:RUSTFLAGS = ''
        # The existing ownership kernel, by its own recipe (it runs its release tests first).
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'Tools/GodotLanguages/kernels/ownership/build.ps1') `
            -OutputDirectory (Join-Path $OutputDirectory 'ownership-rust-build') -CargoExecutable $cargo `
            -CargoDirectory (Join-Path $rustRoot 'cargo') -RustupDirectory (Join-Path $rustRoot 'rustup') `
            -VisualStudioDirectory $VisualStudioDirectory
        if ($LASTEXITCODE -ne 0) { throw "Ownership kernel (Rust) recipe failed: $LASTEXITCODE" }
        $ownership = Join-Path $OutputDirectory 'ownership-rust'
        New-Item -ItemType Directory -Force -Path $ownership | Out-Null
        Copy-Item -LiteralPath (Join-Path $OutputDirectory 'ownership-rust-build/target/x86_64-pc-windows-msvc/release/dof_graph_kernel.dll') -Destination $ownership -Force
        Report 'ownership-rust' (Join-Path $ownership 'dof_graph_kernel.dll')
    } else { Write-Output "MISSING rust (no $cargo)" }

    # ------------------------------------------------------------ Zig
    $zig = Get-ChildItem -LiteralPath $ToolchainDirectory -Recurse -Filter zig.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'zig-0\.17\.0' } | Select-Object -First 1
    if ($zig) {
        Write-Output ("TOOLCHAIN zig " + (& $zig.FullName version))
        $dll = Join-Path $OutputDirectory 'lb_zig.dll'
        $cache = Join-Path $OutputDirectory 'zig-cache'
        & $zig.FullName build-lib -dynamic -O ReleaseFast -mcpu baseline -target x86_64-windows-gnu `
            --cache-dir $cache --global-cache-dir $cache "-femit-bin=$dll" (Join-Path $PSScriptRoot 'native/zig/kernels.zig')
        if ($LASTEXITCODE -ne 0) { throw "Zig build failed: $LASTEXITCODE" }
        Report 'lb_zig' $dll
    } else { Write-Output 'MISSING zig 0.17.0' }

    # ------------------------------------------------------------ Odin
    if (-not $OdinExecutable) { $OdinExecutable = (Get-Command odin -ErrorAction SilentlyContinue).Source }
    if ($OdinExecutable -and (Test-Path -LiteralPath $OdinExecutable)) {
        Write-Output ("TOOLCHAIN odin " + (& $OdinExecutable version))
        $dll = Join-Path $OutputDirectory 'lb_odin.dll'
        & $OdinExecutable build (Join-Path $PSScriptRoot 'native/odin') -build-mode:dll -target:windows_amd64 -o:speed -microarch:x86-64 "-out:$dll"
        if ($LASTEXITCODE -ne 0) { throw "Odin build failed: $LASTEXITCODE" }
        Report 'lb_odin' $dll
        $ownership = Join-Path $OutputDirectory 'ownership-odin'
        New-Item -ItemType Directory -Force -Path $ownership | Out-Null
        & $OdinExecutable build (Join-Path $repo 'Tools/GodotLanguages/kernels/ownership/odin/src') -build-mode:dll -target:windows_amd64 -o:speed -microarch:x86-64 "-out:$(Join-Path $ownership 'dof_graph_kernel.dll')"
        if ($LASTEXITCODE -ne 0) { throw "Odin ownership build failed: $LASTEXITCODE" }
        Report 'ownership-odin' (Join-Path $ownership 'dof_graph_kernel.dll')
    } else { Write-Output 'MISSING odin (no odin.exe on PATH or -OdinExecutable; not named in toolchains.windows.lock.json)' }

    # ------------------------------------------------------------ Nim
    if (-not $NimExecutable) { $NimExecutable = (Get-Command nim -ErrorAction SilentlyContinue).Source }
    if ($NimExecutable -and (Test-Path -LiteralPath $NimExecutable)) {
        Write-Output ("TOOLCHAIN nim " + ((& $NimExecutable --version) | Select-Object -First 1))
        $dll = Join-Path $OutputDirectory 'lb_nim.dll'
        & $NimExecutable c --app:lib -d:release -d:danger:off --opt:speed --cc:vcc --mm:none --threads:off `
            "--nimcache:$(Join-Path $OutputDirectory 'nim-cache')" "--out:$dll" (Join-Path $PSScriptRoot 'native/nim/kernels.nim')
        if ($LASTEXITCODE -ne 0) { throw "Nim build failed: $LASTEXITCODE" }
        Report 'lb_nim' $dll
    } else { Write-Output 'MISSING nim (no nim.exe on PATH or -NimExecutable; not named in toolchains.windows.lock.json)' }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
}
Write-Output "NATIVE_DIRECTORY=$OutputDirectory"
