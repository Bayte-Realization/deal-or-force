# Content packs

A downloadable content pack is a mod that ships with the game.

That is not a simplification — it is the design. There is one content pipeline in Deal or
Force, and a pack goes through it: the same folders, the same "later file wins" rule, the
same loader. What separates a pack from a mod is provenance and permission, not machinery.

    dlc/
      entitlements.json          what the player owns
      dof_iron_and_grain/
        dlc.json                 what the pack says about itself
        common/                  exactly the folders a mod uses
        history/
        gfx/
        map_data/
        localization/

## Load order

    base game  →  content packs  →  mods

A pack overrides the base game. A mod overrides a pack. That order is deliberate: the
player owns both, and the one they installed by hand is the one they meant.

Among packs, `load_order` decides; ties break on id, so the order never depends on how the
filesystem happened to list the folders.

## dlc.json

    {
      "id": "dof_iron_and_grain",
      "name": "Iron and Grain",
      "version": "1.0.0",
      "description": "…",
      "entitlement": "dof_iron_and_grain",
      "load_order": 100,
      "requires": [],
      "affects_rules": true
    }

`entitlement` is what the player has to own. Leave it out and the pack is free with the
base game — which is what a patch pack, a bundled scenario or a translation usually is.

`requires` names other packs this one is written against. A pack whose requirements are
missing is listed and not loaded, rather than half-loaded: half a pack is worse than none.

`affects_rules` says whether owning it changes the simulation or only how it looks. It
decides one thing that matters: two players may sit down together with different cosmetic
packs and may not with different rule packs, and that has to be visible before the game
starts rather than discovered when it desynchronises.

## entitlements.json

There is no store yet, and writing a fake one would be worse than admitting that. What
exists is the shape a store plugs into: a list of what the player owns.

    { "owned": [ "dof_iron_and_grain" ], "unlock_everything": false }

`dlc/entitlements.json` ships with the game; `user://entitlements.json` is the player's
own, and is read second so that a real purchase always beats whatever a development build
left lying about. `unlock_everything` treats every pack on disk as owned — the switch for
testing a pack before there is anything to buy it with, and wrong in a shipped build.

## Iron and Grain

The pack in this folder is a worked example rather than a placeholder: three decisions, the
triggers and AI scores they need, a reason countries remember each other by, and its own
localisation in two languages. It is deliberately **not** owned in the shipped
`entitlements.json`, so the base game is exactly what it was — which also means the gate is
doing something you can see. Add its id to `owned`, or set `unlock_everything`, and the three
decisions appear in the decisions panel.

`Tools/SimBench` checks both halves of that: locked, the decisions are absent; unlocked,
they are there and the AI will take them.

The third decision, `break_the_bank`, is also this pack's job as a test. It is the only
content in the repository that reads and writes the monetary model from data — `inflation`,
`key_rate` and `government_sets_key_rate` are ordinary scripted fields, so a condition about
money is written exactly like one about bread:

```json
"prices_climbing": { "min_inflation": 0.06, "min_key_rate": 0.03, "max_government_sets_key_rate": 0.5 }
```

```json
"effects": { "government_sets_key_rate_set": 1.0, "key_rate_set": 0.012 }
```

If those three ever stop being scripted fields, this pack stops loading and says which one
went.
