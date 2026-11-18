# Icons and buttons template / Шаблон значков и кнопок

Retints icon families, reshapes button borders/corners, and swaps icon-role mappings
without editing C# or scene files.

1. Copy this folder out of `_templates`, give `manifest.json` a unique `id`, and set
   `"enabled": true`.
2. `interface/50_icons_and_buttons.json` sets `icon.category.*` palette tokens, button
   fills/borders, metrics (`button.corner_radius`, `button.border_width`), and surface
   materials (`button`, `chip`).
3. `gui/50_icon_roles.json` re-assigns top-bar / rail roles (`campaign.icons` and
   `campaign.icon_categories`, matching `game/gui/30_icon_roles.json`).
4. `gfx/50_icon_tints.json` adjusts individual sprite tints without duplicating icon
   art. To ship replacement icons, drop white-mask `.png` / `.svg` files under `gfx/`.

Все настройки описаны на основе `game/interface/00_skin.json`,
`game/interface/10_materials.json` и `game/gui/30_icon_roles.json`.
