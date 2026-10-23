---
name: deal-or-force-modding
description: Creating and verifying mods for Deal or Force across all supported tiers (JSON/YAML data overlays, scripted effects, GDScript entrypoints, compiled C#/F# assemblies, and Go/Rust/Luau worker mods).
allowed-tools:
  - read
  - grep
  - find_file_by_name
  - edit
  - write
  - exec
---

# Deal or Force — Modding Guide & Workflow

## Mod Locations & Structure
- Developer / built-in mods: `res://mods/<mod_id>/manifest.json`
- User mods: `user://mods/<mod_id>/manifest.json`
- Templates (ignored by loader due to `_` prefix): `mods/_templates/`

## 3 Tiers of Modding
1. **Declarative Data (JSON / YAML)**:
   - Override only the keys you need in `common/defines/`, `common/countries/`, `common/buildings/`, `common/commodities/`, `common/production_methods/`, `common/units/`, `common/ideologies/`, `common/governments/`, `common/decisions/`, `common/movements/`, `events/`, `localization/`, `gui/`, `interface/`.
   - Scripted effects use declarative suffixes: bare number (add), `_percent`, `_mult`, `_set`, and `state.` prefix for regional application.
2. **GDScript Entry (`gdscript_entry`)**:
   - Lifecycle callbacks: `on_campaign_created(api)`, `on_day_advanced(api, day)`.
3. **Compiled .NET (`corecode_assembly` in C# or F#) & External Workers (Go/Rust/Luau)**:
   - Implement `IDealOrForceMod` in `corecode/` or configure worker protocol via `WorkerModBackend`.
