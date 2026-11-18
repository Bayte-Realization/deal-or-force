# Fonts template / Шаблон шрифтов

Overrides the system font fallback stack for UI roles (`ui.default`, `ui.display`,
`map.countries`, `map.cities`) from `game/fonts/00_fonts.json`.

To bundle a custom `.ttf` / `.otf` file with your mod (for example for another script or
writing system), place it inside `fonts/` and set `"file": "fonts/my_font.ttf"` on the
role object instead of `"system_families"`.
