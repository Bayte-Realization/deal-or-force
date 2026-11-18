# Experimental native language adapters

The installer pins a real Luau ScriptLanguageExtension and builds it locally with
the LanguageKit recipe (`LanguageKit/adapters/luau/build.ps1 -Suffixes lua-and-luau`):
bindings from the installed engine's `--dump-extension-api`, the recorded patches,
`.lua` and `.luau`, an editor and a template_release DLL. `-Activate` copies them into
`game/extensions/luau` only when no Godot holds the old DLL. No engine fork is required.
The beta2 build was verified on 14 November 2026 in a separate project, not yet installed
into the game (the editor had the project open).
See [the Russian guide](../../docs/LUAU_ADAPTER_RU.md) for installation, API,
trust boundaries and limitations. This is not native Rust/Go/F# source support.

`luau_probe.gd` checks engine registration, typed method dispatch, distinct
instances and capability return values. The `--only-registration` and
`--skip-api` user arguments isolate shutdown regressions.

`fixtures/campaign_mod` is a developer-only, deliberately failing integration
fixture. It is **not installed as a gameplay mod**. To repeat the test, explicitly
copy it into a new `mods/native_adapter_validation` folder when that folder does
not exist, run a fresh **unsaved** Main scene and start the clock at a 24-hour step.
Its load hook toggles aviation, checks it and restores the original value. It
checks the campaign-created hook, acknowledges the first daily call and returns
false on the second. The expected result is one loader error, no later calls from
that fixture, and a game which continues normally and exits cleanly. Move the
copied fixture out of `mods/` afterwards; do not leave it enabled in real games.

The main adapter template under `mods/_templates/luau_engine` does not deliberately
fail and remains disabled until the author copies and enables it.
