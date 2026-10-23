# Odin ownership kernel experiment

Independent implementation of the exact C ABI v1 in [../ownership.h](../ownership.h).
This uses core Odin only, without Godot/gdext, Rust dependencies, retained pointers,
global data, campaign authority, or heap allocation in the kernel call. Our source
and recipe are GPL-3.0-only; see [../../../LICENSE](../../../LICENSE).

All validation and traversal contracts match the Rust reference: vertex and edge
budgets, declared capacities, null/alignment/address overflow/alias checks, CSR
monotonicity and bounds, zero/one water flags, ascending eligible starts, original
neighbor order, sentinel/owner-zero/water exclusions, exact BFS member order, and
zeroed required output tails. Inputs are fully checked before any output write.
Input aliases are permitted; used output ranges/counts may alias neither inputs
nor one another. Neighbor pointer may be null for zero edges. Status 1 and 2 leave
outputs untouched. The member output serves as the FIFO; no private queue allocates.

Status 3 reports an explicit internal queue/count invariant failure and may leave
partial scratch. This Odin function does not catch language panics, access
violations, aborts, or invalid foreign allocations. The host must discard all
output on any nonzero status and retain its managed fallback. Standard bounds
checks remain enabled. This library is trusted in-process code, not a sandbox.

From the repository root, build with the existing private compiler:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/GodotLanguages/kernels/ownership/odin/build.ps1
```

The default compiler is
`.fennara/languages/native/toolchains/odin/dist/odin.exe`; `-OdinExecutable` selects
an existing compiler elsewhere. The Windows x64 speed build emits
`.fennara/languages/ownership-odin/release/dof_graph_kernel.dll` and prints its
absolute directory and hash. `-OutputDirectory` selects another scratch folder.
It does not overwrite the Rust artifact, install an addon, modify global
environment variables, or load a live editor. The recipe can use a provided Unix
compiler and emit `libdof_graph_kernel.so`; only Windows is verified here.

The recipe first compiles and runs 22 direct ABI cases: exact BFS/zero tails,
malformed CSR/water, null/unaligned/overflowing pointers, input/output and output
aliases, count budgets, undersized buffers, and a null empty neighbor buffer.
Rejected calls must leave outputs untouched. These run independently of Godot.

The same managed host must validate every backend, without a production hook:

```powershell
dotnet run -c Release --project Tools/OwnershipKernelBench -- --self-test --native C:/absolute/ownership-odin/release
dotnet run -c Release --project Tools/OwnershipKernelBench -- --map 20260830 --iterations 51 --native C:/absolute/ownership-odin/release
dotnet run -c Release --project Tools/OwnershipKernelBench -- --synthetic --iterations 25 --native C:/absolute/ownership-odin/release
```

Timings must include native preflight/CSR validation, water packing, pinning,
managed result validation, and identical materialization. Cache creation is a
separate cost. Faster arithmetic or a single benchmark is not automatic authority
to activate a native backend. The campaign continues using its existing C# code.

## Verified Windows result, 2026-10-16

Compiler: `dev-2026-10-nightly:84bc3fc`, speed optimization, ordinary bounds checks.
The common C# host passed its four ordered fixtures and 300 randomized graphs,
checking exact labels, member order, offsets, materialized indices, and unchanged
inputs. Self-test plus actual-map timing finished with 3,934 checks; synthetic
timing finished with 303 checks. Each direct native call stayed native; fallback
is a benchmark failure.

All methods produced the same digest for each workload. Full materialization
includes input packing, pinning/ABI, native validation and traversal, managed
structural validation, and the same HashSet/rim/pore/state materialization.

| Workload | Samples | Existing C# p50 / p95, ms | Managed CSR p50 / p95, ms | Odin ABI p50 / p95, ms |
| --- | ---: | ---: | ---: | ---: |
| Actual map: 35,295 provinces, 184,904 directed edges | 51 | 2.729 / 5.710 | 3.722 / 5.512 | 3.931 / 4.993 |
| Small synthetic: 512 provinces | 25 | 0.026 / 0.032 | 0.041 / 0.050 | 0.049 / 0.062 |
| Stress synthetic: 300,000 provinces | 25 | 9.200 / 15.233 | 10.689 / 17.878 | 14.690 / 20.846 |

Actual-map kernel-only timing, still including water packing and native/result
validation, was 0.958 / 1.123 ms for managed CSR and 1.184 / 1.325 ms for Odin ABI.
CSR construction took 1.955 ms separately. Actual-map managed allocations per full
pass were 1,917,144 bytes for existing C# and 2,375,160 bytes for Odin through the
host. The .NET allocation counter does not measure native runtime startup.

This kernel is slower at the median on all three workloads. Keep the experiment
in tools; these results do not justify activating Odin in the campaign. The
actual-map p95 difference alone does not establish a simulation-wide gain.

Logs are private scratch under `.fennara/languages/ownership-odin/map-51.log` and
`synthetic-25.log`. The verified DLL SHA-256 is
`40A359D2E00890051FBEBE61CAC7D7994D1A3F6AC256CC199AF4FD01F3A21068`.
