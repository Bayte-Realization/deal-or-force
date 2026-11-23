# Historical Era Starter: July Crisis 1914 / Шаблон исторической эпохи: 1914

Ready-to-clone historical scenario template built on top of the Earth map (`"base_world": true`)
without requiring a 20 MB exported `world.bdfsave`.

## What it demonstrates

- **Custom start date & era calendar** (`1914-07-28`, `"Great War Era"`, `"{day} {month} {year} {era_short}"`).
- **Declarative era feature gates**: disables `nukes`, `drones`, and `missiles` completely, while marking `aviation` and `air_defence` as `restricted` until any nation researches `reconnaissance_biplanes`.
- **Clean base-2024 replacement**: `replace_paths` suppresses 2024 events, technologies, and decisions; `clear_base_wars` and `clear_base_diplomacy` clear 2024 wars/blocs before seeding the Triple Entente, Central Powers, and Great War opening.
- **Zero-error validation**: passes `ModValidator.ValidateMod(...)` with 0 errors and 0 warnings, and ticks 30+ days cleanly.

## How to clone in 3 steps

1. Copy `mods/_templates/historical_1914_era` to `mods/my_1914_mod`.
2. In `manifest.json`, change `"id"` to `"my_1914_mod"` and set `"enabled": true`.
3. Customize `scenarios/great_war_1914/scenario.json`, `common/technologies/`, `events/`, and `localization/`.

---

Готовый шаблон исторического сценария на базовой карте Земли (`"base_world": true`) без тяжёлого `world.bdfsave`.
Скопируйте папку в `mods/my_1914_mod`, поменяйте `id`, поставьте `"enabled": true` и правьте даты, союзы, войны, технологии и события прямо в JSON/YAML.
