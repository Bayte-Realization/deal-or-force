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
