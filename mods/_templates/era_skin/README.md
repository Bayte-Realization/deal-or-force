# Era skin overlay template / Шаблон эпохального облика

Ships a selectable era skin pack under `skins/<id>/` without changing campaign simulation
or breaking save compatibility (`SkinOverlays`, `game/interface/10_materials.json`).

1. Copy this folder out of `_templates`, set a unique `id` in `manifest.json`, and enable it.
2. Select `"bronze_atlas"` via the **Era overlay** setting (`skin_overlay`), or set
   `"skin": "bronze_atlas"` in your custom `scenarios/<id>/scenario.json`.
3. Every folder inside `skins/bronze_atlas/{interface,gui,gfx}` layers in the same schema
   as top-level `interface/`, `gui/`, and `gfx/` only while this overlay is active.
