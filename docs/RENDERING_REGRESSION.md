# Country-label resource regression

## Reproduction

Use a disposable vanilla `Scenes/Main.tscn` session on Windows / Forward+ / D3D12.
Wait for the authored map to finish loading. Do not run these probes in a player's
campaign or another developer's runtime session.

1. Open the visual layout editor. Capture its normal appearance.
2. Close it, change language, and reopen it without an extra frame between actions.
3. Resize the window to 1280×720 and capture it.
4. In the same frame, restore the original window size, close the editor and change
   language back. Wait at least 15 seconds and inspect the **full runtime log**.
5. Also test a wider visible map: focus the globe near 52° N / 32° E at distance 10,
   switch to the flat map and focus the same location at equivalent distance 14.
   Switch language in both projections. Repeat the editor/window/language sequence.

On 2026-09-23 the original sequence reproduced
`not enough room in the RESOURCES descriptor heap` at the default 65,536 descriptors.
Slow, separately spaced language and resize tests did not reproduce it. Reusing the
country nodes fixed the first sequence, but a subsequent wider flat-map/language
test still reproduced exhaustion; node reuse alone was not sufficient evidence of
a complete fix.

## Implementation and checks

- Keep country groups keyed by map index and reuse the existing glyph slots. Remove
  excess glyphs and groups for countries that no longer have a drawable name.
- Reapply projection and visibility immediately after placement; a refresh can happen
  after the normal per-frame pass.
- Cache fonts by catalog role instead of constructing another `SystemFont` for every
  language switch. Invalidate when `FontCatalog.Changed` fires. Do not dispose a font
  while live controls or labels can still reference it.
- `CycleLanguage` uses the one map-name refresh already inside `RefreshLocalisedText`.
- Reserve 131,072 D3D12 resource descriptors instead of 65,536. The wide-map projection
  plus language transition still exhausted the original limit with both reuse fixes,
  even though repeated tests returned to the same node count and video-memory plateau.
  The rendering driver is unchanged. This is additional transient capacity, not a
  claim that an arbitrarily large mod can fit the heap.

Check actual country text before/after a language roundtrip, retained glyph instance
IDs, stable font instance IDs, correct flat/globe positioning, and bounded resource
counts. Script completion is **not** a renderer-health verdict: inspect errors emitted
after the last capture, too. In the local Fennara workspace, the reusable probe is
`.fennara/scripts/runtime/country_label_resources_v1.gd` (private test scratch, not
shipped game content). It changes no save, mod or preference files.

## Verified result (2026-09-23)

Godot 4.7.2 mono, Windows, Forward+ / D3D12, RTX 3060, vanilla content:

- The current build passed ten editor/window/language cycles (20 language changes),
  then ten exact globe-distance-10 → flat-distance-14 + same-frame language changes
  and language roundtrips (another 20 changes), in the same runtime session.
- Country words returned exactly to their original values; 1,491 of 1,575 original
  glyph objects survived (surplus slots are removed when translated names shorten),
  and the map font instance stayed the same.
- Settled node count was 8,198 throughout. Reported video memory reached 2,635.695 MiB
  on cycle two and stayed there through the final projection loop.
- Full session log had no engine errors or descriptor-exhaustion messages, including
  the final eight-second observation after the last transition. Initial editor input
  probe assertions compared JSON floats to integer arrays and falsely failed; after
  correcting the private probe, all nine editor input checks passed. These were test
  assertion errors, not rendering errors.
- English and Russian editor appearances were inspected, including 1280×720. C#
  project diagnostics reported zero errors/warnings; `FormableTests` passed.

Local evidence: runtime `runtime-1790195549387-2098`, tool log directory
`D__Godot_Projects_by-deal-or-force__72604/results/tool_2095_runtime_session`.
This establishes the two reproduced regressions on this configuration, not
unbounded resource use or compatibility with every GPU, driver, renderer or mod.
