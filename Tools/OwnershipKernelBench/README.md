# Ownership component benchmark

Standalone .NET 8 harness; it compiles the simulation sources and the isolated managed
experiment under `Tools/GodotLanguages/kernels/ownership/managed` without Godot. Run from
the repository root so the real map and campaign data are found.

Rust is reserved for rare hard cases with a measured benefit. This ownership experiment
did not demonstrate a median improvement over the existing C# BFS and is not activated
in the game. There is no production native Corridors hook or Rust runtime requirement.

```powershell
dotnet run -c Release --project Tools/OwnershipKernelBench -- --self-test
dotnet run -c Release --project Tools/OwnershipKernelBench -- --self-test --native C:/absolute/kernel-output
dotnet run -c Release --project Tools/OwnershipKernelBench -- --map 20260830 --iterations 51 --native C:/absolute/kernel-output
dotnet run -c Release --project Tools/OwnershipKernelBench -- --synthetic --iterations 25 --native C:/absolute/kernel-output
```

`--native` requires the fixed library in the supplied absolute directory to load and
remain enabled on every direct native call. Falling back to managed code fails the
benchmark, so a missing library cannot produce a native performance claim.

The reference is the original all-owner BFS from the working Corridors pore cache.
It preserves root order, neighbor order, BFS member order, first-state representatives,
and equal-sized pore selection. Every timed method constructs the same HashSet-based
components, rim sets, pore index and state index. New kernel timings include packing
the water buffer on every pass. A separate kernel metric includes water packing,
the ABI crossing, native execution and managed structural validation, but omits
HashSet/index materialization. CSR setup is printed separately. Digests and parity
checks run outside each timed section. Methods are interleaved to reduce order bias.
The result reports p50/p95 duration and managed allocated bytes per pass; native
allocator activity is not measured by the .NET allocation counter.
`--synthetic` repeats both stages on 512 and 300,000 vertex graphs with nonascending
adjacency, multiple owners, water and unclaimed vertices. These complement the real map;
they are not a replacement for its timing.

This measures rebuilding a cache after a border change. It does not measure a hot
cache hit or claim that a component-kernel gain speeds up the entire simulation.

The differential checks include deliberately nonascending adjacency, forty tied
components, water/unclaimed vertices, duplicate/invalid/sentinel edges, and 300
reproducibly generated graphs. They compare exact labels, offsets and ordered members,
then the fully materialized output. Inputs must remain unchanged.

For campaign checks, run separate processes under the desired experimental probe backend:

```powershell
$env:DOF_EXPERIMENTAL_KERNEL = 'off'
dotnet run -c Release --project Tools/OwnershipKernelBench -- --map 20260830 --iterations 5 --campaign-days 30 > managed-ownership.log
$env:DOF_EXPERIMENTAL_KERNEL = 'rust'
$env:DOF_EXPERIMENTAL_KERNEL_DIRECTORY = 'C:/absolute/kernel-output'
dotnet run -c Release --project Tools/OwnershipKernelBench -- --map 20260830 --iterations 5 --native C:/absolute/kernel-output --campaign-days 30 > rust-ownership.log
```

Compare the `CAMPAIGN` records from both logs. Each contains the completed day,
campaign fingerprint, RNG state, and ordered component digest. The harness also
checks that reads leave campaign/map state unchanged, restores a network snapshot
halfway through the run, and compares the two worlds as they continue. The environment
selects only the tools experiment's `OwnershipComponents.Read` cache probe. The campaign's
Corridors code remains C# in both processes. `--native` separately selects and verifies
direct native calls in this harness. `--campaign-days` is optional and may take longer
than a kernel microbenchmark. Snapshot data stays in memory.
The experimental selector accepts `rust`, `odin`, or `nim` for compatible ABI builds.
The native method labels are language-neutral; retain the supplied binary's build
directory and provenance when recording which implementation was measured. A selector
name is caller-provided metadata and does not authenticate the library.

The restored topology receives the same hydrology raster as the original world.
`CloneForOverrides` copies political and terrain arrays but omits derived lake access
and river data. Continuing that bare clone would remove lake supply bonuses on the
next logistics pass and create a false save-continuation failure. The harness verifies
lake access, river intensities and river-crossing count before continuing. Attaching a
topology increments `MapRevision`; an immediate snapshot comparison may report that
expected metadata difference even when the authoritative fingerprint matches.

On 2026-10-16, a temporary optional Corridors integration prototype also produced 31
identical daily fingerprint/RNG/component records through day 30 for managed and native
runs, with snapshot continuation after day 15. Those logs are historical prototype
evidence. The prototype was withdrawn after the benchmark showed no median advantage;
they do not describe Rust activation in the current shipping simulation.

Keep generated `bin/`, `obj/`, logs, and native binaries out of source publication.
