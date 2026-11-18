# Luau ScriptLanguage (GDExtension), Godot 4.8 beta3

This adapter embeds Luau, **not Lua 5.4 or LuaJIT**, and registers a native
`ScriptLanguageExtension`. The user-suggested gilzoide/lua-gdextension implements
Lua, not Luau; it is packaged separately under `../lua`.

Upstream: [Fumohouse/godot-luau-script](https://github.com/Fumohouse/godot-luau-script),
commit `7942ce6def8023596ca0cb8d819b032eb40a4bce` (MIT, **archived**: every later Godot
API change is ours to absorb, through the patches below).
Pinned dependencies: godot-cpp `714c9e2c165db2dcb7e6ea57e62a04204d3cfbfa`,
Luau `e0b55a9cb1857bc699a20f0aa3099c7a495a0152`.

Since 14 November 2026 the build binds against the API of the engine that loads it:
`build.ps1` runs `<engine> --headless --dump-extension-api` inside the source checkout
(never inside a game project) and generates both godot-cpp and the Luau bindings from
that dump. The descriptor therefore says `compatibility_minimum = "4.8"`.

## Patches

| Patch | Applies to | Why |
|---|---|---|
| `builtin-vararg-return.patch` | source | keep bool/int results of native `Callable.Call` |
| `skip-native-pointer-args.patch` | source `bindgen/` | 4.8 adds `GDExtensionManager.load_extension_from_function(const GDExtensionInitializationFunction*)`; the generator only skipped `const void*` and stopped |
| `editor-only-singletons.patch` | source | 4.8 lists the editor-only `GDScriptLanguageProtocol` among singletons; fetching it outside the editor printed an `ERROR` on every load in a game or `--script` run |
| `distinct-luau-extension.patch` | source | `-Suffixes luau` (default): `.luau`, `.mod.luau`, `init.luau` only, so gilzoide Lua can own `.lua` in the same workbench |
| `dual-lua-luau-extension.patch` | source | `-Suffixes lua-and-luau`: loader and saver take both; `require` tries `.luau`, then `.lua`; `.mod.lua` and `.mod.luau` are modules. For the game, which ships no other Lua |
| `godot-cpp-beta2-api.patch` | `extern/godot-cpp` | the 4.4-era generator read typed dictionaries as `K;;V` (4.8 writes `K;V`), would redefine the `<cstdint>` limit macros 4.8 now exports as global constants, and took `"meta": "required"` for a C++ type |

The two suffix patches exclude each other: build them from separate checkouts.

## Build and probe

```powershell
$engine = 'C:/GodotEngineNet 4.8-Beta3/Godot_v4.8-beta3_mono_win64_console.exe'
git clone --no-checkout https://github.com/Fumohouse/godot-luau-script.git .cache/sources-beta3/luau-distinct
git -C .cache/sources-beta3/luau-distinct checkout --detach 7942ce6def8023596ca0cb8d819b032eb40a4bce
./adapters/luau/build.ps1 -GodotExecutable $engine -SourceDirectory .cache/sources-beta3/luau-distinct `
    -OutputDirectory .cache/verified-beta3/luau/addon -Suffixes luau -Jobs 8
./workbench/verify-beta3.ps1 -Languages luau        # = probe.ps1 -OutputDirectory .cache/verified-beta3/luau -Release
```

`build.ps1` builds `editor` and `template_release` (`-Targets` narrows it), writes
`luau.gdextension` with a `windows.release.x86_64` entry for the release DLL, and a
`build-info.json` with the engine, the API dump hash, the patches, the suffixes and the
SHA256 of each DLL. It refuses an existing output directory: a loaded DLL is never replaced.

`probe.ps1` makes a fresh project under `<OutputDirectory>/runs/<stamp>` and checks,
for the suffixes `build-info.json` claims:

- the language registers and the resource loader recognises every claimed suffix;
- `probe.luau` (and with both suffixes `probe_dual.lua`): typed `add` through a required
  `.mod` module = 42, `increment` on two instances 1, 2 / 1, read-only native Callables;
- with both suffixes, the game's `EngineScriptMod.Load`/`Invoke` sequence on a copy of
  `mods/_templates/luau_engine/corecode/main.lua` (`-EngineModDirectory` takes the real one):
  `CACHE_MODE_IGNORE` load, class `LuauScript`, base `RefCounted`, `on_load(api)`,
  `on_campaign_created(api)`, `on_day_advanced(api)` all `true` with a read-only dictionary
  of method Callables, and `set_feature("aviation", true)` reached on day 365 only;
  a `.lua` script requiring `modules/war_damage.mod.lua` gets 3 options back;
- `-Release`: editor bootstrap, `--export-release` with the official 4.8.beta2 template
  (console wrapper), the exported build runs the same checks with the `template_release` DLL.

Any `ERROR:`, `leaked`, `still in use`, `No loader found`, crash text or non-zero exit fails.
A probe with an older lua-only DLL fails on purpose:
`loader does not recognise .luau (recognised: tres,res,gd,gdc,cs,lua)`.

## Verified on 4.8 beta2 (14 November 2026)

Engine `4.8.beta2.mono.official.cafc17a19`, API dump SHA256
`2C4306B1FFF4547A9224C6224F22ED59F10A26C8DDB883D1E0A8A9AC4FFB5D7C`, MSVC (VS 2026), SCons 4.11.1.

- `-Suffixes lua-and-luau` (game flavor), `probe.ps1 -Release -EngineModDirectory <game template>`:
  debug and exported release both `LANGUAGE_KIT_PASS`, `LUAU_ENGINE_MOD_PASS`, exit 0, no errors.
  editor DLL `C4F7142DF61FA5B5F4052489C6A2EB97A12E9BA381DD74A6180AE67808634FE6`,
  template_release DLL `B8A304BC196172EBEA11368D4E8A950A8A2B4A673E2BF0DBBC235FB043896E8D`.
- `-Suffixes luau` (kit flavor), `./workbench/verify-beta2.ps1 -Languages luau`: debug and
  exported release `LANGUAGE_KIT_PASS`, exit 0, results in
  `.cache/verified-beta2/verification-20261114-210405`. editor DLL
  `F78C4479AD34F2A05FCC138086387D67048E2322D80386C326EAC48E0A9F5A45`, template_release DLL
  `75AE6A24EF0224EDA806BDF9D7EC65BAC05358B0F0E86EBC3C3E98CD89891714`.
  The same addon in a scratch copy of `projects/AllSix`:
  `combined_probe.gd -- luau` → `LANGUAGE_WORKBENCH_PASS languages=luau failures=0`.
  `sources.lock.json` records `verification.luau`; Doctor prints `RUNTIME-SMOKE-VERIFIED`.
- `tests/kit-tests.ps1`: 29 checks, 0 failures.

Hashes are a record of what was tested, not a signature; another toolchain gives others.

## Safety / compatibility limits

This is a trusted in-process native extension, not an OS sandbox. It has no worker
deadline. A bad script or native bug can hang or crash Godot. For untrusted mods use the
worker boundary (`luau_worker`, JSON protocol v1) and explicit capabilities.

Do **not** load two Fumohouse builds in one project: their native class names are the
same. GDScript closures (lambdas) passed into Luau are still not certified; the probe and
the game pass method/C# Callables. Properties, signals, hot reload and editor tooling
beyond script registration are not covered by the probe. Only Windows x86_64 is built.
No binaries are committed. MIT upstream `LICENSE.txt` / `COPYRIGHT.txt` are copied with
the addon; upstream icons are not bundled.

## Godot 4.8-beta3 (16 November 2026)

Rebuilt from the Beta3 API dump (`api_sha256=D5B1F03A…456E`), editor and template_release, with the
same five patches. Kit flavor: debug and exported-release `LANGUAGE_KIT_PASS`. Game flavor
(`-Suffixes lua-and-luau`, probe with `mods/_templates/luau_engine/corecode`): `LUAU_ENGINE_MOD_PASS`
and `LUAU_MODULE_PASS module=war_damage.mod.lua` in debug and exported release. Details in
`reports/beta3-2026-11-16.md`.
