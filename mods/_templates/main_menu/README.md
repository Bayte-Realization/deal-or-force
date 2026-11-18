# Main menu template / Шаблон главного меню

Changes the title backdrop art, logo banner tint, menu music, palette accents, and menu
copy right at startup via `ModLoader.ApplyPresentation()` (before any campaign begins).

1. Copy this folder out of `_templates`, choose a unique `id` in `manifest.json`, and
   set `"enabled": true`.
2. Replace `menu_world` or `brand_logo_banner` in `gfx/50_menu_art.json` (or drop your
   own `gfx/interface/brand/menu_world.png` beside it).
3. `music/50_menu_music.json` swaps the `"when": "menu"` rotation.
4. `interface/50_menu_theme.json` and `localization/*/50_menu.json` adjust menu palette
   tokens and localized copy without loading campaign history.
