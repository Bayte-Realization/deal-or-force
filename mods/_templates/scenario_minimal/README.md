# Minimal scenario template / Шаблон минимального сценария

Demonstrates how `ScenarioLibrary` discovers a standalone scenario (`scenarios/<id>/scenario.json`
+ exported `world.dofsave`), sets the default `player_country`, and pairs optional localization
and era overlay settings.

- **Custom scenario**: place an exported full-map `.dofsave` at `scenarios/<id>/world.dofsave`
  (this starter ships a compact single-province JSON payload for `ScenarioLibrary.Create`).
- **Overriding the shipped 2024 start**: add `scenarios/2024/scenario.json` with `"id": "2024"`,
  choosing a different `"player_country"` or `"skin"` without copying `history/`.
