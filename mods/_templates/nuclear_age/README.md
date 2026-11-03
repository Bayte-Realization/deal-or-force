# The nuclear age, opened step by step

Copy this directory outside `_templates`, give it a unique id and enable its manifest.

The nuclear mechanics are three switches in `Features`, each independent of the others:

| Feature | What it governs |
| --- | --- |
| `defcon` | The world's alert ladder: the DEFCON badge, its hotspots, upkeep and trade effects. Off hides the badge. |
| `nuclear_posture` | What a holder does short of use: raising and lowering its alert, tests, the red telephone, coercion of its enemies, incidents at the brink, the umbrella over its allies. Needs `defcon`. |
| `nuclear_strikes` | The last resort and the answer to it (the second strike). Needs `nuclear_posture`. |

The define `DefconEnabled` stays the master switch over all three.

Ways to open or close them:

- At the start: `Features` in `common/defines` (this template starts all three closed).
- Automatically: `FeatureUnlocks` maps a feature to a named trigger in
  `common/scripted_triggers`. Once a day, if any country matches, the feature opens for the
  whole world. `min_technology.<id>: 1` tests a researched technology, `min_year` the date,
  `tags` a particular country, `max_feature.<id>: 0` whether another switch is still closed,
  `min_nuclear_tier: 1` a country holding a deterrent, `max_nuclear_posture: 2` one at
  DEFCON 2 or worse, `max_defcon: 2` the world at the brink.
- From content: an event, decision or national course effect
  `{ "feature.nuclear_strikes_set": 1 }` opens it, `0` closes it. `scripted_effects` here has
  three ready-made blocks (`open_nuclear_use`, `ban_nuclear_use`, `close_nuclear_age`).
- From script: `api.set_feature("nuclear_strikes", true)` (Luau/GDScript/worker mods),
  `Campaign.SetFeature` from C#.
- From the developer console: `feature nuclear_strikes off` (single player).

An automatic rule that still matches reopens a feature the next day: leave it empty (`""`)
when an event must be the only authority, as `nuclear_strikes` is here.

Closing a feature suspends it; it does not erase it. Each holder's alert, its last test and
strike stay in the save and resume when the feature reopens.

Who holds a deterrent is not a switch: it comes from `common/deterrence` (the holders of the
scenario's start) and from any technology whose effects give `deterrence` (see
`common/technologies/70_deterrence.json`).
