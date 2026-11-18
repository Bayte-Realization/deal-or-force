# Builds the Rust kernel (both CPU levels) and the C# bench, puts the two DLLs beside the
# bench, and runs it on a COPY of a save. Everything lands in tmp_logs (git-ignored).
#
#   powershell -ExecutionPolicy Bypass -File Tools\KernelBench\run.ps1 -Save C:\path\copy.bdfsave
#
# Rust is looked for in tmp_logs\rust (where the 17 November run installed it with
# --no-modify-path) and then on PATH. Without it the C# variants still build, but the run
# stops at the Rust ABI line: the native variants are part of the comparison, not optional.
# The run takes about half a minute to load and two minutes to measure; start it hidden and
# detached as AGENTS.md describes when the shell would be killed sooner.
param(
    [Parameter(Mandatory = $true)][string]$Save,
    [int]$Samples = 41,
    [int]$Warmup = 10
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
$localRust = Join-Path $root 'tmp_logs\rust'
if (Test-Path (Join-Path $localRust 'cargo\bin\cargo.exe')) {
    $env:RUSTUP_HOME = Join-Path $localRust 'rustup'
    $env:CARGO_HOME = Join-Path $localRust 'cargo'
    $env:PATH = (Join-Path $localRust 'cargo\bin') + ';' + $env:PATH
}
$manifest = 'Tools\KernelBench\native\Cargo.toml'
$env:RUSTFLAGS = ''
cargo build --release --manifest-path $manifest --target-dir tmp_logs\kb_rust\v1
$env:RUSTFLAGS = '-C target-cpu=x86-64-v3'
cargo build --release --manifest-path $manifest --target-dir tmp_logs\kb_rust\v3
$env:RUSTFLAGS = ''
dotnet build Tools\KernelBench\KernelBench.csproj -c Release --artifacts-path tmp_logs\kb_build
$bin = 'tmp_logs\kb_build\bin\KernelBench\release'
Copy-Item tmp_logs\kb_rust\v1\release\household_kernel.dll (Join-Path $bin 'household_kernel_v1.dll') -Force
Copy-Item tmp_logs\kb_rust\v3\release\household_kernel.dll (Join-Path $bin 'household_kernel_v3.dll') -Force
dotnet (Join-Path $bin 'KernelBench.dll') $Save --samples $Samples --warmup $Warmup
exit $LASTEXITCODE
