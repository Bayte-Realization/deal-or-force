# Lua 5.4 GDExtension (separate optional language)

Source: [gilzoide/lua-gdextension](https://github.com/gilzoide/lua-gdextension),
commit `58cb05e62557741ff646f0096fa4fdc8d09b1ae3` (MIT), Lua **5.4.8**.
This is not Luau. The requested Luau adapter is under `../luau`.
This recipe builds the ScriptLanguage and LuaState core; it does not enable the
upstream optional editor REPL plugin or copy its scenes/icons.

## Godot 4.8 beta1 compatibility patch

Unmodified upstream printed two errors on Godot 4.8 beta1: `LuaTable` attempted to
bind `get` and `set` on top of `Object` members. `godot48-table-members.patch`
exposes these table operations as **`get_value(key, fallback)`** and
**`set_value(key, value)`**, retaining their C++ behavior and updating generated
class documentation. `rawget`/`rawset` and string property access are unchanged.
Update code using upstream's table.get/table.set when moving to this recipe.

Tested on `4.8.beta1.mono.official.4d10419ca`, Windows x86_64: native `.lua` Script
attach/method calls, independent instance counters, restricted LuaState without
opened io/os/package libraries, numeric table keys/defaults/string properties.
Final run: clean process exit 0, no ERROR/SCRIPT ERROR lines. Coexistence with the
`.luau` adapter and F# managed bridge also passed in one process.

For automated editor checks with these native adapters, see
`../fsharp/editor-probe.ps1` and the upstream
[cold headless import race](https://github.com/godotengine/godot/issues/111048).
Runtime success alone is not an editor-import test; use a delayed editor bootstrap
followed by strict warm import while that engine issue remains unfixed.

```powershell
git clone --no-checkout https://github.com/gilzoide/lua-gdextension.git C:/Temp/lua-gdextension
git -C C:/Temp/lua-gdextension checkout --detach 58cb05e62557741ff646f0096fa4fdc8d09b1ae3
./build.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -SourceDirectory C:/Temp/lua-gdextension -OutputDirectory C:/Temp/dof_lua
./probe.ps1 -GodotExecutable 'C:/GodotEngineNet 4.8-Beta1/Godot_v4.8-beta1_mono_win64_console.exe' -AddonDirectory C:/Temp/dof_lua -ProjectDirectory C:/Temp/lua-probe
```

Prerequisites: Git, Python 3.12+, MSVC. A local virtual environment pins SCons
4.11.1; all Git submodule SHAs are pinned by the source commit. Output goes in a
project's `addons/dof_lua`, not globally into the editor executable folder.

Only the tested Windows editor/debug entry is enabled. LuaJIT, release/export,
all upstream API features and full editor UI are not certified by this smoke
test. Restricted LuaState libraries are not an OS security boundary or CPU/memory
quota. Fully attached Lua scripts have Godot API access and must be trusted.

Native binaries remain local. The builder copies upstream MIT and all bundled
dependency notices (including Lua's complete header copyright/license block).
