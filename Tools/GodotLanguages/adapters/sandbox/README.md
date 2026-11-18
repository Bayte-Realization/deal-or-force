# Godot Sandbox adapter

Optional companion to the six requested language integrations, based on [libriscv/godot-sandbox](https://github.com/libriscv/godot-sandbox), `main` commit `4f9c73d683f6ae734c87773d01938448ab3c3d50` (13 November 2026, 23 commits after release v0.60). It was moved off v0.60 on 16 November 2026 for upstream security fixes: indirect calls (`call`, `callv`, `get`, `set`, `Callable.create`) to banned methods and properties are refused, `connect()` is checked, and coroutines no longer own borrowed Variants.

This is a RISC-V execution boundary with SafeGDScript and guest C++/Rust tooling. It does **not** automatically sandbox native Nim/Odin/Rust GDExtensions or the Kotlin/JVM runtime. Native extensions and their build scripts remain trusted code.

## Build and check

Requires PowerShell 7, Git, Python/SCons, CMake, Ninja, MinGW-w64 C++ and Zig 0.17.0. Tested: w64devkit x64 2.10.1 (GCC 16.2.0) and Zig 0.17.0, both in `toolchains.windows.lock.json` (`./workbench/install-portable-toolchains.ps1 -Languages w64devkit,zig`). Compilers are not bundled in this source kit.

```powershell
./build.ps1 -SourceDirectory <pinned-godot-sandbox-checkout> `
  -OutputDirectory <artifact-directory> -CompilerBin <w64devkit/bin> -ZigExecutable <zig-0.17.0/zig.exe>
./probe.ps1 -GodotExecutable <Godot-mono-console.exe> `
  -ProjectDirectory <artifact-directory/project>
```

The build initializes exact submodule revisions, applies the compatibility patch idempotently, handles the known Windows Git symlink header, compiles the native DLL, cross-compiles the SafeGDScript compiler guest `gdscript.elf` to RISC-V with `zig c++` (upstream builds it with riscv64 GCC 14 on Linux), and assembles the addon from the tracked `bin/addons/godot_sandbox` files like upstream's release workflow. No prebuilt release archive is downloaded; the only download is `generated_api.hpp` from godot-sandbox-programs v0.1, checked against its published SHA-256. A restricted Sandbox compiles its scripts with `gdscript.elf` inside the sandbox, and finds `compile` through the ELF's `.symtab`. Zig strips `.symtab` unless it links with `-g`, so the recipe links with `-g` and `strip-debug.py` then removes only the `.debug_*` sections (5.1 MB result), refusing to write if the loaded image or any symbol changes or `compile` is missing. It does not touch a user's running Godot editor or game project. Only the Windows x86-64 single-precision DLL and gdscript.elf are built; the addon's descriptor still lists other platforms, whose libraries are not built or validated by this work.

## Compatibility changes

- Explicit guest-property methods are named `sandbox_set`, `sandbox_get`, and `sandbox_get_property_list`. The original names shadowed methods already owned by `Object` and were rejected by Godot 4.8. Native C++ implementations are unchanged; regular `Object` properties keep their inherited names. Existing scripts using the old explicit guest API need to use the new aliases.
- Script languages/classes register at `SERVERS`, before editor script-language selectors are constructed. Node subclasses and resource loaders remain at `SCENE`. The fresh probe also supplies its known extension list before startup, because discovering a brand-new extension later in the first scan still occurs too late for those selectors.

The patch is preserved as source in `patches/`; no modified upstream binary is committed. BSD-3-Clause and dependency notices are under `licenses/`; the staged addon also retains upstream `LICENSE` and `THIRDPARTY.txt`.

## Security scope

The smoke test proves registration, a restricted arithmetic call, no unexpected guest exceptions, and normal process exit. It is **not** an adversarial security audit. Set resource/class/object/method/property allow-lists and finite memory, allocation, reference, recursion and execution budgets before loading untrusted initializers. Expose a small command-shaped capability API, not arbitrary scene-tree or filesystem access. Keep the native extension, guest compiler, translation policy, host callbacks and versioned guest ABI within the trust review.

For the game, first use this boundary for isolated, bounded mod calculations. Validate outputs in C# before creating authoritative commands. Preserve deterministic ordering and save/network compatibility; never assume a new language or a sandbox makes a subsystem faster or deterministic without tests.

## Godot 4.8-beta3 (16 November 2026)

Built from `main` `4f9c73d6` as above, on `4.8.beta3.mono.official.c51ff06e8`: `EDITOR_BOOTSTRAP cold_exit=0 warm_import_exit=0 errors=0`,
`SANDBOX_PROBE:PASS sum=42 restrictions=true`, `SANDBOX_ADAPTER:PASS import=0 runtime=0`. A first Zig build without `.symtab`
failed exactly here (`Function not found: compile`), which is why the recipe checks for it. The probe
is a smoke test: the new security behaviour is covered by upstream's own unit tests, which passed
on this commit (GitHub Actions run 43373963201), not by this probe. Release v0.60 also passed on
Beta3 before the move. See `reports/beta3-2026-11-16.md`.
