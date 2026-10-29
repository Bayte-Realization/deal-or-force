# Languages for Godot 4.8 Beta2 .NET

[English](GUIDE.md) · [Русский](GUIDE_RU.md)

Installed target: Windows x86-64, `4.8.beta2.mono.official.cafc17a19`
(`C:/GodotEngineNet 4.8-Beta2/Godot_v4.8-beta2_mono_win64_console.exe`).
The October 16 joint run was `4.8.beta1.mono.official.4d10419ca`
([reports/native-bindings.md](reports/native-bindings.md)). This kit is a lab. It is not
on the campaign tick. F# assault rules stay. Do not read a binding as a faster simulation:
the ownership benchmark's C# median was about 2629 ms against Rust at about 3657 ms.

## Status on 29 October 2026

| Language | In this repo | Upstream pin | Beta2 on this machine | One command |
| --- | --- | --- | --- | --- |
| F# | `adapters/fsharp` managed bridge. Not a scene script. | none (our template). SDK `Godot.NET.Sdk/4.8.0-beta.2` | Built and probed. `LANGUAGE_KIT_PASS language=fsharp` | `./adapters/fsharp/build.ps1` then `probe.ps1`, console exe above |
| Rust | `adapters/rust` `DofRustKernel`. Not a simulation kernel. | gdext `0b680b4d59c4790d4673b2293110687d2e5261c7` (crate 0.5.5, 4 Oct 2026). Release tag v0.5.5 is the older `e7b3a4a7dc9678a5502dc8432c258c702ff89429`. Master `a6318ab080aba694814962fcf09a398b09af3e2f` (25 Oct) still has no `api-4-8` feature | Built and probed. `RUST_PROBE PASS weighted_sum=20 empty=0`. `api-custom` from the Beta2 executable | `./adapters/rust/build.ps1 -GodotExecutable <beta2 console> -SourceDirectory <that commit> -OutputDirectory <scratch>` then `probe.ps1` |
| Go | `adapters/go` `DofGoKernel` via graphics.gd. Not the globe renderer. | `quaadgras/graphics.gd` `af2e5393cdf04d36e1cba40cb4d6437624916d40` (`go 1.27`) | Probe passed: `GO_PROBE PASS`. PATH Go 1.21.4 is rejected. The DLL used Go 1.27.3 | `./adapters/go/build.ps1` with `-GoExecutable` pointing at Go 1.27+, then `probe.ps1` |
| Zig | `adapters/zig` `DofZigKernel`. Not the globe renderer. | gdzig `c1b0cc3c7befc4c2483624d078e1e650b07b2dfa` (27 Oct 2026). Vendored API is Godot 4.7.2; Zig 0.17.0 | Probe passed: `ZIG_PROBE PASS`. That does not cover 4.8-only APIs | `./adapters/zig/build.ps1` with Zig 0.17.0, then `probe.ps1` |
| Python | `adapters/python` recipe. Not on the tick. | py4godot `b41ddb82af5f8e59ce4e1a9254bec9cf82a138dd` (29 Oct, meson fix). Latest **release** is `4.7-alpha21` (`d8e17428deeb0428587349b663f6da26cd71ef3a`) | Not built. No 4.8 release. Upstream still calls it a demo | do not treat `build.ps1` as a green Beta2 run |
| Nim, Odin, Kotlin, Luau, Lua | Existing adapters, pins unchanged in `sources.lock.json` | same commits as the October lock | Not rebuilt this pass. `adapters/lua/build.ps1` and `adapters/luau/build.ps1` still refuse any engine that is not Beta1 | Doctor, then that adapter's README |
| Sandbox | `adapters/sandbox` plus the 4.8 patch. Not the campaign tick | godot-sandbox **v0.60** `b43ed1619abc12a669c59cb0aa25c4582873a7b9` (still the latest GitHub release). Master has later commits; they were not rebuilt | Not rebuilt | `./adapters/sandbox/build.ps1` only after a fresh pin review |

GDScript is the engine's language. C# is the game (`Godot.NET.Sdk/4.8.0-beta.2` already). Neither was retargeted here.
This is a set of source adapters and local examples, not a new official Godot
distribution. The engine executable and GodotSharp are not replaced.

## What actually works

The rows below are the 16 October joint run on Beta1, not the 29 October Beta2 check.

| Language | Real integration | Call that was tested |
| --- | --- | --- |
| Luau | ScriptLanguageExtension, `.luau` resources | Typed method, module, independent instances, native Callable |
| F# | .NET 10 library through a C# bridge | Batch calculation, independent state, rejection of invalid input |
| Rust | Compiled GDExtension, API from the installed 4.8 | Native class, PackedInt32Array, negative and empty input |
| Nim | Compiled GDExtension, fixed Windows bootstrap | Native class, batch calculation, clean shutdown |
| Odin | Low-level C ABI GDExtension | Class registration, Variant/ptrcall, object ownership and release |
| Kotlin | Godot-JVM RC4 GDExtension, compiled JVM classes | Loading a `.kt` resource and calling a registered method |

