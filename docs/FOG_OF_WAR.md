# Fog of war and contacts — 7 September 2026

*Русская версия: [FOG_OF_WAR_RU.md](FOG_OF_WAR_RU.md)*

This is the current visibility model. It replaces the original rule of "you see only what is
yours".

## What is visible

- All territory a side controls, plus three neighbouring layers of provinces beyond its
  borders. Distance is counted along the province adjacency graph, not in kilometres.
- The province each of your divisions stands in, and one layer of neighbouring provinces —
  including on foreign territory.
- The site of a battle your side is in, its neighbouring provinces, and the enemy divisions
  taking part in that battle. Your own battle report shows both sides.
- As your landing fleet approaches its objective, the coastal province and its immediate
  surroundings open up. A distant landing order does not by itself reveal an island.
- Foreign divisions inside your field of view are drawn on the map but cannot be selected
  for command. Their routes and orders are not revealed. Watching does not open a foreign
  state's internal statistics.

Contacts are current, with no permanent memory. Once you leave the field of view and the
battle ends, visibility goes away unless something else supplies it. Observer mode is an
explicit exception; the pre-start country picker remains a survey of the scenario.
Visibility of foreign fleets has not yet been extended separately into full naval
reconnaissance.

## Settings for mods

File: `game/common/defines/06_visibility.json`.

| Field | Base | Meaning |
| --- | --- | --- |
| `BorderVisionDepth` | 3 | How far you see beyond controlled territory, 0–6 layers |
| `ArmyVisionDepth` | 1 | Vision around your divisions, your battle and a detected shore, 0–6 layers |
| `LandingVisionRangeDegrees` | 1.0 | The approach threshold for a landing fleet, in the naval coordinate metric used, 0.1–3 |

## Implementation and diagnostics

`CampaignIntel.Vision` is the shared contact calculation for the player and the AI. The
offensive planner accounts for the strength of the defenders it has detected, not for hidden
formations deep inside the country. Visibility is not a right of command: that is still a
separate `ArmyInteraction` check.

Results are cached per side and per campaign revision. Repeated queries do not walk the
whole map again. A separate R8 province-visibility texture is updated only when the snapshot
changes. Measurements are available in the profile under `intel.visibility` and
`f:visibility upload`.

This is an in-game limit on information, not protection of the network snapshot against
extraction: LAN synchronisation and the diagnostic logs may still contain the full state of
the world.

## Checks

- Regression tests: depth 3/4, detection by a neighbouring division, identical rules for the
  AI, loss of contact, a distant battle, the approach to an island, and the refusal to
  command a detected enemy.
- A real Godot window check: both sides of the front are visible; two deep Ukrainian groups
  stay hidden; clicking an enemy neither selects it nor drops your own selection; the shader
  mask matches the contacts. Both 2D and the globe were checked.
- Counter performance: see `FLAT_COUNTER_PERFORMANCE_2026-09-07.md`.
- Where the logs live: `LOGS.md` in the project root.
