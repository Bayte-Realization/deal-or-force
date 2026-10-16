# Godot 4.8 Beta1 language kit

Portable integration sources for **Luau, F#, Nim, Odin, Rust and Kotlin** on
`4.8.beta1.mono.official.4d10419ca`, Windows x86_64. Lua and Godot Sandbox
are separate optional adapters, not replacements for Luau or F#.

This is not a new official Godot distribution and does not replace its executable.
GDExtensions are loaded **per project**. Keeping DLLs beside the editor alone does
not register a language in every project. F# uses the same .NET runtime through a
C# bridge; native Rust/Nim/Odin classes are not automatically ScriptLanguage entries.
See the per-adapter report for what actually passed, including clean shutdown.

## Start

Use PowerShell 7 and the console executable of the .NET editor. No global PATH
change is required; portable compiler paths can be supplied to each build recipe.

```powershell
./language-kit.ps1 -Action Doctor -EngineDirectory 'C:/GodotEngineNet 4.8-Beta1'
./tests/kit-tests.ps1
./language-kit.ps1 -Action Fetch -Languages rust,nim,odin,kotlin,luau,sandbox,lua
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
./language-kit.ps1 -Action InstallKit -EngineDirectory 'C:/GodotEngineNet 4.8-Beta1'
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

The repository also records upstream source gitlinks when present. A recursive
checkout obtains those sources; the portable installed kit can instead fetch the
same pinned revisions from the lock file. Third-party source keeps its original
license. Our kit code follows this repository's GPL-3.0 license; no claim is made
to authorship of the upstream bindings. Native binaries/toolchains are local build
artifacts, not committed game builds.

## Boundaries

- Trusted native/managed code can access the host. A native language binding is
  not a security sandbox for downloaded mods.
- Single-language probes do not prove coexistence, export, hot reload, networking,
  all editor APIs or any other OS. These need separate evidence.
- The old game's `.lua` Luau adapter is not overwritten by the new `.luau` kit.
  Lua5.4/LuaJIT and Luau have different syntax, APIs and runtimes.
- Keep campaign authority in one place: immutable bulk input into a kernel,
  validated bulk output through the existing commands. Do not move save/load,
  multiplayer authority or every scene node across language boundaries.
- A successful weighted-sum probe demonstrates calling the language, not a game
  performance improvement. Use representative batch workloads before migrating.

For Russian usage, integration status and migration notes see
[GUIDE_RU.md](GUIDE_RU.md), which also travels with the installed kit.
