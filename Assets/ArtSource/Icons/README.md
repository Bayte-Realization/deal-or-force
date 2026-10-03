# Command and strategic-goods PNGs

Original artwork generated with the built-in `image_gen` tool on 2026-10-04.
These are newly generated game assets, not cut-outs from the user's Paradox screenshots.
The screenshots informed the interaction/readability goal, not the artwork.

## Art direction / reusable briefs

- `army_commands.png`: a transparent 4 × 4 icon sheet; sixteen isolated, centred
  grand-strategy command pictograms. Brass gold and ivory, a dark contour, strong
  silhouettes readable at 18–22 px, generous transparent gutters, no captions,
  lettering, frames or scenery. In row-major order: halt palm; barricade shield;
  three forward arrows; spear breaking a line; reserve formations behind a shield;
  folded map with pause; folded map with play; formations deploying along a front;
  bent toothed front; curved attack arrow; fallback barrier with return arrow;
  crossed-out stacked orders; eraser; retreat arrow with broken crossed swords;
  turtle/shield caution; lightning/forward-arrow aggression.
- `strategic_goods.png`: matching transparent 4 × 4 commodity pictogram sheet;
  each silhouette distinct without relying on colour, no words or scenery. In
  row-major order: copper coil; lithium crystal/battery; nickel ingot; cobalt ore;
  phosphate mineral/sprout; silica quartz; rubber tyre; sulfur mineral/drop;
  steel I-beams; batteries; semiconductor chip; motor vehicle/truck; ammunition
  shells; precision gear/sensor; arms/rifle+wrench; supply crate/arrow.

These briefs describe the generated sets; they are not a promise that another
generation will reproduce identical pixels. The committed atlases are the exact
source of the shipped rasters. `*.mapping.json` records their cell names.

## Mechanical export

Run from the repository root with **Windows PowerShell 5.1** (System.Drawing):

```powershell
powershell -NoProfile -File Tools/IconCompiler/export-atlas.ps1 -Atlas Assets/ArtSource/Icons/army_commands.png -Mapping Assets/ArtSource/Icons/army_commands.mapping.json -OutputDirectory game/gfx/interface/icons
powershell -NoProfile -File Tools/IconCompiler/export-atlas.ps1 -Atlas Assets/ArtSource/Icons/strategic_goods.png -Mapping Assets/ArtSource/Icons/strategic_goods.mapping.json -OutputDirectory game/gfx/interface/icons
```

The exporter divides proportional cells even when the sheet dimensions are not
multiples of four, crops alpha bounds, and fits each into an 84 px footprint on a
transparent **96 × 96 PNG**. It invents no artwork. Let Godot import changed/new
PNGs before launching or exporting; do not hand-edit generated `.import` files.

## Modding

The game uses **individual PNGs**, not the source sheets. Replace
`gfx/interface/icons/icon_plan_erase.png`, for example, in a mod to change that
symbol independently. Existing sprite catalog overrides still work.

Commodity icon names follow the stable data ID, lowercased:
`Ammunition` → `icon_good_ammunition.png`. A mod-added `TestCrystalFuel` uses
`icon_good_testcrystalfuel.png`, not its runtime enum-slot number. All 34 shipped
goods have PNGs. `icon_military_supply.png` is an extra asset available to mods;
it is not claimed as a new mechanic or a new shipped toolbar action.

`Assets/ArtSource/.gdignore` keeps these working sheets out of Godot's runtime
resource scan/export. Only the individual files under `game/gfx/interface/icons`
are game assets; source-sheet `.import` files are not needed in Git.
