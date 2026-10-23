# Godot Sandbox adapter

Optional companion to the six requested language integrations, based on [libriscv/godot-sandbox](https://github.com/libriscv/godot-sandbox), release `v0.60`, commit `b43ed1619abc12a669c59cb0aa25c4582873a7b9`.

This is a RISC-V execution boundary with SafeGDScript and guest C++/Rust tooling. It does **not** automatically sandbox native Nim/Odin/Rust GDExtensions or the Kotlin/JVM runtime. Native extensions and their build scripts remain trusted code.

## Build and check

Requires PowerShell 7, Git, Python/SCons, and MinGW-w64 C++. Tested compiler: w64devkit x64 2.10.1, GCC 16.2.0. The compiler is not bundled in this source kit.

```powershell
./build.ps1 -SourceDirectory <pinned-godot-sandbox-checkout> `
  -OutputDirectory <artifact-directory> -CompilerBin <mingw/bin>
./probe.ps1 -GodotExecutable <Godot-mono-console.exe> `
  -ProjectDirectory <artifact-directory/project>
```

The build initializes exact submodule revisions, applies the compatibility patch idempotently, handles the known Windows Git symlink header, compiles the native DLL, verifies the downloaded release archive, and stages a separate probe project. It does not touch a user's running Godot editor or game project. Only the Windows x86-64 DLL is rebuilt/patched; other platforms in the upstream archive are not validated by this work.

## Compatibility changes

- Explicit guest-property methods are named `sandbox_set`, `sandbox_get`, and `sandbox_get_property_list`. The original names shadowed methods already owned by `Object` and were rejected by Godot 4.8. Native C++ implementations are unchanged; regular `Object` properties keep their inherited names. Existing scripts using the old explicit guest API need to use the new aliases.
- Script languages/classes register at `SERVERS`, before editor script-language selectors are constructed. Node subclasses and resource loaders remain at `SCENE`. The fresh probe also supplies its known extension list before startup, because discovering a brand-new extension later in the first scan still occurs too late for those selectors.

The patch is preserved as source in `patches/`; no modified upstream binary is committed. BSD-3-Clause and dependency notices are under `licenses/`; the staged addon also retains upstream `LICENSE` and `THIRDPARTY.txt`.

## Security scope

The smoke test proves registration, a restricted arithmetic call, no unexpected guest exceptions, and normal process exit. It is **not** an adversarial security audit. Set resource/class/object/method/property allow-lists and finite memory, allocation, reference, recursion and execution budgets before loading untrusted initializers. Expose a small command-shaped capability API, not arbitrary scene-tree or filesystem access. Keep the native extension, guest compiler, translation policy, host callbacks and versioned guest ABI within the trust review.

For the game, first use this boundary for isolated, bounded mod calculations. Validate outputs in C# before creating authoritative commands. Preserve deterministic ordering and save/network compatibility; never assume a new language or a sandbox makes a subsystem faster or deterministic without tests.
