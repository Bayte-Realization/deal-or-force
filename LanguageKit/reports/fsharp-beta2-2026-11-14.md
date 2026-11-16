# F# on Godot 4.8 beta2: runtime smoke, 14 November 2026

Engine: `4.8.beta2.mono.official.cafc17a19`, Windows x86_64, .NET SDK 10, `net10.0`.
F# is a managed library called through a C# Godot class, not a `ScriptLanguage`.

| Check | Command | Result |
|---|---|---|
| Adapter template build | `adapters/fsharp/build.ps1 -GodotExecutable <beta2 console exe> -OutputDirectory <scratch>` | 0 errors, 0 warnings |
| Adapter runtime probe | `Invoke-KitGodot <exe> --headless --path <scratch> --script probe.gd --quit-after 120` (what `probe.ps1` runs) | `LANGUAGE_KIT_PASS language=fsharp managed_bridge=true score=42 instances=true invalid_input=true`, exit 0 |
| AllSix workbench build | `dotnet build LanguageKitFSharp.csproj` on a copy of `projects/AllSix` | 0 errors; resolved `GodotSharp/4.8.0-beta.2`, `GodotSharp.dll` 4.8.0+cafc17a19 |
| AllSix workbench probe | `<exe> --headless --path <copy> --script res://combined_probe.gd -- fsharp` | `LANGUAGE_WORKBENCH_PASS languages=fsharp failures=0`, exit 0 |

Changed in this pass: `projects/AllSix/LanguageKitFSharp.csproj` now pins
`Godot.NET.Sdk/4.8.0-beta.2` (it said beta.1 and only resolved beta2 through the
restore cache). `sources.lock.json` gained `verification.fsharp` (stage
`runtime-smoke`), so `language-kit.ps1 -Action Doctor` prints
`RUNTIME-SMOKE-VERIFIED` for fsharp.

Not covered here: the game's own use of F# (pure rules in `Scripts/Rules`, the
`IGlobalityMod` corecode mods). Those are tested by the game's
`Tools/FormableTests` flags, recorded in the repository's
`docs/LANGUAGE_BACKENDS_RU.md`.
