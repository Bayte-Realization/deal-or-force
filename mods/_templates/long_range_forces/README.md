# Long-range forces template

Artillery, aircraft, drones and air defence are data: every kind of long-range formation is an
entry in `common/support`, and the panel (the aircraft button on the left of the map), the AI
and the battles read them from there.

1. Copy this directory out of `_templates`, give the folder and the manifest a unique id and
   set `"enabled": true`.
2. `common/support/50_loitering_munitions.json` adds a kind and changes another:
   - a **new kind** lists every field it needs - anything left out is zero;
   - a **kind the game already has** lists only what changes: `{ "mlrs_battalion": { "range_km": 120 } }`.
3. Name the new kind in every language you ship - `localization/<language>/` - keyed by its
   English `"name"`.

What each field does is written at the top of the base game's
`common/support/00_support_types.json`. The air war itself - how much a formation loses flying
over a defended front, how quickly it recovers resting - is in `common/defines`
(`SupportAttritionPerDay`, `SupportAttritionSaturation`, `SupportAttritionFloor`).

`kind` decides which rules apply: `ground` guns, crewed `air`craft, `drone`s, or
`air_defence` that covers its own ground whatever it was told. `missions` are what it may be
ordered to do; `standby` - resting - is always there.

## Operational interface

The panel keeps one roster grouped by base and one shared command bar above it:
click selects a formation, Ctrl-click toggles it, Shift-click selects a contiguous range
of visible rows. Drag a rectangle on the map to select several counters (Ctrl/Shift adds).
Right-click a friendly region to rebase the selection, or arm **REBASE ON MAP** and left-click.
Esc cancels an armed rebase before closing the panel. Ordinary map clicks while this panel
is open do not issue division orders or open diplomacy.

Rebasing is all-or-nothing: every selected formation must still belong to the issuer, the
region must have a controlled base province, and the whole group must fit. Training orders
reserve posts too. Formations already at the destination do not consume an extra post.
The feedback reports required/free/reserved posts and does not silently split a group.

Mission buttons show compatible/selected counts. A mixed group changes only the formations
which support that mission; the report says how many were ordered. These missions apply to
battles inside the actual base's radius, not to a separate air-region assignment system.

Map cards show readiness and mission, with an arm-coloured stripe and a gold selection frame.
Cards at one base are separated in screen space: this never changes the geographic base or
range. Reach is a band with a faint selected-area fill; standby reach is dashed. Symbols come
from the support type's `icon` through the sprite catalogue.

Override `gui/34_support_map.json` to tune `support.counter_scale`, `support.selected_scale`,
`support.stack_spacing_x/y`, pick half-width/height, reach band widths (kilometres), fill alpha,
`support.roster_row_height`, `support.recruitment_height`, arm colours and visibility distances.
Below `support.compact_height`, opening recruitment temporarily hides the shared mission bar
without clearing selection. Closing recruitment restores it, keeping the roster usable.
`localization/<language>/130_support_controls.json` contains the new control/status captions.
