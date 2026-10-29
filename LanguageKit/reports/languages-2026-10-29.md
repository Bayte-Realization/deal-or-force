# Language tracks — 29 October 2026 (Godot 4.8 Beta2)

Target moved from `4.8.beta1.mono.official.4d10419ca` to
`4.8.beta2.mono.official.cafc17a19` (`C:/GodotEngineNet 4.8-Beta2`,
GUI SHA-256 `6B894EF7578A8999266ECDBC267BCD424CAADB16B8F15E0F6DEE4D3FC0596524`).

Two kinds of statement live in this kit, and they are not interchangeable:

- **verified** — this kit built it and ran a probe against an exact engine, on this machine;
- **recipe-only** — the pin, the prerequisites and the recipe are exact and checked, but the
  build was **not** executed here. A recipe that cannot proceed fails loudly; it never reports
  an artifact or a pass it did not produce.

## The tracks

| Track | Upstream / licence | Pin (date) | Integration | Toolchain | Status |
|---|---|---|---|---|---|
| luau | [godot-luau-script](https://github.com/Fumohouse/godot-luau-script) MIT (archived) | `7942ce6` | ScriptLanguageExtension | C++ build | verified on Beta1 |
| fsharp | included template + C# bridge | — | managed library | .NET SDK | verified on Beta1 |
| nim | [gdext-nim](https://github.com/godot-nim/gdext-nim) MIT | `15b69a9` | GDExtension | Nim 2.2 | verified on Beta1 |
| odin | [odin-godot](https://github.com/dresswithpockets/odin-godot) Apache-2.0 | `9339590` | GDExtension | Odin dev-2026-10 | verified on Beta1 |
| rust | [gdext](https://github.com/godot-rust/gdext) MPL-2.0, release **v0.5.5** | `0b680b4` | GDExtension | Rust 1.94+ | verified on Beta1; **veto lifted 29 Oct** |
| kotlin | [godot-jvm](https://github.com/utopia-rise/godot-jvm) MIT | `5d39c7e` | JVM GDExtension | JDK 17+ | verified on Beta1 |
| lua | [lua-gdextension](https://github.com/gilzoide/lua-gdextension) MIT | `58cb05e` | ScriptLanguageExtension | C build | optional, verified on Beta1 |
| sandbox | [godot-sandbox](https://github.com/libriscv/godot-sandbox) BSD-3 | `b43ed16` | sandbox GDExtension | C++ build | optional, verified on Beta1 |
| **zig** | [gdzig](https://github.com/gdzig/gdzig) MIT | `c1b0cc3` (2026-10-27) | GDExtension | **Zig 0.17.0** | **implemented, not executed** |
| **go** | [graphics.gd](https://github.com/quaadgras/graphics.gd) MIT | `af2e539` (branch `release`, 2026-10-25) | cgo GDExtension + `gd` tool | **Go 1.27**, C compiler | **implemented, not executed** |
| **python** | [py4godot](https://github.com/niklas2902/py4godot) MIT | `b41ddb8` (2026-10-29) | Cython GDExtension | **Python 3.11+**, MSVC | **recipe only, not executed** |

The zig and go tracks are not sketches: each has a kit-owned kernel (`DofZigKernel`, `DofGoKernel`)
and a probe that asserts `weighted_sum` on four vectors — `20`, `0`, `-20`, `2147483647` — in the
same shape as the Rust/Nim/Odin adapters. They are "not executed" only because this machine
cannot run them: **Zig is absent**, and **Go is 1.21.4 while graphics.gd asks for 1.27**.

## Why the three new tracks are not "verified"

- **gdzig** vendors a **Godot 4.7.2** API snapshot and requires Zig 0.17.0 exactly. Against
  4.8-beta2 that is forward compatibility, not a supported pair. `-Dgodot_compatibility_minimum`
  is a floor, not an API upgrade.
- **graphics.gd** asks for **Go 1.27** and builds through cgo, so it also needs a C compiler. A
  `gd build` flow exists upstream; this adapter uses the `c-shared` path because that is what
  yields a vendor-neutral probe.
- **py4godot** describes itself as *"an early phase of development… consider this more as a
  demo"*. Its release `.zip` is copied into `addons/`; building from source is four upstream
  steps plus pip, which this kit runs inside a **kit-owned venv** rather than in your
  interpreter. Its probe is load-only and says so. It is not a place for campaign authority.

## Rust, now allowed

The owner lifted the Rust veto on 29 October 2026. What changes: Rust may be used for kernels,
tools and new adapters without asking for an exception. What does not change: the measured
result. The Rust/Odin ownership kernels did not beat the existing C# median, so a Rust rewrite
still needs a benchmark that beats the managed version, not a hope that native is faster.

## What to run on a machine that has the toolchains

```powershell
./language-kit.ps1 -Action Doctor
./tests/kit-tests.ps1
./language-kit.ps1 -Action Fetch -Languages zig,go,python
./language-kit.ps1 -Action Build -Languages zig    -BuildOptions @{ ZigExecutable    = 'C:/zig/zig.exe' }
./language-kit.ps1 -Action Build -Languages go     -BuildOptions @{ GoExecutable     = 'C:/Go/bin/go.exe' }
./language-kit.ps1 -Action Build -Languages python -BuildOptions @{ PythonExecutable = 'C:/Python311/python.exe' }
./language-kit.ps1 -Action Probe -Languages zig,python
```

`Probe` on `go` ends in a documented refusal with the exact per-project command — by design.

## What was actually run in this pass

- `tests/kit-tests.ps1` — **24 checks, 0 failures**: the lock schema, the install-boundary
  contract, and the four new assertions that an unverified adapter says *that*, names its
  toolchain, and says why.
- `language-kit.ps1 -Action Doctor` against Beta2 — engine `4.8.beta2.mono.official.cafc17a19`,
  executable and directory, and every adapter with a VERIFIED / RECIPE-ONLY verdict plus
  READY/MISSING lines for its toolchain. On this machine: `go` READY (1.21.4), `python` READY
  (3.13.1), `ninja` READY, `zig` MISSING, `gcc` MISSING, `meson` MISSING, `cargo`/`nim`/`odin`
  not on PATH.
- `language-kit.ps1 -Action Build -Languages zig` end to end — fetch at the pin succeeded
  (`SOURCE zig c1b0cc3…`) and the build refused with `This pin requires Zig 0.17.0`; exit 1,
  no artifact claimed.
- `adapters/python/build.ps1 -PreflightOnly` — `PYTHON_PREFLIGHT OK pin=b41ddb8… python=Python 3.13.1`,
  exit 0: the pin and the version gate are live.
- The go and zig adapters' version gates were exercised through those same checks; their builds
  were **not** run, because the toolchains they require are not installed.

Nothing else. No native build, no probe, no export, and no claim that 4.8-beta2 changes any
adapter's behaviour — that is the next pass, on a machine with Zig 0.17.0, Go 1.27+ with a C
compiler, and (for a source build) MSVC plus Python 3.11+.

## Who did what

The zig and go adapters were written in the same pass by a parallel agent working in this tree;
this pass contributed the Beta2 engine pin, the lock's verified/requires/status contract, the
Doctor rewrite, the four lock assertions, the python track and these notes. That is recorded
rather than glossed over: two hands wrote this folder today, and the scripts are the authority,
not the prose.
