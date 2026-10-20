# Experimental real Luau ScriptLanguage

This adapter embeds Luau, **not Lua 5.4 or LuaJIT**, and registers a native
`ScriptLanguageExtension`. The user-suggested gilzoide/lua-gdextension implements
Lua, not Luau; it is packaged separately under `../lua`.

Upstream: [Fumohouse/godot-luau-script](https://github.com/Fumohouse/godot-luau-script),
commit `7942ce6def8023596ca0cb8d819b032eb40a4bce` (MIT, archived).
Pinned dependencies: godot-cpp `714c9e2c165db2dcb7e6ea57e62a04204d3cfbfa`,
Luau `e0b55a9cb1857bc699a20f0aa3099c7a495a0152`.
The adapter uses the upstream 4.4-era generated API binding surface; passing the
4.8 beta1 smoke test does **not** imply every new 4.8 method is bound.

Our recorded patches:

- `builtin-vararg-return.patch`: preserve bool/int results from native Callable.
- `distinct-luau-extension.patch`: use `.luau`, `.mod.luau`, `init.luau`, including
  module loader, saver, class/type analysis and editor extension recognition.
  This prevents ownership conflicts with gilzoide Lua's `.lua` handler.

Tested on official `4.8.beta1.mono.official.4d10419ca`, Windows x86_64:
native Script registration, typed method return, independent instance state,
`.mod.luau` imports and read-only Dictionary/native Callable return values;
normal process exit 0, no ERROR/SCRIPT ERROR lines. Also tested coexisting Lua,
Luau and the managed F# bridge in one headless .NET Godot process.

Cold headless `--import` is affected by Godot's known
[extension documentation shutdown race](https://github.com/godotengine/godot/issues/111048),
which also reproduces with other languages' otherwise working extensions. A fresh
F# + Luau project passed a bounded headless editor bootstrap, then normal warm
`--import`, both exit 0. See `../fsharp/editor-probe.ps1`. This workaround does not
repair the engine or certify arbitrary immediate-shutdown automation.

```powershell
git clone --no-checkout https://github.com/Fumohouse/godot-luau-script.git C:/Temp/godot-luau-script
git -C C:/Temp/godot-luau-script checkout --detach 7942ce6def8023596ca0cb8d819b032eb40a4bce
./build.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -SourceDirectory C:/Temp/godot-luau-script -OutputDirectory C:/Temp/dof_luau
./probe.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -AddonDirectory C:/Temp/dof_luau -ProjectDirectory C:/Temp/luau-probe
```

Prerequisites: Git, Python 3.12+, MSVC C++ toolchain. The build installs pinned
SCons 4.11.1/tomli 2.4.0 in a source-local virtual environment, not system Python.
The output addon goes in a project's `addons/dof_luau`. Godot needs project-local
GDExtension discovery; dropping a DLL next to the editor alone is insufficient.

## Safety / compatibility limits

This is a trusted in-process native extension, not an OS sandbox. It has no worker
deadline. A bad script/native bug can hang or crash Godot. For untrusted mods use a
restricted worker boundary and explicit capabilities, not arbitrary Node access.

Do **not** load this together with the old Fumohouse `.lua` adapter already in a
project: their registered native class names are the same. A migration must
preserve old `.lua` mods and disable the old descriptor only while Godot is closed.
Neither this build nor its probe modifies/restarts any running editor.

The descriptor deliberately has only a Windows debug/editor library entry.
Release/export, hot reload, all editor tooling and GDScript closures passed into
Luau are **not certified**. A previous local 4.8-dev7 release experiment with the
legacy editor DLL corrupted the heap on shutdown. It is not enough to see a PASS
line: require normal process exit and regression checks on the actual export
template before adding a release entry. Existing game release exclusions remain.

No binaries are committed. MIT upstream `LICENSE.txt` / complete `COPYRIGHT.txt`
are retained and copied with the addon; upstream icons are not bundled here.
