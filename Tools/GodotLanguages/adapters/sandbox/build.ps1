[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    # MinGW-w64 bin directory (w64devkit 2.10.1 in toolchains.windows.lock.json) for the host DLL.
    [Parameter(Mandatory)][string]$CompilerBin,
    # Zig 0.17.0 cross-compiles the SafeGDScript compiler guest (gdscript.elf) to RISC-V;
    # upstream's program/cpp/cmake supports `zig c++` directly.
    [Parameter(Mandatory)][string]$ZigExecutable,
    [string]$CMakeExecutable = 'cmake',
    [string]$NinjaExecutable = 'ninja',
    [string]$PythonExecutable = 'python',
    [ValidateRange(1,16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
# main after v0.60: refuses indirect calls to banned methods/properties, checks connect(),
# fixes coroutine ownership of borrowed Variants. Built entirely from this source: the host
# DLL, the gdscript.elf guest and the addon files. No prebuilt release archive is used.
$revision = '4f9c73d683f6ae734c87773d01938448ab3c3d50'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne $revision) {
    throw "Use the pinned godot-sandbox source revision $revision."
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $source -or $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Output must be outside the upstream source directory.'
}
$compiler = (Resolve-Path -LiteralPath $CompilerBin).Path
if (-not (Test-Path -LiteralPath (Join-Path $compiler 'g++.exe'))) { throw 'A MinGW-w64 C++ compiler is required.' }
$zig = (Get-Command $ZigExecutable -ErrorAction Stop).Source
$zigVersion = (& $zig version | Out-String).Trim()
if ($zigVersion -ne '0.17.0') { throw "The RISC-V guest build is tested with Zig 0.17.0, found $zigVersion." }
$cmake = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$ninja = (Get-Command $NinjaExecutable -ErrorAction Stop).Source
$python = (Get-Command $PythonExecutable -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $output | Out-Null
& git -C $source submodule update --init --depth 1 ext/godot-cpp ext/libriscv
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize pinned sandbox dependencies.' }
$patch = Join-Path $PSScriptRoot 'patches/0001-godot48-language-registration-and-property-api.patch'
& git -C $source apply --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    & git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox compatibility patch could not be applied.' }
} else {
    & git -C $source apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patch does not match this checkout.' }
}
# With core.symlinks=false Git writes the target path into a regular file.
# Materialize only this known, in-repository header used by the native build.
$header = Join-Path $source 'src/syscalls.h'
if ((Get-Content -LiteralPath $header -Raw).Trim() -eq '../program/cpp/docker/api/syscalls.h') {
    Copy-Item -LiteralPath (Join-Path $source 'program/cpp/docker/api/syscalls.h') -Destination $header -Force
}

# 1. Host DLL (Windows x86-64, single precision, default "restricted" compiler policy:
#    a restricted Sandbox compiles its scripts with gdscript.elf inside the sandbox).
$oldPath = $env:PATH
try {
    $env:PATH = $compiler + ';' + $oldPath
    & scons -C $source platform=windows target=template_release arch=x86_64 "-j$Jobs" *> (Join-Path $output 'native-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Sandbox build failed; see $output/native-build.log" }
} finally { $env:PATH = $oldPath }
$library = Join-Path $source 'bin/addons/godot_sandbox/bin/libgodot_riscv.windows.template_release.x86_64.dll'
if (-not (Test-Path -LiteralPath $library)) { throw 'Sandbox build did not produce the Windows x86-64 DLL.' }

# 2. gdscript.elf, the same CMake project as upstream's build_gdscript_elf.yml (MinSizeRel,
#    single precision), with zig c++ instead of riscv64 GCC 14. Zig turns STRIPPED's
#    -Wl,--strip-debug into a full strip, and without -g at link time it drops .symtab too;
#    the Sandbox finds `compile` only through .symtab ("Function not found: compile").
#    So: STRIPPED=OFF, -g at link time, then strip-debug.py removes only the .debug_* sections.
$elfBuild = Join-Path $output 'gdscript-elf-build'
New-Item -ItemType Directory -Force -Path $elfBuild | Out-Null
foreach ($tool in @(@('zig-cc.cmd', 'cc -target riscv64-linux-musl'), @('zig-cxx.cmd', 'c++ -target riscv64-linux-musl'),
                    @('zig-ar.cmd', 'ar'), @('zig-ranlib.cmd', 'ranlib'))) {
    [IO.File]::WriteAllText((Join-Path $elfBuild $tool[0]), "@`"$zig`" $($tool[1]) %*`r`n", [Text.Encoding]::ASCII)
}
# The CMake project downloads this header on configure; fetch it first and check the
# digest GitHub publishes for the godot-sandbox-programs v0.1 release asset.
$api = Join-Path $elfBuild 'generated_api.hpp'
$apiExpected = 'b4e0a6f7d951468e9c7f21769864921150a08599a4f19709c30296dfe491655c'
if (-not (Test-Path -LiteralPath $api)) {
    Invoke-WebRequest 'https://github.com/libriscv/godot-sandbox-programs/releases/download/v0.1/generated_api.hpp' -OutFile ($api + '.partial')
    Move-Item -LiteralPath ($api + '.partial') -Destination $api
}
if ((Get-FileHash -LiteralPath $api).Hash.ToLowerInvariant() -ne $apiExpected) { throw 'generated_api.hpp checksum mismatch.' }
$saved = @{ CC = $env:CC; CXX = $env:CXX }
try {
    $env:CC = Join-Path $elfBuild 'zig-cc.cmd'; $env:CXX = Join-Path $elfBuild 'zig-cxx.cmd'
    # tests/toolchain.cmake is a symlink Git wrote as text; use the file it points to.
    & $cmake -G Ninja -B $elfBuild -S (Join-Path $source 'program/cpp/gdscript_compiler') "-DCMAKE_MAKE_PROGRAM=$ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $source 'program/cpp/cmake/toolchain.cmake')" -DCMAKE_BUILD_TYPE=MinSizeRel `
        -DSTRIPPED=OFF -DDOUBLE_PRECISION=OFF '-DCMAKE_EXE_LINKER_FLAGS=-g' "-DCMAKE_AR=$(Join-Path $elfBuild 'zig-ar.cmd')" `
        "-DCMAKE_RANLIB=$(Join-Path $elfBuild 'zig-ranlib.cmd')" *> (Join-Path $output 'gdscript-elf-configure.log')
    if ($LASTEXITCODE -ne 0) { throw "gdscript.elf configure failed; see $output/gdscript-elf-configure.log" }
    & $cmake --build $elfBuild --target gdscript --parallel $Jobs *> (Join-Path $output 'gdscript-elf-build.log')
    if ($LASTEXITCODE -ne 0) { throw "gdscript.elf build failed; see $output/gdscript-elf-build.log" }
} finally { $env:CC = $saved.CC; $env:CXX = $saved.CXX }
$elf = Join-Path $elfBuild 'gdscript.elf'
Remove-Item -LiteralPath $elf -Force -ErrorAction SilentlyContinue
& $python -I (Join-Path $PSScriptRoot 'strip-debug.py') (Join-Path $elfBuild 'gdscript') $elf compile
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $elf)) { throw 'gdscript.elf debug strip or symbol check failed.' }
$magic = [IO.File]::ReadAllBytes($elf)[0..19]
if ($magic[0] -ne 0x7F -or $magic[1] -ne 0x45 -or $magic[4] -ne 2 -or $magic[18] -ne 0xF3) { throw 'gdscript is not a 64-bit RISC-V ELF.' }

# 3. Addon, assembled like upstream's release.yml: the tracked bin/addons/godot_sandbox
#    files, the built DLL, gdscript.elf, LICENSE and THIRDPARTY.txt inside the addon.
$project = Join-Path $output 'project'
if (Test-Path -LiteralPath (Join-Path $project 'project.godot')) { throw 'Existing probe project preserved; choose a fresh output directory.' }
$addon = Join-Path $project 'addons/godot_sandbox'
foreach ($tracked in @(& git -C $source ls-files 'bin/addons/godot_sandbox')) {
    $relative = $tracked.Substring('bin/addons/godot_sandbox/'.Length)
    if ($relative -match '^bin/.*\.(framework/|plist$)') { continue }
    $target = Join-Path $addon $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $tracked) -Destination $target
}
Copy-Item -LiteralPath $library -Destination (Join-Path $addon 'bin/libgodot_riscv.windows.template_release.x86_64.dll')
Copy-Item -LiteralPath $elf -Destination (Join-Path $addon 'gdscript.elf')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE'), (Join-Path $source 'THIRDPARTY.txt') -Destination $addon
Copy-Item -Path (Join-Path $PSScriptRoot 'template/*') -Destination $project -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $project '.godot') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'template/extension_list.cfg.in') -Destination (Join-Path $project '.godot/extension_list.cfg') -Force
[pscustomobject]@{
    projectRoot = $project
    addonRoot = $addon
    descriptor = 'addons/godot_sandbox/bin/godot-riscv.gdextension'
    probeScript = 'res://probe.gd'
    sourceRevision = $revision
    librarySha256 = (Get-FileHash -LiteralPath $library).Hash.ToLowerInvariant()
    gdscriptElfSha256 = (Get-FileHash -LiteralPath $elf).Hash.ToLowerInvariant()
    generatedApiSha256 = $apiExpected
}
