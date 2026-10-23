# Kotlin/JVM adapter

Uses [Godot-JVM](https://github.com/utopia-rise/godot-jvm) as a **GDExtension**, not a replacement engine. The tested source is release candidate `1.0.0-rc4`, commit `5d39c7e3fbe5be78853837e3c89676e7a92c9ec9`. The upstream documentation's newer `1.0.0` wording is not a reason to label this RC as a final release.

## Build and check

Requires PowerShell 7, Git, SCons/Python, MSVC C++ build tools, and a JDK. Tested with JDK 25 and MSVC 2022. Set the arguments to your installations; the scripts do not install global environment variables or modify an engine executable.

```powershell
./build.ps1 -SourceDirectory <pinned-godot-jvm-checkout> -OutputDirectory <artifact-directory> `
  -JavaHome <jdk-directory> -VcVarsPath <VS/VC/Auxiliary/Build/vcvars64.bat>
./probe.ps1 -GodotExecutable <Godot-mono-console.exe> `
  -ProjectDirectory <artifact-directory/project> -JavaHome <jdk-directory>
```

The build downloads a checksum-pinned addon archive, compiles the Kotlin fixture, and builds the native editor DLL from pinned source with optimization and LTO disabled. Runtime debug/release DLLs in that archive are upstream artifacts; actual standalone exports have not been tested. The native editor build is a supported reproducible route, **not** a claimed fix for the editor's cold-import race.

The sample uses the selected toolchain JDK but generates Java 17 bytecode for its main and generated registrar classes. `--jvm-path` selects the runtime explicitly. This prevents a different machine-wide `JAVA_HOME` or an embedded JRE from silently choosing a runtime too old for the generated code.

The Gradle RC artifacts currently live in the upstream Maven snapshot repository. Source and native archive are pinned; Maven `SNAPSHOT` resolution is not a content-addressed dependency lock. Freeze/verify resolved dependency checksums before a reproducible release pipeline or public binary distribution.

## Why the extension-list template exists

On the tested Godot 4.8 Beta 1, immediate shutdown after first GDExtension discovery can crash. See [Godot issue #111048](https://github.com/godotengine/godot/issues/111048). The isolated fixture supplies its known `.godot/extension_list.cfg` before the first editor launch, so the extension loads during startup. The probe rejects nonzero exits and Godot error lines; it does not turn a successful calculation followed by a crash into a pass. For an existing project, merge existing extension entries rather than replacing the list.

## Integration boundary

Copy `addons/jvm`, compiled `jvm`, and JVM source/registration directories into a deliberately configured project. Merely copying a DLL beside `Godot.exe` does not install a script language globally. Keep Gradle/native addon versions matched.

Good candidates are isolated tooling, content validators, or subsystems that benefit from existing JVM libraries. Avoid one JNI crossing per province/army in a tick. Use bounded batch inputs/outputs, benchmark against C#, and retain the current authoritative C# command/save/fingerprint contract until determinism and measured performance justify a migration. JVM code is trusted native-process code, not a safe mod sandbox.

Licenses for Godot-JVM and its godot-cpp fork are preserved under `licenses/`. Preserve Kotlin/Gradle/JRE and other dependency notices when distributing binaries; this source adapter does not grant additional rights to third-party runtimes.
