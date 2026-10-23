# Nim / gdext-nim adapter

Upstream: https://github.com/godot-nim/gdext-nim

Reviewed pin: `15b69a92ba2642f2bea117e01e3bbcd112c9f57d`, version 0.17.1, MIT (`upstream-LICENSE.txt`). The upstream branch targets Godot >= 4.6; this adapter was independently tested on the actual 4.8 Beta1 binary. This does not prove every 4.8 API is available: upstream's generated class catalogue remains 4.6.

Requirements: Windows x86_64, Nim >= 2.2 (tested 2.2.12), Visual C++ / Windows SDK. Prefer a dedicated checkout of the reviewed pin: build applies the retained `patches/windows-runtime-bootstrap.patch` in that checkout.

```powershell
./build.ps1 -GodotExecutable C:/Godot/Godot_console.exe -SourceDirectory C:/sources/gdext-nim -OutputDirectory C:/build/nim -NimExecutable C:/Nim/bin/nim.exe
./probe.ps1 -GodotExecutable C:/Godot/Godot_console.exe -OutputDirectory C:/build/nim
```

Two Windows compatibility repairs are required:

1. Force-include `stdint.h` for MSVC because upstream's generated interface header relies on fixed-width C types without including their definition.
2. Initialize Nim's runtime/allocator before entering any Nim callback. Upstream deferred `NimMain()` to the editor initialization phase; the first `new environment` could crash before reaching that phase. A small C entry wrapper calls the generated `PreMain()` once. User top-level execution remains in its original phase through `NimMainInner()`, without initializing the allocator twice. This is specific to the tested Nim C backend and must be revalidated when updating Nim.

The embedded build also defines `noSignalHandler`: a native addon must not replace Godot/.NET's process-wide signal handlers.

Build emits `project/addons/dof_nim` and prints `ADDON_ROOT`. Install into a project's `addons/dof_nim`. Exposes `DofNimKernel : RefCounted`, `weighted_sum(PackedInt32Array)` and `language_name()`. This is native compiled extension support, not a new attachable `.nim` ScriptLanguage/editor integration. Keep game state owned by the existing campaign and exchange bounded data across this boundary.

Verified: clean editor import, native class construction, GDScript method calls, negative and empty-array inputs, exit 0, no Godot errors. MSVC currently reports upstream's irrelevant `-static-libgcc` flag as warning D9002; this is not hidden. No full upstream Nim test suite, export build, hot reload, cross-platform or performance claim.
