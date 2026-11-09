# Kotlin worker example

A stateless protocol-1 worker written in Kotlin, compiled with **Kotlin/Native**
to a single `worker.exe`. No Java runtime ships with the game and none is
required: the executable is the whole program, like the Go and Rust examples.

Kotlin/JVM would also speak the protocol, but `worker_executable` must be a real
executable inside this package - a `.jar` cannot be launched directly, and a
system `java.exe` cannot be referenced outside the mod directory. Kotlin/Native
is the target that keeps the package self-contained.

## Build

Needs the Kotlin/Native compiler (`kotlinc-native`, part of the Kotlin/Native
distribution or a Kotlin Multiplatform project with a `mingwX64`/native target).

```powershell
kotlinc-native Main.kt -opt -o worker
# produces worker.kexe on some toolchains, worker.exe on others -
# rename/copy the result to worker.exe beside this file's parent folder:
#   corecode/worker.exe
```

`manifest.json` points at `corecode/worker.exe`. The template ships `enabled:
false` and is never discovered by the loader anyway (`_templates` prefix).

## Protocol notes

- One JSON request on stdin, closed after the write; one JSON response on
  stdout. Read to EOF - do not wait for a second request.
- Response shape: `{"protocol":1,"logs":[...],"commands":[...]}`. Logs are
  capped at 32 entries of 1024 chars; the whole response at 256 KB.
- Whitelisted commands only (`WorkerModBackend.Capabilities` plus
  `study_doctrine` / `propose_diplomatic_act`); an unknown type fails the batch.
- Workers are single-player in protocol v1 and spawn once per hook, so keep
  startup cheap - Kotlin/Native cold start is milliseconds, a JVM's is not.
- The snapshot carries nations by tag, never by list id; resolve tags the same
  way the C# command layer does before queueing anything.
