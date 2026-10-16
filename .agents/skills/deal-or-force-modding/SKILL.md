---
name: deal-or-force-modding
description: >-
  Create and verify Deal or Force mods across JSON/YAML overlays, scripted
  effects, GDScript entrypoints, compiled C# or F# assemblies, and Go, Rust,
  or Luau workers. Use when the user asks for a mod, a data overlay, a new
  good, decision, event, or a template under mods/.
---

# Deal or Force — modding

## Where a mod lives

- Built-in: `res://mods/<mod_id>/manifest.json`
- User: `user://mods/<mod_id>/manifest.json`
- Examples the loader skips because of the `_` prefix: `mods/_templates/`

Override only the keys the mod names. Do not fork a whole base file to change three numbers.

## Tiers

1. **Data.** `common/`, `events/`, `localization/`, `gui/`, `interface/`. Effect suffixes: a bare number adds, `_percent` scales, `_mult` multiplies, `_set` assigns, `state.` applies the effect to every region of the country.
2. **GDScript.** `gdscript_entry` with `on_campaign_created(api)` and `on_day_advanced(api, day)`.
3. **Compiled mod.** `corecode_assembly` implements `IDealOrForceMod`. External workers go through `WorkerModBackend` and stay single-player in protocol v1.

Both English and Russian strings ship with the mod. A new effect key needs a test that the result actually happens, plus a diagnostic when the key is unknown.
