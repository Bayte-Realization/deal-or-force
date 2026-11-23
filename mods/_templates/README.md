# Deal or Force — Mod & Scenario Templates / Шаблоны модов и сценариев

Folders inside `mods/_templates/` are skipped automatically by `ModLoader` (because `_templates` starts with `_`).
Copy any template into `mods/<your_mod_id>/` (or `Documents/Bayte Realization/Deal or Force/mods/<your_mod_id>/`),
set a unique lowercase `"id"` in `manifest.json`, and change `"enabled": true`.

---

## Multi-Era & Total-Conversion Scenario Starters (Start Here!)

| Template | Setting | Highlights |
| --- | --- | --- |
| **`historical_1914_era/`** | Historical Earth (`1914-07-28`) | Uses `"base_world": true` (no 20 MB world file needed), custom era label `"Great War Era"`, `replace_paths` for 2024 tech/events/decisions, disables `nukes`/`drones`/`missiles`, restricts `aviation` until `reconnaissance_biplanes` is researched, seeds Triple Entente, Central Powers & Great War opening, full EN+RU loc. |
| **`chronicles_of_aethelgard/`** | Fantasy Total Conversion (`Year 402 of the Third Sun`) | `total_conversion: true`, `replace_paths` replacing 2024 tech/events/decisions/ideologies/governments, custom 12-month fantasy calendar (`Frostfall`..`Starquiet`), self-contained 3-realm `world.bdfsave`, custom goods/ideologies/governments/techs/decisions/events, parchment era skin, full EN+RU loc. |
| **`scenario_minimal/`** | Minimal Sandbox (`1995`) | Smallest possible standalone `scenario.json` + single-province `world.bdfsave`. |
| **`bce_calendar/`** | Ancient BCE Reference (`3500 BCE`) | Astronomical vs civil BCE date conventions (`game/documentation/calendar_eras.md`). |
| **`era_features/`** | Era Mechanic Switches | Dynamic `Features`, `FeatureUnlocks`, `FeatureDefines`, and scripted triggers. |
| **`nuclear_age/`** | Phased Nuclear Ladder | Step-by-step unlocking of `defcon`, `nuclear_posture`, and `nuclear_strikes`. |
| **`era_skin/`** | Era Visual Overlay | `skins/<id>/` palette and icon-category overrides without touching simulation. |

---

## Focused System Templates

- **Data & YAML**: `yaml-data/`, `yaml_example/`, `balance_and_script/`, `politics/`, `budget_and_taxes/`, `central_bank/`, `cabinet_and_trade/`, `trade_instructions/`, `production_chains/`, `production-programme/`, `statecraft/`, `long_range_forces/`, `cities_and_militia/`, `private_military_company/`, `recruitment_workbench/`, `regional_census/`, `regional_civic_voices/`
- **Map & Identity**: `border_redraw/`, `map_data_override/`, `rename_the_world/`, `flag_designs/`, `historical_flags/`, `translation/`
- **UI & Audio**: `interface_reskin/`, `icons_and_buttons/`, `fonts/`, `main_menu/`, `sounds/`, `soundtrack-data/`
- **Code & Workers**: `csharp_mod/`, `fsharp_mod/`, `luau_engine/`, `luau_worker/`, `go_worker/`, `rust_worker/`, `kotlin_worker/`

Full Russian guide for building Historical, Future/Sci-Fi, Alt-History, and Fantasy scenarios in 10 minutes:
**[`docs/MODDING_SCENARIOS_GUIDE_RU.md`](../../docs/MODDING_SCENARIOS_GUIDE_RU.md)**
