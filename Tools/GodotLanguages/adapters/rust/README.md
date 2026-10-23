# Rust / gdext adapter

Upstream: https://github.com/godot-rust/gdext

Reviewed pin: `0b680b4d59c4790d4673b2293110687d2e5261c7`, version 0.5.5, MPL-2.0 (`upstream-LICENSE.txt`). No upstream modifications. Cargo.lock also pins transitive dependencies, including godot4-prebuilt `3a0f8c3db2157974b18e14acb1d082ea4b6dcf5c`.

Requirements: Windows x86_64, Rust >= 1.94, MSVC linker/Windows SDK, exact target Godot editor executable. `api-custom` generates both API and interface bindings from that executable; this is not a forced 4.7 compatibility claim for 4.8. Code generation is intentionally minimal, not all Godot classes; enable full codegen when an application needs it and revalidate.

```powershell
./build.ps1 -GodotExecutable C:/Godot/Godot_console.exe -SourceDirectory C:/sources/gdext -OutputDirectory C:/build/rust
./probe.ps1 -GodotExecutable C:/Godot/Godot_console.exe -OutputDirectory C:/build/rust
```

`-CargoExecutable` selects a portable cargo executable. Its Rust toolchain must be available through process-local PATH/RUSTUP_HOME/CARGO_HOME. Build emits `project/addons/dof_rust` and prints `ADDON_ROOT`. Copy that directory into a target project's `addons/dof_rust`; GDExtensions are per-project, not global engine plugins.

Exposes `DofRustKernel : RefCounted`, `weighted_sum(PackedInt32Array)` and `language_name()`. This tiny arithmetic kernel demonstrates a native boundary; it is not a port of authoritative campaign logic. It does not add Rust as a drop-in scene script or provide Rust syntax editing/completion in Godot. Cargo and a code editor remain the authoring tools. Build is debug; performance and export-template parity are not claimed.

Verified on `4.8.beta1.mono.official.4d10419ca`: editor import, class construction, method dispatch, negative and empty-array inputs, zero exit, no Godot error lines.
