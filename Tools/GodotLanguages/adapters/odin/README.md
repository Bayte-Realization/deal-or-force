# Odin / odin-godot adapter

Upstream: https://github.com/dresswithpockets/odin-godot

Reviewed pin: `93395901a76f62a9f2adff76c2eb38a185d91513`, Apache-2.0 (`upstream-LICENSE.txt`). No upstream modifications.

Requirements: Windows x86_64, Odin (tested `dev-2026-10-nightly:84bc3fc`), native Windows linker/SDK. This adapter uses the upstream `gdext` C-interface package directly. It does not depend on generated full Godot bindings, Temple, Godin, or submodules.

```powershell
./build.ps1 -GodotExecutable C:/Godot/Godot_console.exe -SourceDirectory C:/sources/odin-godot -OutputDirectory C:/build/odin -OdinExecutable C:/Odin/odin.exe
./probe.ps1 -GodotExecutable C:/Godot/Godot_console.exe -OutputDirectory C:/build/odin
```

Build emits `project/addons/dof_odin` and prints `ADDON_ROOT`. Copy it into a project's `addons/dof_odin`. `src/kernel.odin` shows explicit class registration, instance allocation/free, Variant-call and ptrcall dispatch, owned StringName/String lifetime, and unregister cleanup. It exposes `DofOdinKernel : RefCounted` and `weighted_sum3(a, b, c)`.

The upstream README calls the complete toolkit WIP and documents Godot 4.4, with Godin explicitly incomplete. Its pinned Temple generator does not compile with Odin 2026-10 (`filepath.walk`, `os.Errno` and other removed APIs). Therefore this kit does **not** advertise full generated 4.8 wrappers, attachable `.odin` scene scripts, Godin, editor completion, or reload support. The smaller direct ABI path is independently verified on Godot 4.8 Beta1.

Verified: editor import, class construction, negative-input method call returning 20, zero input returning 0, exit 0 and no Godot errors, including teardown. No full upstream suite, export-template, performance or cross-platform claim.