All six passed one shared run of a real .NET Godot: the first editor bootstrap, a
second import, the calls, and an exit with code 0 and no Godot ERROR. An extra shared
check also passed with **a separate Lua 5.4.8** and **Godot Sandbox**. Lua does not
replace Luau. LuaJIT was not tested in this pass.

F# does not add Attach Script for `.fs`; Rust, Nim and Odin are not new scene-script
languages. Their code is edited and compiled with external tools, and the classes they
register are available to Godot. Kotlin needs the Gradle build and registrars, and Java.
Extensions are activated **per project**, not globally by DLLs sitting next to
Godot.exe. The languages do not get the whole 4.8 API automatically.

## Running it locally, as the author does

The kit is installed next to the editor:

```text
C:/GodotEngineNet 4.8-Beta2/LanguageKit/
  Open Workbench.cmd
  projects/AllSix/       # the local built workbench, not in Git
  adapters/             # examples, recipes, patches, licences
  reports/              # limits and results of each adapter
```

`Open Workbench.cmd` opens a **separate** workbench editor without closing the main
game. Double-clicking it does not carry these extensions into any other project.
PowerShell 7, .NET SDK 10 and Java must stay installed. The tested machine uses JDK 25;
the final Kotlin example generates Java 17 bytecode. On another machine or Java, pass
your own paths in the parameters of `open-workbench.ps1`. This launcher opens the
sources; the computational regression is run separately:

```powershell
./language-kit.ps1 -Action Doctor
./tests/kit-tests.ps1
# For an already built workbench, from the LanguageKit folder:
$env:JAVA_HOME = 'C:/Program Files/Eclipse Adoptium/jdk-25.0.2.10-hotspot'
& 'C:/GodotEngineNet 4.8-Beta2/Godot_v4.8-beta2_mono_win64_console.exe' `
  --headless --path ./projects/AllSix --script res://combined_probe.gd -- `
  fsharp luau rust nim odin kotlin
