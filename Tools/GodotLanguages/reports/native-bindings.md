# Native language adapter verification — 2026-10-16

Target: `C:/GodotEngineNet 4.8-Beta1`, `4.8.beta1.mono.official.4d10419ca`, Windows x86_64. Engine executable and live game/editor were not modified. Each adapter ran in its own temporary project; no user's project was used as a test fixture.

| Adapter | Retained upstream pin | Result |
| --- | --- | --- |
| Rust gdext 0.5.5 | `0b680b4d59c4790d4673b2293110687d2e5261c7` | Custom API + interface generated from exact engine. Editor import and native class invocation passed, exit 0. |
| Nim gdext-nim 0.17.1 | `15b69a92ba2642f2bea117e01e3bbcd112c9f57d` | MSVC header/runtime startup repaired with retained patch. Editor import and native class invocation passed, exit 0. |
| Odin odin-godot | `93395901a76f62a9f2adff76c2eb38a185d91513` | Direct upstream C-interface layer. Editor import, class/method dispatch and teardown passed, exit 0. Full generator/Godin remain unsupported. |

The Rust/Nim probe passes `[3, -2, 7]` to a weighted sum and checks 20, the correct language name and an empty-array result of 0. Odin checks the same scalar sum and a zero-input result. Probe runners reject nonzero exits, absent success markers, Godot `ERROR:`, `SCRIPT ERROR:` and crash lines. Compilation alone is not accepted as success.

## Local evidence

Under the publication worktree's ignored `.fennara/languages/native/`:

- `rust-build-final.log`, `rust-probe-final.log`; artifact `rust-build/project/addons/dof_rust`.
- `nim-build-final.log`, `nim-probe-final.log`; artifact `nim-build/project/addons/dof_nim`.
- `odin-build-v2.log`, `odin-probe-final.log`; artifact `odin-build/project/addons/dof_odin`.

Build scripts and probe sources are retained under `Tools/GodotLanguages/adapters/{rust,nim,odin}`. Upstream license texts are preserved. No binary or private toolchain belongs in Git.

## Toolchains

Portable tools were installed only in ignored task scratch, without changing system PATH or replacing an existing installation:

- Rust 1.99.1 (`58cf8f9c3`, 2026-10-13), rustup minimal x86_64-pc-windows-msvc, minimum binding requirement 1.94. Cargo.lock pins application dependencies.
- Nim 2.2.12 official Windows x64 archive SHA256 `DAFE9C81F8118FAC2E07EE24F3649448A590D8B0F3B55D1A97C66F99035CC8E6`, checked against upstream checksum.
- Odin dev-2026-10 official Windows archive SHA256 `B6B2E426A8B6F147B74EC7E18BEF8DF27CF40FE16193159DF7CA09A0CC0C2011`, checked against GitHub release digest.
- Existing Visual Studio Community 2022 MSVC 14.44.35207, Windows SDK. No Visual Studio components were installed.

## Limits and refactor boundary

These are opt-in, per-project native GDExtensions, not six new globally registered scripting languages. Rust and Nim examples are debug builds; Odin is optimized. No language-speed conclusion follows from the arithmetic probe. The complete upstream suites, export templates, every engine API, hot reload and other platforms were not tested.

Lifecycle caveat: a first cold import of the fresh Nim fixture completed the editor scan without an error line but returned a nonzero exit once; its repeat import and runtime both returned 0. The parent integration also reproduced the engine's documented editor documentation-generation teardown race ([Godot issue #111048](https://github.com/godotengine/godot/issues/111048)), including mixed managed/native fixtures. The central kit uses a bounded delayed-editor bootstrap before its strict warm import. This is a workaround, not a change to the engine or proof of general production stability. Cross-language deployment acceptance belongs to the central kit verification.

Keep campaign state, save/fingerprint rules and command ordering in C#. Profile before extracting a kernel. Good first candidates are bounded batch operations over plain numeric buffers (path-cost rasterization, map geometry, compression), with a managed reference implementation and exact equivalence tests. Do not move one object/property access at a time through Variant calls, scatter authority among languages, or mix floating-point reducers into multiplayer lockstep without explicit rounding/order contracts. F# can share .NET data without introducing a native ABI and is a separate adapter track.

Primary sources: [gdext](https://github.com/godot-rust/gdext), [gdext-nim](https://github.com/godot-nim/gdext-nim), [odin-godot](https://github.com/dresswithpockets/odin-godot), [Nim Windows distribution](https://nim-lang.org/install_windows.html), [Odin 2026-10 release](https://github.com/odin-lang/Odin/releases/tag/dev-2026-10).
