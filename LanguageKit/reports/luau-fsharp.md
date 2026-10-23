# Luau, F# and the requested Lua addon: verification 2026-10-16

Target: `C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe`,
`4.8.beta1.mono.official.4d10419ca`; Windows x86_64. No running editor restarted,
no installed engine files or original game files changed by this track.

## Results

| Adapter | Actual integration | Result |
| --- | --- | --- |
| Luau | Native ScriptLanguageExtension, `.luau` Script resources | PASS; typed dispatch, independent instances, `.mod.luau` require, native Callable bool/int results; exit 0 |
| F# | `net10.0` class library through generated C# Godot bridge | PASS; Godot calls F# kernel = 42, instance state isolation, invalid input rejected; exit 0 |
| gilzoide Lua | Separate optional native Lua 5.4.8 ScriptLanguage + LuaState | PASS after recorded 4.8 member-name patch; table defaults/numeric keys, restricted libraries; exit 0 |
| All three | Lua + Luau + F# in one official .NET Godot process | PASS, each independently computes 42, normal exit 0 |

All final positive logs were checked for ERROR/SCRIPT ERROR, not only PASS text.
F# is **not** an attachable `.fs` editor scripting language. It uses Godot's
existing .NET runtime, no custom engine binary. Luau's archived binding surface
is older than 4.8; whole-API/editor/export certification is not implied.

## Editor-import follow-up: engine documentation race

The combined-track first-import tests found `0xC0000005` on cold immediate
`--editor --import` with F# plus Luau, Rust, Nim or Odin. This is **not** evidence
that every binding has a managed interoperability bug: it matches Godot's open
[issue #111048](https://github.com/godotengine/godot/issues/111048) and pending
[PR #123658](https://github.com/godotengine/godot/pull/123658). Deferred extension
documentation callbacks can execute after editor documentation has been deleted.
The inspected Windows minidump recorded an access violation reading address `8`
inside the Godot executable (`+0x53489d5`), not a binding DLL; without engine PDBs
this is consistent evidence, not a symbolicated local stack proving the function.

Bounded local controls:

- F#-only ordinary import: exit 0 (parent-track control).
- Existing managed Luau project with delayed headless editor: exit 0.
- Fresh F# + Luau project, first discovery with delayed editor: exit 0.
- The same freshly bootstrapped project, ordinary warm `--import`: exit 0.

`adapters/fsharp/editor-probe.ps1` provides delayed bootstrap then strict warm
import for isolated samples. It does not swallow/retry failures, patch the engine,
or promise the race is fixed. Private logs: `managed-luau-delayed.log`,
`editor-cold-delayed.log`, `editor-warm-import.log`, `crash53112.txt`.

## Reproducible sources

- Fumohouse/godot-luau-script `7942ce6def8023596ca0cb8d819b032eb40a4bce`;
  godot-cpp `714c9e2c165db2dcb7e6ea57e62a04204d3cfbfa`;
  Luau `e0b55a9cb1857bc699a20f0aa3099c7a495a0152`.
- gilzoide/lua-gdextension `58cb05e62557741ff646f0096fa4fdc8d09b1ae3`;
  Lua `6e22fedb74cf0c9b6656e9fce8b7331db847c605`;
  godot-cpp `58d1de720b8ffe9f8ffcdfe3a85148582cfd2e74`;
  sol2 `ab874eb0e8ef8aea4c10074a89efa25f62a29d9a`;
  tree-sitter `0bb43f7afb5f0d83579007037a13d1fede636dbd`;
  tree-sitter-lua `d76023017f7485eae629cb60d406c7a1ca0f40c9`.
- F# template: .NET SDK 10.0.402, Godot.NET.Sdk 4.8.0-beta.1, net10.0.
- Python 3.13; source-local SCons 4.11.1, Luau tomli 2.4.0. System SCons ran
  Python 3.10, which cannot parse these upstream generators: local venv solves it.

`adapters/*/build.ps1` and `probe.ps1` are portable source recipes. No DLL/EXE is
committed. Native output includes upstream licenses and bundled dependency notices.
Use source pins and recorded patches, not mutable default branches.

## Private execution evidence (not product dependencies)

Under `.fennara/languages/luau-fsharp/`:

- `luau-recipe-build.log`, `luau-probe.log`, `luau-modules-probe.log`;
  addon `luau-addon/`, final isolated project `luau-modules-probe/`.
- `fsharp-script-probe/`: generated project, built bridge and F# DLL;
  build had 0 errors / 0 warnings. Original uncombined `probe.gd` passed too.
- `lua-recipe-build-v2.log`, `lua-probe-v2.log`, addon `lua-addon-v2/`.
  Initial `lua-probe.log` is RED evidence: two duplicate-member registration
  errors, despite a misleading PASS line from the first unstrengthened probe.
- `combined-probe.log`: all three in one managed process, clean shutdown.

Luau artifact SHA256 (record of this build, not a reproducible-signature claim):
`C2FEF87D69D89C28A1C516B47455CBC4A0974488FB60D6DEDEB3C1A5E2994CB5`.

## Integration / migration boundary

Install these as project-local addons from the engine-side LanguageKit. Loading
DLLs globally next to the editor executable is not how GDExtension discovery works.
The old game already has a Fumohouse `.lua` adapter with the same registered native
class names: do not activate both old and new Luau adapters in that project.
The new `.luau` suffix avoids gilzoide Lua collisions, not duplicate Fumohouse
registrations. Preserve old Lua mod files; migrate deliberately while editor closed.

Only editor/debug mappings are shipped in these two native descriptors. A prior
legacy Luau/editor-DLL release experiment crashed during shutdown; release must
stay excluded until a release-template-specific regression passes. This work does
not overturn that game build safeguard. GDScript closures passed into Luau,
all export targets and hot reload remain unverified.

## Sensible game refactor direction

Keep campaign ownership/save/fingerprint/commands in the current authoritative
model. Start language adoption behind small batched interfaces:

- **F#**: pure typed formulas, eligibility/validation, economic planning scorers,
  data transforms. Tests compare exact outputs against C# baselines; no automatic
  speed claim, since both are .NET.
- **Luau**: trusted event/decision/mod rules via a documented capability table;
  return requested commands instead of mutating arbitrary Godot nodes/state.
  Do not dispatch a cross-language call per army/province per frame.
- **Optional Lua**: user-authored simple scripting when Lua compatibility matters;
  do not treat Lua source as type-compatible with Luau.

Measure batching, allocation and p95 tick time before moving a hot loop. Cross-
language rewrites without profiling can be slower and less moddable. These smoke
tests intentionally do not mutate a saved campaign or switch production logic.

Primary references: [Luau upstream](https://github.com/Fumohouse/godot-luau-script),
[Lua upstream](https://github.com/gilzoide/lua-gdextension),
[Godot C# basics](https://docs.godotengine.org/en/latest/tutorials/scripting/c_sharp/c_sharp_basics.html),
[F# class-library workflow](https://learn.microsoft.com/en-us/dotnet/fsharp/get-started/get-started-command-line).
