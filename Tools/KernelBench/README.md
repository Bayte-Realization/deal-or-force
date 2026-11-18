# KernelBench: data layout and native code on the household review

Tools-only experiment. The game does not reference this project, its packages or its
Rust library. Results and the decision: `docs/DATA_LAYOUT_AND_NATIVE_BENCH_2026_11_17_RU.md`.

## What it measures

The group walk of `HouseholdEconomy.Review` (Scripts/Simulation/HouseholdEconomy.cs): every
population group of every region, once a market week - labour and employment sums, the
employment, wealth, living-standard and radicalism update of each group, and the region's
living standard. On the 107-year save that is 257,946 groups in 1,915 regions.

Variants, all on the same loaded save:

| | Layout | Code |
|---|---|---|
| a | the game's `List<PopulationGroup>` objects | the game's expressions, serial and as the game schedules them |
| b | structure of arrays, `Span` | scalar C#, serial and `DeterministicParallel` |
| c | the same arrays | `Vector256` C#, serial and `DeterministicParallel` |
| d | Friflo.Engine.ECS 3.6.0, entity per group | serial |
| e | Arch 2.1.0, entity per group | serial |
| f | the same arrays, pinned | Rust 1.100 behind a C ABI, one call per week, SSE2 / AVX2, 1 or N threads |

Plus the bridge: packing the game's objects into arrays and writing the results back, and
two end-to-end runs (pack + kernel + unpack).

Every sample starts from the same bytes. Every result is hashed (FNV-1a over the bit
patterns of every written field) and compared with the game's code; a variant that differs
by one bit is reported `DIFFERS` and the process exits 1. At the end the game's real
`HouseholdEconomy.Review` runs on the world and every field the loop writes is compared to
the reference bit for bit (`ORACLE`).

How floating-point order is kept: no fused multiply-add anywhere (C# keeps `*` and `+`
separate, Rust never contracts without fast-math); clamp is `v < lo ? lo : v > hi ? hi : v`
in all languages, as two `ConditionalSelect`s in SIMD (not `Min`/`Max`, which treat NaN
and -0 differently); `Math.Max(0, x)` is `x <= 0 ? +0 : x`; the two `double` sums are added
in group order, never vectorised or split across threads; parallelism is by region, and a
region's groups are never split.

## Running

Read-only on the save. From the repository root (needs the `game/` data):

```powershell
dotnet build Tools/KernelBench/KernelBench.csproj -c Release --artifacts-path tmp_logs/kb_build
# Rust (optional for variants f): rustup stable, minimal profile; no crates are used
cargo build --release --manifest-path Tools/KernelBench/native/Cargo.toml --target-dir tmp_logs/kb_rust/v1
$env:RUSTFLAGS='-C target-cpu=x86-64-v3'; cargo build --release --manifest-path Tools/KernelBench/native/Cargo.toml --target-dir tmp_logs/kb_rust/v3
copy tmp_logs/kb_rust/v1/release/household_kernel.dll tmp_logs/kb_build/bin/KernelBench/release/household_kernel_v1.dll
copy tmp_logs/kb_rust/v3/release/household_kernel.dll tmp_logs/kb_build/bin/KernelBench/release/household_kernel_v3.dll
dotnet tmp_logs/kb_build/bin/KernelBench/release/KernelBench.dll <copy-of-save.bdfsave> --samples 31 --warmup 10
```

Loading the save takes about half a minute; the run about two minutes. Start it hidden and
detached (see AGENTS.md) rather than in a shell that dies at thirty seconds.

## Licences

Friflo.Engine.ECS is MIT, but it depends on Friflo.Json.Fliox, which is **LGPL-3.0-only**.
Fine for a benchmark nobody ships; not cleared for the game. Arch is Apache-2.0 and its
dependencies are MIT or Apache-2.0. The Rust library uses no crates; its standard library
is MIT/Apache-2.0. See THIRD_PARTY_NOTICES.md.
