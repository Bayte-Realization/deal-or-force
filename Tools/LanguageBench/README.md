# LanguageBench — second languages against C#, on three kernels

A standalone .NET 8 console host (like `Tools/SimBench`, server GC like the game) that runs
three kernels of different shapes in every contender this machine can build, checks every
output against the game's own C#, and times them alternated round by round.

| Kernel | Shape | C# reference (called as the game calls it) | Input |
| --- | --- | --- | --- |
| `frontier` | array crunch, 16.6 M cells | `FrontierField.Build` (Scripts/Simulation) | the real 5760×2880 `WorldMapSource.Control` grid and the `MapRasterCatalog` province topology; owners = the campaign's owners after a deterministic 3 % conquest / 0.2 % abandonment, so `CurrentHolder` takes every branch |
| `graph_full`, `graph_kernel` | graph walk, 34 k vertices | the original all-owner BFS (Corridors pore cache) and `OwnershipComponentGraph.ComputeManaged` | the real map; the existing ownership kernel of `Tools/GodotLanguages/kernels/ownership` and its existing host `NativeOwnershipKernel`, reused unchanged |
| `budget`, `luau_budget` | per-country arithmetic | `BudgetCore.Evaluate` for every nation | the real campaign advanced 30 days (`--days`), the shipped budget catalogue |

Contenders: the game's C# as written; the same C# single-threaded (the honest baseline for a
single-threaded native port); C# tuned (spans, `Vector<float>`, `Parallel.For`, scratch kept);
F# (same process); Rust (baseline x86-64 and x86-64-v3) and Zig through a C ABI DLL; the existing
Rust ownership kernel through its ABI v1 host; Luau through its real bridge, the per-request
worker process. Odin and Nim build too if `odin.exe`/`nim.exe` are passed to `build-native.ps1`.

## Fairness rules this follows

- Same algorithm everywhere, ported line by line. Faster arrangements of the same arithmetic are
  separate rows labelled `tuned`/`parallel`; their bytes must still equal the reference.
- Release builds: `dotnet -c Release` (tiered PGO on, as in the game); Rust `opt-level=3`,
  `codegen-units=1`, fat LTO; Zig `ReleaseFast` (no bounds checks); `-mcpu`/`target-cpu`
  baseline unless the row says `v3`.
- Warm-up discarded: at least 4 rounds, and at least 3 s for the small kernels so the JIT has
  promoted the managed code past tier 0.
- Every round runs every contender once, the order rotating each round (A B C, B C A, …).
  Each round therefore gives a ratio per pair; the median of those ratios is the headline,
  because it survives the load of other simulations on the machine.
- Frontier rounds collect garbage before each timed run (outside the timer), so one
  contender's 170 MB of scratch is not collected on the next one's clock.
- Every sample's output is compared with the reference outside the timed part: bytes for the
  frontier, a digest of the ordered BFS for the graph, float bits for the budget (Luau included:
  it rounds every operation to float32, exact by construction).
- The crossing is measured on its own (`CROSSING` lines): pinning plus a call to a no-op export
  with the same arity, the water packing and structural validation of the ownership host, the
  packing of Nation objects into flat arrays, and the worker spawn of Luau with an empty hook.

## Run it

```powershell
# from the repository root
powershell -ExecutionPolicy Bypass -File Tools/LanguageBench/build-native.ps1    # Rust, Zig (+ Odin/Nim if given)
dotnet build -c Release Tools/LanguageBench/LanguageBench.csproj -o tmp_logs/languages/langbench/bin
dotnet tmp_logs/languages/langbench/bin/LanguageBench.dll --series 2 > tmp_logs/languages/langbench/full.log
```

Options: `--only frontier,graph,budget,luau`, `--frontier-samples 30`, `--graph-samples 200`,
`--budget-samples 300`, `--luau-samples 200`, `--days 30`, `--seed 20260830`, `--native DIR`,
`--luau EXE` (default `build/luau-worker/Release/deal-or-force-luau.exe`), `--json PATH`.
`build-native.ps1 -OdinExecutable C:/…/odin.exe -NimExecutable C:/…/nim.exe` adds those two.
Toolchains default to the installed Beta3 kit's private cache
(`C:/GodotEngineNet 4.8-Beta3/LanguageKit/.cache/toolchains`); nothing is downloaded here.

Never starts Godot. All output (DLLs, logs, JSON with every raw sample) goes under
`tmp_logs/languages/langbench/`, which the editor ignores.

## Native ABI (lb v1)

All `extern "C"`, x64 cdecl, blittable arguments, caller-owned output, no retained pointers:

```c
uint32_t lb_abi_version(void);                                   /* 1 */
int32_t  lb_noop16(uintptr_t a0, ..., uintptr_t a15);            /* the crossing alone */
int32_t  lb_frontier_v1(const uint16_t *authored, const uint16_t *cell_to_province,
                        int32_t width, int32_t height, const uint16_t *owners, uint32_t owners_len,
                        const uint16_t *authored_owners, uint32_t authored_owners_len, uint8_t *out);
int32_t  lb_frontier_parallel_v1(..., uint8_t *out, int32_t threads);   /* Rust only */
int32_t  lb_frontier_tuned_v1(..., uint8_t *out, int32_t threads);      /* Rust only */
int32_t  lb_budget_v1(int32_t nations, int32_t lines, float tax_tolerance, float tax_evasion,
                      const float *line_floats /* L*12 */, const int32_t *line_ints /* L*4 */,
                      const int32_t *factor_offsets /* L+1 */, const int32_t *factor_slots,
                      const float *factor_weights, const float *slots /* N*8 */,
                      const float *nation_floats /* N*8 */, const float *rates /* N*L */,
                      float *out /* N*8 */);
```

The flat budget layout is documented in `BudgetFlat.cs` (`BudgetInput`). Status 0 is success.

This is a measurement harness, not a game backend: nothing here is loaded by the game.
