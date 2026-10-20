# Ownership graph kernel experiment

This dependency-free Rust `cdylib` labels all same-owner land floods in a CSR graph
in one call. It has no Godot bindings, campaign state, retained pointers, global
cache, or output allocation. The caller's member output doubles as the BFS queue.
This is an optional computation backend, not a mandatory game runtime or evidence
that rewriting the game improves performance. The managed implementation remains
the fallback and differential reference. The prototype game hook was withdrawn:
these sources are tools-only and are not compiled into the shipping simulation.
The equivalent [Odin implementation](odin/README.md) shares this ABI; the C# host
lives in [managed](managed) and the common differential/timing harness is
`Tools/OwnershipKernelBench` in the game repository. Both native implementations
matched the reference but failed to beat the existing C# median. Rust remains
reserved for exceptional hard cases with measured benefit.

Our source, header, recipe, and tests use GPL-3.0-only, like the kit's own code;
the full text is [../../LICENSE](../../LICENSE). There are no third-party crate
dependencies. Cargo.lock contains only this package. Normal builds/tests use
`--locked --offline`; the recipe does not download or install a toolchain.

## ABI v1

[ownership.h](ownership.h) is the exact C declaration. Exports use C calling
convention (`Cdecl` in .NET), fixed-width primitives, and ABI version `1`:

```c
uint32_t dof_graph_abi_version(void);
int32_t dof_owner_components_v1(
    const int32_t *offsets, uint32_t offsets_count,
    const int32_t *neighbors, uint32_t neighbor_count,
    const uint16_t *owners, uint32_t vertex_count,
    const uint8_t *water, uint32_t water_count,
    int32_t *labels, uint32_t labels_capacity,
    int32_t *member_order, uint32_t member_capacity,
    int32_t *component_offsets, uint32_t component_offsets_capacity,
    int32_t *out_component_count, int32_t *out_member_count);
```

`V=vertex_count` includes sentinel vertex zero. `1 <= V <= 1,000,000`,
`0 <= E=neighbor_count <= 16,000,000`, `offsets_count=V+1`, and `water_count=V`.
Offsets start at zero, are nonnegative and monotone, and end at E. Each neighbor
is in `[0,V)`; duplicates, self edges, and asymmetric directed input are accepted.
Every water value, including the sentinel's, is either zero or one. Owners are
16-bit map indices and may be sparse, including 65535.

The caller provides labels/member buffers of at least V elements, component
offsets of at least V+1, and two separate writable counts. The function preflights
nonnull/aligned addresses, address arithmetic overflow, and overlap between every
used output region/count and all inputs/other outputs. Inputs may overlap each
other. Neighbors may be null only when E=0. Other pointers must be nonnull even
for a graph with no eligible land. Extra capacity beyond the required lengths is
neither accessed nor included in overlap checks.

Labels for sentinel zero, water, and owner zero are zero. Starts visit vertices
`1..V` in ascending order. Each unseen eligible start opens component `1..C` and
floods same-owner non-water outgoing neighbors in exactly their CSR row order.
Each vertex enters the FIFO once. For asymmetric input these are ordered floods,
not strongly or weakly connected components. Symmetric input gives the game's
ordinary ownership components.

`member_order[0..M]` concatenates component BFS traversal order. Offsets start at
zero; component k occupies `member_order[offsets[k-1]..offsets[k]]` for `1<=k<=C`.
The function writes counts C and M; unused required output entries are zeroed.
No capital sorting, claim/title checks, corridor pricing, or state changes occur.
Preserving member order matters: rebuilding a HashSet in another order can change
the subsequent corridor search's tie breaking.

| Status | Meaning | Outputs |
| --- | --- | --- |
| 0 | Success | Complete, as above |
| 1 | Invalid counts, pointers, aliases, CSR, or water | Untouched |
| 2 | Insufficient declared output capacity | Untouched |
| 3 | Caught Rust unwind panic | Discard all output; it may be partial |

Count bounds are checked first, then capacities, pointer shapes/aliases, and CSR
contents. If several conditions are invalid, this order determines the status.
Build profiles keep `panic=unwind`; `catch_unwind` prevents a Rust unwind crossing
the C boundary. It does not catch access violations, process aborts, invalid
foreign allocations, or races. The caller must own valid allocations for every
declared range, keep inputs unchanged during the call, and discard the complete
batch on any nonzero status. An absent/incompatible native library should select
the managed backend. Native code is trusted in-process code, not a sandbox.

## Reproduce the Windows x64 release build and tests

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/GodotLanguages/kernels/ownership/build.ps1
```

The default private Cargo is
`.fennara/languages/native/toolchains/cargo/bin/cargo.exe`; private CARGO_HOME and
RUSTUP_HOME point to its sibling `cargo` and `rustup` directories. MSVC x64 tools
are discovered with vswhere and the newest complete installed Windows SDK is
selected. All environment overrides last only for the recipe process and are
restored even if it fails. Pass `-CargoExecutable`, `-CargoDirectory`,
`-RustupDirectory`, `-VisualStudioDirectory`, or `-WindowsSdkDirectory` for another
existing installation. The verified private toolchain's exact version is emitted
by Cargo. A recent stable Rust toolchain supporting Cargo.lock v4 is required.

The recipe runs release-mode Rust tests, then builds the release DLL, both offline
and locked. Output stays under `.fennara/languages/ownership-kernel/target` by
default; `-OutputDirectory` selects another scratch directory. It prints
`NATIVE_KERNEL_DLL=<absolute path>` and the artifact SHA-256. It does not copy the
DLL to the engine, load it into a live editor, or install a game addon.

An analogous Unix command is provided for portability work; Unix is not tested:

```sh
cargo test --release --locked --offline --manifest-path Tools/GodotLanguages/kernels/ownership/Cargo.toml --target-dir .fennara/languages/ownership-kernel/target
cargo build --release --locked --offline --manifest-path Tools/GodotLanguages/kernels/ownership/Cargo.toml --target-dir .fennara/languages/ownership-kernel/target
# Linux artifact: .fennara/languages/ownership-kernel/target/release/libdof_graph_kernel.so
```

Rust tests cover exact BFS/member order, sparse ownership, exclusions, directed
edges, empty land and null empty adjacency, malformed CSR/water, capacity failure,
null/unaligned/overlapping/overflowing addresses, count bounds, and changed inputs.
They do not prove invalid foreign pointers safe or replace the C# host's
randomized differential tests, actual-map timing, campaign fingerprints, and
save/reload/replay checks. Compare equivalent managed/native algorithms including
native validation, pinning, input copies, output checks, and materialization;
report graph caching separately from any language effect.
