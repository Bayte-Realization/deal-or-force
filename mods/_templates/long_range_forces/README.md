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

## How they move and where they stand

Since 17 November 2026 (`Scripts/Simulation/LongRangeMovement.cs`) a formation stands in a
province and gets anywhere by travelling - nothing jumps:

- `movement`: `land` drives by road at a division's march pace, by train inland where neither end
  of a step touches the enemy, and by ship between two ports of one sea where there is no road;
  `air` flies straight ferry legs. Left out, aircraft (`kind: air`) fly and everything else drives.
- `speed`: pace against a division's march, `1.0` by default (the stock towed artillery and SAM
  brigades are `0.8`).
- `deploy_days`: days between arriving and working (`1`; SAM brigades `2`).
- `ferry_km`: the longest single flight. Left out it is `LongRangeFerryRangeFactor` × `range_km`
  within `LongRangeFerryMinKm`..`LongRangeFerryMaxKm`.
- `basing`: what the formation takes of a region's basing capacity (`1`; aircraft `2`; strategic
  bombers `3`). A region holds `LongRangeBasingBase` + `LongRangeBasingPerBarracks` per barracks +
  `LongRangeBasingPerCity` per leading city its owner holds. Going over is allowed: readiness in
  that region recovers at capacity / used.

The numbers - sea and air pace, port days, the overrun loss, the reach slack, basing, the
training queue, the computer's review - are in `common/defines/108_long_range_movement.json`,
each with its reason. A province the enemy takes costs the formation standing there
`LongRangeOverrunLoss` of its strength once; it then withdraws by road or flies out, and with no
way out it is cut off where it stands.

## Operational interface

Long-range formations are selected and ordered on the map the way divisions are, panel or no panel:

- left click on a card selects it; Shift+click adds; a box drawn on the map takes divisions and
  formations together; Esc or a left click on empty ground lets go of the selection;
- right click on any province they may stand in sends each selected formation there by its own
  way; Shift+right click adds a waypoint after the route it already has. Hovering a province with
  formations selected shows the days, the way (road, rail, sea, air), the basing on arrival, or
  why not;
- with only formations selected and the panel closed, their own bar stands where the army bar
  stands: status, missions, HALT, SHOW, PANEL.

The panel is the roster, raising and missions. Each formation is judged on its own - one that
cannot go does not hold back the rest, and the feedback names who stayed and why. Rows read
"en route → place, N d", "deploying", or "CUT OFF"; region captions read "used X of Y".

Map cards show readiness and mission, or the days left on the road. A moving card is drawn
part-way along the step it is making and its route is dashed in its arm's colour. Reach is a
band with a faint selected-area fill; standby reach is dashed. Symbols come from the support
type's `icon` through the sprite catalogue.

Override `gui/34_support_map.json` to tune `support.counter_scale`, `support.selected_scale`,
`support.stack_spacing_x/y`, pick half-width/height, reach band widths (kilometres), fill alpha,
`support.roster_row_height`, `support.recruitment_height`, arm colours and visibility distances
(`support.marker_distance` is the divisions' zoom, 10.8).
`localization/<language>/zz_430_long_range.json` and `130_support_controls.json` contain the
control and status captions.
