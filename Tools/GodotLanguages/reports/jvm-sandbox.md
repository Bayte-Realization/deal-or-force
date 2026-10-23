# Kotlin/JVM and sandbox track — 2026-10-16

## Environment and source pins

- Target engine inspected: `4.8.beta1.mono.official.4d10419ca`, installed under `C:/GodotEngineNet 4.8-Beta1`. No engine executable was replaced and no user editor was restarted.
- Godot-JVM release `1.0.0-rc4`, commit `5d39c7e3fbe5be78853837e3c89676e7a92c9ec9`, MIT. godot-cpp fork `d058a0e8f39542bc655349e65f92735f721c94ae`, MIT.
- Godot Sandbox `v0.60`, commit `b43ed1619abc12a669c59cb0aa25c4582873a7b9`, BSD-3-Clause. godot-cpp `60b5a4196de8442b43b32ba68ebe1e79cfcb762f`; libriscv `e121610a7b88bb902e9d2a33b4cdfc14847003e5`.
- Kotlin built with JDK 25/Gradle 9.1.0/Kotlin 2.3.20; sample bytecode target 17 verified as class major version 61. Native JVM editor binding built with MSVC 2022, optimized, LTO disabled.
- Sandbox built with w64devkit 2.10.1/MinGW-w64; only private build caches contain toolchains/binaries.

## Download receipts

| Asset | SHA-256 |
| --- | --- |
| `godot-jvm-addon-1.0.0-rc4.zip` | `22424ffe160fec0fe901abafefefd969140fe2b0e4e1fd5ae44b0a172a6d28fe` |
| `godot-sandbox.zip` from `v0.60` | `6b7f4c6a74d283352036c2cf6baf4672059ab17d4eaf00386a0357eda73ce865` |
| `w64devkit-x64-2.10.1.7z.exe` | `add1edf984ce4d473cfeb7c52e0ab38db73a855bba6c7f564d5e16648eb2896a` |

Local optimized editor DLL SHA-256: `b88df48bf00131f31f41de1a6b0ac1b580559e523c9fbe1196ba56fe18190198`. Local patched Sandbox DLL SHA-256: `248e8dadd322e023a7e99f05a9834ff316946c5fa96b31f7e65708cd100121d9`. Build hashes are receipts, not guarantees that compiler output is reproducible byte-for-byte.

## Results and the important failed controls

1. The real Kotlin `RefCounted` sample compiled, registered and returned `42` and `Kotlin/JVM` to GDScript, with a normal runtime exit.
2. Initial **cold** immediate editor imports crashed with access violation `-1073741819`, including a source-built DLL. A subsequent warm import passed. A first impression that rebuilding with LTO disabled solved it was disproved by a fresh-directory control and is **not** a conclusion of this report.
3. Fresh projects containing only the known preseeded GDExtension list, without an existing import/doc cache, passed strict editor import and runtime probes with exit `0`, no Godot errors. This is a startup/CI mitigation, not an engine race fix.
4. Unpatched Sandbox arithmetic worked, but emitted three duplicate inherited-method binding errors, plus cold script-menu icon-index errors. These were not counted as a clean integration.
5. After the source compatibility patch and early startup extension list, Sandbox passed strict editor import and restricted arithmetic with exit `0`, no Godot errors. No unexpected guest exceptions were observed.
6. Parent integration verified all six requested languages in a single `.NET` workbench, including a real F#/CLR call alongside Kotlin/JVM, with clean editor bootstrap/import and runtime. This is a small interoperability fixture, not a performance or full game integration claim.
7. The final checked-in adapter scripts were run end-to-end into fresh `kotlin-final` and `sandbox-final` directories. Both builds and strict editor/runtime checks passed. Kotlin's final build generated Java 17-compatible bytecode while using JDK 25.

Private evidence, relative to the clean worktree:

- `.fennara/languages/jvm-sandbox/kotlin-build.log` and `kotlin-target17-build.log` — successful Kotlin builds.
- `.fennara/languages/jvm-sandbox/kotlin-preseed-probe/kotlin-import.log` and `kotlin-runtime.log` — clean fresh startup/runtime.
- `.fennara/languages/jvm-sandbox/sandbox-preseed-probe/sandbox-import.log` and `sandbox-runtime.log` — clean fresh startup/runtime.
- `.fennara/languages/jvm-sandbox/kotlin-artifact/project/kotlin-import.log` — failed cold-control import, retained.
- `.fennara/languages/jvm-sandbox/sandbox-artifact/project/sandbox-import.log` — failed cold-control menu timing, retained.
- `.fennara/languages/combined-six-v3.log` — parent six-language result.
- `.fennara/languages/jvm-sandbox/kotlin-final/project/kotlin-{import,runtime}.log` and `sandbox-final/project/sandbox-{import,runtime}.log` — final reusable adapter checks.

## Remaining validation

No standalone Windows export, mobile export, GraalVM native image, network determinism, long campaign, hostile-mod audit, or game-performance benchmark was completed for these two adapters. No gameplay simulation module was rewritten in Kotlin or SafeGDScript. Snapshot Maven artifacts still need a content-checksum lock for a fully reproducible public release pipeline. Preserve dependency notices when distributing runtime/JAR bundles.

## Primary references

- [Godot-JVM current repository](https://github.com/utopia-rise/godot-jvm), [RC4 release and snapshot instructions](https://github.com/utopia-rise/godot-jvm/releases/tag/1.0.0-rc4), [source build guide](https://godot-jvm.dev/en/1.0/contribute/build-from-source/).
- [Godot Sandbox source](https://github.com/libriscv/godot-sandbox), [v0.60 release](https://github.com/libriscv/godot-sandbox/releases/tag/v0.60).
- [Godot cold GDExtension documentation shutdown race #111048](https://github.com/godotengine/godot/issues/111048). The local before/after controls are compatible with the documented cold/warm timing behavior; no local native stack proved a specific crashing source line.
