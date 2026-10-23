# Campaign hint art

Original generated atlas: 4 columns × 6 rows, 256 px cells, true PNG alpha.
Provenance and full prompt: `docs/art/campaign-hints-2026-10-20.json`.

The renderer measures visible alpha in each cell once, fits the visible rectangle
inside a 29 px square and centres it independently of transparent padding.
All 24 views share one atlas; source CPU pixels are released after loading.
The original PNG is not rewritten or destructively cropped.

Rows: four category medallions; money/food/materials/energy;
ammunition/copper/lithium/sulfur; research/elections/decisions/personnel;
construction/supply/diplomacy/organisations; war/battle/crisis/events.

Mods may replace `hints_atlas` (keep the 4×6 layout), or declare an individual
`hint_NAME` sprite, for example `hint_good_copper` or `hint_research`, through
the ordinary gfx sprite catalogue. Individual replacements are alpha-centred too.
