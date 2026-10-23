# Community source package — 2026-10-20

The editor-side `C:/GodotEngineNet 4.8-Beta1/LanguageKit` was copied, not removed.
The community copy is `LanguageKit/` in the game repository. Engine binaries,
GodotSharp, generated AllSix projects, compiler distributions, caches and DLLs
are not redistributed in this source commit. Upstream sources retain their own
licenses; our compatibility patches and examples do not imply upstream authorship.

Fresh checks on Windows x64 / official 4.8.beta1.mono.4d10419ca:

- Kit contract tests: 21 PASS, no failures (pins, paths, source-only packaging,
  preserving different existing files, idempotent install).
- Doctor: exact installed editor version confirmed.
- F# portable adapter: rebuilt from this package, 0 errors / 0 warnings; real
  Godot execution returned score 42, isolated instances, invalid input rejected,
  exit 0. No installation or replacement of a system compiler was needed.
- Game C# build: excludes the entire kit, including Content/None globs; a
  `.gdignore` file alone is not a .NET compilation boundary. Regression command:
  `./LanguageKit/tests/game-isolation.ps1` from the game root.

The other adapter runtime results are the dated 2026-10-16 evidence in the sibling
reports, not a fresh eight-language certification on October 20. Kotlin snapshot
Maven artifacts remain a reproducibility limitation. No new export/hot-reload,
other-OS, security-sandbox or performance claims are made. Game-specific experiments
need matching game sources, unlike the standalone adapter templates.
