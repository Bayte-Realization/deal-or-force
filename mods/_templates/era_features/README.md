# Era-dependent mechanics

Copy this directory outside `_templates`, assign a unique id and enable its manifest.
Choose an early scenario with its own `ScenarioRules.StartYear`; this template does not
rewrite the date, geography or economy of 2024 into a medieval world.
In a 2024 campaign its 1903 aviation rule passes on the first running tick: this is intentional.

`common/defines` merges `Features` and `FeatureUnlocks` by key. Aviation, drones and air
defence start off; artillery remains on. Remove the aviation unlock rule (set it to `""`)
for a permanently aircraft-free scenario. `long_range_forces: false` disables every support arm.

A named trigger unlocks a feature globally when any country matches, once per campaign day.
No random event roll is involved. Add `tags` to restrict the country whose development matters;
`min_technology.<id>: 1` checks a researched technology. The extra experimental trigger is
illustrative and is not assigned to `FeatureUnlocks` in this example.

Events/decisions can use `{ "effects": { "use": "open_aviation" } }` or directly
`{ "effects": { "feature.aviation_set": 1 } }`. Zero disables; one enables.
`{ "trigger": { "max_feature.aviation": 0 } }` tests whether it is off.
An automatic unlock rule that still matches can re-enable a manually disabled feature next day;
remove the rule when manual scripts must be its only authority.

For a modded support type add `"required_feature": "experimental_air"` in `common/support`.
This is an additional gate, not a replacement for its built-in arm/master switches.
For an entirely new scripted mechanic check `api.feature_enabled("my_mechanic")` before
running it and call `api.set_feature("my_mechanic", true)` when its own conditions pass.
C# corecode modules use `Campaign.FeatureEnabled` / `SetFeature`.
New custom feature names do not automatically switch off unrelated C# systems.

Disabled formations and training remain in saves, but disappear from roster/map/reach,
do not fight, train, recover or receive AI orders, and cannot receive new missions/rebases.
They resume from their saved condition when enabled; no retroactive training is awarded.
Newly enabled mechanics do not conjure free units: recruit them or provide scenario history.
Feature defaults, dynamic overrides and unlock rules travel in saves and network snapshots.
The developer console command `feature aviation off` is single-player-only; `feature` lists switches.