```

The last line of a success is `LANGUAGE_WORKBENCH_PASS ... failures=0`.
Any ERROR, crash or non-zero exit code is a failure, even if a PASS was printed before
it. Do not run checks on top of an open workbench while its DLLs are being rebuilt.
No probe closes the user's main editor.

## Getting the sources and rebuilding

The public repository keeps the recipes, the example sources, compatibility patches,
licences and the exact revisions of seven upstream projects. Binaries, compilers and
game builds are not in Git. To get the sources, run Fetch against `sources.lock.json`:

```powershell
./language-kit.ps1 -Action Fetch
./language-kit.ps1 -Action Build -Languages fsharp
./language-kit.ps1 -Action Probe -Languages fsharp
```

The native adapters need their compilers and the Windows SDK; the parameters of each
are in `adapters/<language>/README.md`. `-BuildOptions` passes the paths of portable
compilers without changing the system PATH. Kotlin and Sandbox need their own build
tools; Kotlin's Maven SNAPSHOT dependencies are not yet pinned by content checksum, so
fully reproducible releases are not claimed yet. The builders apply patches only to
the selected pinned checkout; do not point them at your own unfinished upstream branch.
Source Fetch does not reset any other checkout.

After a build, `create-workbench.ps1 -OutputDirectory <a-new-empty-folder>` takes the
dictionary `-Addons @{ luau=<addon>; rust=<addon>; nim=<addon>; odin=<addon> }`,
`-KotlinProject <the-built-project>` and `-JavaHome <JDK>`. The flags
`-EditorBootstrap -Probe` check that the built combination works together.
The optional keys `lua` and `sandbox` are not needed for the six languages.

`InstallKit` copies only sources, refuses when an existing file differs, does not
overwrite the engine and does not activate DLLs in the live game. Caches and `projects`
are not copied into the source kit. GPL-3.0 covers our glue code; third-party
libraries keep MIT, MPL-2.0, Apache-2.0 or BSD-3-Clause and their own notices.

## Fixes and how far it is ready

- Nim: the Windows runtime and allocator are initialised before the first Nim callback;
  `stdint.h` is added, and capturing the global signal handlers is turned off.
- Luau: `.luau`/`.mod.luau`, a fix to vararg Callable. Upstream is archived and its API
  surface is old; only the editor and debug mappings that were checked are claimed.
- Lua: `LuaTable.get/set` are replaced by `get_value/set_value`, so they do not shadow
  inherited Object methods, with the documentation updated.
- Sandbox: separate `sandbox_*` aliases and early language registration. A limited
  arithmetic example passed; this is **not** an audit against malicious mods.
- Odin: a working direct ABI instead of the full Temple/Godin generator, which does not
  work with the new Odin. Full generated wrappers and Godin are not declared ready.
- Rust: minimal bindings with the API and header of the specific installed engine.

Beta1 had a cold immediate-import problem consistent with
[Godot #111048](https://github.com/godotengine/godot/issues/111048); the Beta2 pass did not
re-test it, so treat the bootstrap below as a workaround that still earns its place, not as a
closed issue: shutdown can
overtake the deferred generation of extension documentation. Our bootstrap creates the
derived list of known extensions in advance, gives the editor 300 frames with a 20 ms
frame delay, and then requires an ordinary import and runtime without errors. This is a
launch and CI workaround, **not a fix to the Godot executable**, and no guarantee that
there are no races in general. The failed runs that were observed are kept in the
reports as failures.

The main project already contains the old Fumohouse Luau with `.lua`. It cannot be
loaded next to a second Fumohouse: the native types have the same names. The `.luau`
suffix separates the new Luau from gilzoide Lua, but does not solve two Luau extensions
side by side. Migrating old mods and saves is a separate task, to be tested with the
editor closed. The game's current addon is not replaced and existing mods are not
renamed.

Standalone exports and release templates, hot reload, every API and OS, long campaigns,
network determinism and malicious code are not certified yet. Native, JVM and .NET code
is trusted: an ordinary GDExtension is not a sandbox for downloaded mods.

## Refactoring the game: where it actually makes sense

World state, command order, saves, fingerprints and network authority stay in C#. The
six runtimes are not made mandatory for every player and modder. A language is brought
in for a task, with a managed reference and a backend that can be switched off.

| Language | Candidates | What cannot be promised without measuring |
| --- | --- | --- |
| Rust | Only exceptional, complex tasks with measured benefit and a small isolated boundary | Rust as an everyday language of the project, or a gain from one native call per province |
| Nim | Numeric buffers, converting regional data, offline generation | The full 4.8 API and no GC or allocator cost |
| Odin | Raster and overlay kernels, dense graphics buffers, preprocessing | A ready high-level toolkit, which upstream does not provide yet |
| F# | Pure rules, eligibility, economic and diplomatic assessments, validation | Automatic speed-up over C#: it is the same .NET and JIT |
| Luau | Events, decisions, mod formulas through a small capability API | Safety of arbitrary libraries and of frequent host calls |
| Kotlin | Content validators, complex tools, using JVM libraries | Cheap startup, RAM or JNI; the JVM is not needed in every daily tick |

The author's rule: **Rust does not become an everyday language of the project**. A
strict compiler is not runtime instability; the real risks are FFI, ownership, platform
builds and maintenance, and the measured benefit has to justify them.

The first real batch experiment is done: ownership connectivity in Rust and Odin, one
ABI and a C# reference, 35 295 provinces plus small and stress inputs. Both matched the
results, but neither beat the existing C# on the median. The sources and checks stay in
`kernels/ownership`, the experimental host in `Tools/OwnershipKernelBench` of the game
repository. **The game's Corridors has no native hook, and no player needs a DLL from
this experiment.** The same port in Nim is not needed yet: duplicating the algorithm
does not remove the cost of packing and checks.

The more fitting next roles: Odin for a separate dense raster or overlay after
profiling; Nim for checking and converting data for modders; F# for compact pure combat
rules; Luau for optional formulas on a copy of the data. The existing JSON rules stay
the simplest path, and modders need none of these languages.
A pure [F# experiment with the combat rules](experiments/assault-rules/README.md) has
been compared with the game's actual C# assessor: 624 502 cases, no differences. That
checks equivalence and readability; it is not adoption in the game or a speed
measurement. The game's current `EngineScriptMod` accepts `.lua`; the kit's events and
formulas do not become available to it automatically. `enact_policy`, `manage_debt` and
`add_course_shock` go through the command queue, but the old `set_feature`,
`set_nation_number` and `set_define` change state directly, so the API as a whole cannot
be declared queued or a safe sandbox.

For further kernels we measure time including copying across the boundary,
allocations, p50/p95 and memory, and run small, typical and stress inputs. Integer
overflow, float rounding, traversal order and errors need an explicit contract. Then we
compare campaign commands, fingerprints and replay, including save/load and the network.
If the gain does not justify the complexity, it stays in C#. An arithmetic smoke test
does not prove the game got faster.

The priorities do not change: a comfortable interface, a strong AI, a stable simulation
and simple modding. Adding languages is a tool for those goals, not a goal in itself.
