# Content packs

A downloadable content pack is a mod that ships with the game.

That is not a simplification — it is the design. There is one content pipeline in Deal or
Force, and a pack goes through it: the same folders, the same "later file wins" rule, the
same loader. What separates a pack from a mod is provenance and permission, not machinery.

    dlc/
      entitlements.json          what the player owns
      my_pack/
        dlc.json                 what the pack says about itself
        common/                  exactly the folders a mod uses
        history/
        gfx/
        map_data/
        localization/

The repository ships the framework and no pack: a pack is content somebody sells, and the
source is not where it lives. Pack folders are ignored by git for that reason, so one
developed here stays here.

## Load order

    base game  →  content packs  →  mods

A pack overrides the base game. A mod overrides a pack. That order is deliberate: the
player owns both, and the one they installed by hand is the one they meant.

Among packs, `load_order` decides; ties break on id, so the order never depends on how the
filesystem happened to list the folders.

## dlc.json

    {
      "id": "my_pack",
      "name": "My pack",
      "version": "1.0.0",
      "description": "…",
      "entitlement": "my_pack",
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

    { "owned": [ "my_pack" ], "unlock_everything": false }

`dlc/entitlements.json` ships with the game; `user://entitlements.json` is the player's
own, and is read second so that a real purchase always beats whatever a development build
left lying about. `unlock_everything` treats every pack on disk as owned — the switch for
testing a pack before there is anything to buy it with, and wrong in a shipped build.

## Proving the gate

`Tools/SimBench` checks both halves of a pack: locked, its decisions are absent from the
game; unlocked, they load and are there. It checks every pack installed here, and where
there is none it writes itself a small gated pack in the temp folder and checks that, so
the framework is proven whether or not anything is installed.
