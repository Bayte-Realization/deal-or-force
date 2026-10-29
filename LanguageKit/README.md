# Godot 4.8 Beta2 language kit

Portable integration sources for **Luau, F#, Nim, Odin, Rust and Kotlin** on
`4.8.beta2.mono.official.cafc17a19`, Windows x86_64. Lua and Godot Sandbox
are separate optional adapters, not replacements for Luau or F#. **Zig (gdzig), Go
(graphics.gd) and Python (py4godot)** have separate dated evidence:
**Rust, Zig and Go passed real Beta2 runtime smoke tests on 29 October**.
Python remains experimental: py4godot fails the int64 boundary and teardown gate;
the alternative `godot4-meson` received a tested API-parser patch, not a runtime certificate.
The legacy `verified` flags are historical; use the exact-engine `verification` records.
Other languages retain their Beta1 evidence, not an inferred Beta2 certificate.
Start with [the current setup guide](START_HERE_RU.md) and
[the real-call report](reports/beta2-runtime-2026-10-29.md).

This is not a new official Godot distribution and does not replace its executable.
GDExtensions are loaded **per project**. Keeping DLLs beside the editor alone does
not register a language in every project. F# uses the same .NET runtime through a
C# bridge; native Rust/Nim/Odin classes are not automatically ScriptLanguage entries.
See the per-adapter report for what actually passed, including clean shutdown.

`LanguageKit/` at the game repository root is the community source package copied
from the editor-side kit. The installed editor and its local `projects/AllSix`
workbench stay in place. The game's .csproj excludes this entire directory:
examples have independent projects. `.gdignore` alone does not exclude C# files
from the .NET SDK's source glob.

## Start

Use PowerShell 7 and the console executable of the .NET editor. No global PATH
change is required; portable compiler paths can be supplied to each build recipe.

```powershell
./language-kit.ps1 -Action Doctor -EngineDirectory 'C:/GodotEngineNet 4.8-Beta2'
./tests/kit-tests.ps1
./language-kit.ps1 -Action Fetch -Languages rust,nim,odin,kotlin,luau,sandbox,lua
./language-kit.ps1 -Action Fetch -Languages zig,go,python      # recipe-only tracks
./language-kit.ps1 -Action Build -Languages fsharp
./language-kit.ps1 -Action Probe -Languages fsharp
```

`sources.lock.json` pins reviewed upstream commits. `Fetch` never resets an
existing checkout with a different revision or origin. Dependencies/submodules,
toolchain prerequisites and source patches are handled by each adapter's recipe.
For a private Cargo installation, for example:

```powershell
./language-kit.ps1 -Action Build -Languages rust -BuildOptions @{
    CargoExecutable = 'D:/PortableRust/bin/cargo.exe'
}
./language-kit.ps1 -Action Probe -Languages rust
```

The kit's default `.cache/` is local and ignored by Git. A build is not proof of
runtime compatibility: use the probe, inspect Godot ERROR lines and require exit 0.
Do not translate an import/shutdown access violation into a successful test.

## Install beside an existing editor

```powershell
./language-kit.ps1 -Action InstallKit -EngineDirectory 'C:/GodotEngineNet 4.8-Beta2'
```

Copies source recipes, templates, patches, reports and licenses to `LanguageKit/`.
Does not overwrite the editor, `GodotSharp`, project settings, running libraries
or differing existing files. Build output is deliberately not silently activated
in a live game. Open an adapter's generated project to develop with that binding,
then integrate its reviewed addon into your own project when the editor is closed.
An existing legacy Luau adapter must be migrated, not loaded beside a second copy.

`create-workbench.ps1` combines explicitly supplied, already built addons with
the managed template in a **fresh** project. `-EditorBootstrap -Probe` checks
editor startup/import and real runtime calls together. The installed local
`projects/AllSix` can be opened with `Open Workbench.cmd`; that launcher is not
needed by the game and does not restart another editor. It requires PowerShell 7
and the selected Java runtime. A source-only clone must build/create this project
first; compiled workbenches are deliberately absent from Git.

Use `Fetch` to obtain upstream sources at the revisions in the lock file. The
source package needs no upstream gitlinks or recursive clone of the game.
Third-party source keeps its original
license. Our kit code follows this repository's GPL-3.0 license; no claim is made
to authorship of the upstream bindings. Native binaries/toolchains are local build
artifacts, not committed game builds.

## Boundaries

- **Rust's veto is lifted (owner's decision, 29 October 2026).** It may be used for kernels,
  tools and new adapters, not only for exceptional problems. The earlier measurement still
  stands as advice rather than prohibition: the ownership Rust/Odin experiments in
  [kernels/ownership](kernels/ownership/README.md) — benchmarked with
  `Tools/OwnershipKernelBench` in the game development workspace — did not beat the existing
  C# median, and no production native hook was kept. So: allowed, still measured, and a
  weighted-sum probe is still not evidence of a game-speed gain.

- The game-specific `experiments/assault-rules` harness needs matching game sources
  in the parent repository. It is not a standalone adapter smoke test. The portable
  F# example is `adapters/fsharp`, which has no dependency on game rules.
- Trusted native/managed code can access the host. A native language binding is
  not a security sandbox for downloaded mods.
- The three new tracks are **recipe-only**: `zig` (gdzig vendors a Godot 4.7.2 API snapshot),
  `go` (graphics.gd compiles per project through `gd build`, needs Go 1.27 and cgo) and
  `python` (py4godot calls itself an early-phase demo). None of them is a library, an addon or
  a verified adapter on this engine; their READMEs state the prerequisite and the refusal path.
- Single-language probes do not prove coexistence, export, hot reload, networking,
  all editor APIs or any other OS. These need separate evidence.
- The old game's `.lua` Luau adapter is not overwritten by the new `.luau` kit.
  Lua5.4/LuaJIT and Luau have different syntax, APIs and runtimes.
- Keep campaign authority in one place: immutable bulk input into a kernel,
  validated bulk output through the existing commands. Do not move save/load,
  multiplayer authority or every scene node across language boundaries.
- A successful weighted-sum probe demonstrates calling the language, not a game
  performance improvement. Use representative batch workloads before migrating.

For usage, integration status and migration notes see [GUIDE.md](GUIDE.md)
(in Russian: [GUIDE_RU.md](GUIDE_RU.md)); both travel with the installed kit.
