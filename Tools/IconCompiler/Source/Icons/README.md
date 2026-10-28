# Command and commodity PNGs

The current shipped set is authored as simple geometry in `minimal-icons.json`
and exported to **individual transparent 96 × 96 PNGs**. Their authored palette
is dark gold `#b48732` for **all 39 goods and resources**, and `#e7bd69` for
commands and the supply mark, including when opened outside the game. No gradients, metallic
highlights, dark outlines, text, or decorative frames are baked into these icons.
Commodity icons combine slender contours and selective solid faces in one shade.
Steel and material blocks use geometry and negative space to distinguish facets.
Army commands use the
skin's `text.accent_dim`, `text.accent`, and button-state tokens; commodity marks
receive their colour from the consuming UI.

Production portraits, market rows, the economy workbench and the selected trade-good mark use
`icon.commodity` (base skin: `#b48732`). Refresh reapplies that token, including
after presentation hot reload. `minimal_palette.json` declares the authored
`source_colour`; the UI compensates for it before applying the theme, avoiding
double gold tint. Another skin can still recolour these PNGs. Steel is a faceted
I-beam with the underside of its upper flange; IndustrialMaterials uses stacked
construction blocks, not a second I-beam. Arms use a connected rifle with balanced
stock, receiver, barrel and magazine proportions. The arsenal goods are side or top
silhouettes: a tank on its tracks, a quadcopter, a swept-wing jet, a finned missile and a
towed gun on its wheel.

## Reproduce / extend the current artwork

Run from the repository root with Windows PowerShell 5.1 (System.Drawing):

```powershell
powershell -NoProfile -File Tools/IconCompiler/build-minimal-icons.ps1
powershell -NoProfile -File Tools/IconCompiler/check-minimal-icons.ps1
```

The JSON stores normalized 24 × 24 geometry. Each entry has a stable `name` and
ordered `shapes`: `polygon`, `outline` and `line` use x/y pairs; `rect`, `ellipse`
and `ring` use x/y/width/height. `curve` stores a starting x/y pair followed by
groups of three x/y pairs (two cubic Bézier control points and an endpoint).
Lines default to width 1.25 for goods and 2.4 for commands, with round caps and joins.
`outline` and `ring` stroke rather than fill. Per-shape `width` overrides this.
`cut: true`
clears a shape to transparent negative space. Optional `shade` (0.5–1) selects
a flat shade of the command palette; shipped goods use only the single base shade.
The checker rejects shaded commodity interiors. Keep every silhouette away from
the outer edge. Rendering supersamples at 384 × 384 before exporting 96 × 96;
the check decodes each PNG and rejects empty symbols, wrong-palette interiors
and clipped edges. RGB rounding on antialiased edges is permitted.

This is an asset-authoring tool, **not a runtime requirement**. The game does not
need PowerShell or source geometry to draw icons. Modders can replace them with
ordinary PNGs without using the renderer. Filename or explicit texture replacement
clears inherited `source_colour`: legacy white-mask mods still work. A gold mod PNG
can opt into compensation with a declaration such as
`{"icon_good_steel":{"source_colour":"#e7bd69"}}` in its `gfx/` JSON.
An empty reference means a white mask. Explicit references must be `#RRGGBB`
with nonzero channels. The current editable set contains 16 commands, 34 goods and one supply mark.
The authoring script accepts `-GoodsColour` and `-Colour` independently; when changing
the goods palette, also override `icon.commodity` in a mod's `interface/` file.

Let Godot finish importing changed PNGs before launching/exporting; a running
game may retain old textures. Do not hand-edit generated `.import` files.

## Historical generated artwork — not the current source

Original artwork generated with the built-in `image_gen` tool on 2026-10-04.
These are newly generated game assets, not cut-outs from the user's Paradox screenshots.
The screenshots informed the interaction/readability goal, not the artwork.

### Earlier art direction / reusable briefs

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
generation will reproduce identical pixels. The committed atlases preserve the
earlier version only. Current PNGs come from `minimal-icons.json`; subsequent AI
generation trials were rejected for alpha artefacts. `*.mapping.json` records
the historical sheets' cell names.

### Historical atlas export

**Do not run these commands to rebuild the current minimal set:** they restore
the earlier decorative version. Use `build-minimal-icons.ps1` instead.

Run from the repository root with **Windows PowerShell 5.1** (System.Drawing):

```powershell
powershell -NoProfile -File Tools/IconCompiler/export-atlas.ps1 -Atlas Tools/IconCompiler/Source/Icons/army_commands.png -Mapping Tools/IconCompiler/Source/Icons/army_commands.mapping.json -OutputDirectory game/gfx/interface/icons
powershell -NoProfile -File Tools/IconCompiler/export-atlas.ps1 -Atlas Tools/IconCompiler/Source/Icons/strategic_goods.png -Mapping Tools/IconCompiler/Source/Icons/strategic_goods.mapping.json -OutputDirectory game/gfx/interface/icons
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

`Tools/IconCompiler/Source/.gdignore` keeps these working sheets out of Godot's runtime
resource scan/export. Only the individual files under `game/gfx/interface/icons`
are game assets; source-sheet `.import` files are not needed in Git.
