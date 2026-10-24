# Import-exposure batch: C# first, then Odin

Tools-only experiment, not a runtime backend. The game still calls
`Scripts/Simulation/EconomicExposure.cs`. No native DLL, native hook, new save field,
RNG use, networking or gameplay rule is introduced into the game.

Run from the repository root in PowerShell with an explicit portable Odin compiler:

```powershell
./Tools/ExposureKernelBench/run.ps1 -OdinExecutable 'C:/path/to/odin.exe' -World -Save 'C:/path/to/copy.bdfsave'
```

The recorded real-world run used `DOTNET_GCConserveMemory=9` and
`DOTNET_GCHeapHardLimit=0x80000000` for the benchmark process, with a 300-second
owned-process timeout. These are measurement-run settings, not new game defaults.

`-Save` is read-only and used with `-World`. Use a copy of a player's file. Output
goes to ignored `build/exposure-kernel-bench/`; no engine/configuration installation
is performed. This build script targets Windows x64; other platforms are untested.
The tested compiler is official Odin `dev-2026-10-nightly:84bc3fc`.
Release archive SHA-256:
`B6B2E426A8B6F147B74EC7E18BEF8DF27CF40FE16193159DF7CA09A0CC0C2011`.
Get the compiler only from the [official release](https://github.com/odin-lang/Odin/releases/tag/dev-2026-10).

## What is compared

- The original implementation, preserved independently in `Tools/Shared/ExposureReadOracle.cs`.
- One-pass production C# attribution, with source-order **double** sums, rounded
  once to float, matching the old LINQ float-selector `Sum`.
- Tools-only Odin hash aggregation through a C ABI. Timing includes route discovery,
  filtering/packing, array-pool rent/return, pinning, native validation and execution,
  output validation, supplier/row materialization and sorting. No managed fallback
  is silently timed as native.

Each sample reads every country in the same in-memory world. Twelve warm-up sweeps,
31 samples, rotating method order, two sweeps/sample on real worlds (12 on the
synthetic fixture). Report median/p95 milliseconds and current-thread managed
allocation bytes per complete sweep. These are not FPS, campaign-day throughput,
native memory usage or sealed hardware benchmarks.

605 managed regression assertions, 10 ABI boundary cases and 400 randomized native
differentials cover invalid route data, duplicate/modded goods, tie ordering,
double accumulation, float overflow, world fingerprint and RNG non-mutation.
Every real-world result row and supplier is compared bitwise with the old oracle.

## ABI v1

`dof_exposure_abi_version() -> uint32` returns 1. `dof_exposure_sum_v1` takes, in order:

```c
struct Route { int32_t good, seller; float volume; }; /* 12 bytes, alignment 4 */
int32_t dof_exposure_sum_v1(
    const Route *input, uint32_t count,
    Route *output, uint32_t output_capacity,
    double *sums, uint32_t sums_capacity,
    int32_t *slots, uint32_t slots_capacity,
    uint32_t *out_count);
```

All buffers are caller-owned, disjoint and alive throughout the synchronous call.
At most 262144 input rows; output/sums capacity at least count; slot count a power
of two, at least max(2*count,1), at most 524288. Valid input has nonnegative seller
IDs and finite positive volumes. Goods may be signed/mod-defined. Inputs are not
modified. No allocations or retained state in Odin. Output groups follow first
encounter; each source-order double sum is cast once to float (overflow to infinity
is intentionally preserved). C# performs the original stable row and supplier sort.

Status 0 = success, 1 = invalid shape/input/overlap, 2 = capacity error. Invalid
inputs are rejected before writes. Pointer checks cannot establish whether foreign
memory is mapped or alive; this is not a sandbox or protection against a corrupt
native caller. Default Odin bounds checks remain enabled. No fast-math flag.

## Decision on 24 October 2026

On the two actual campaign inputs Odin did **not** beat optimized C# median:
0.733 vs 0.635 ms (bookmark day 8), 2.818 vs 2.773 ms (saved day 1960).
It used fewer managed bytes, and won the synthetic fixture, but that is insufficient
for runtime activation. Keep the production C# improvement; retain Odin here as a
reproducible rejected pilot. See `docs/LANGUAGE_PERFORMANCE_AUDIT_2026_10_24_RU.md`
for workload limits and the whole-simulation profile.
