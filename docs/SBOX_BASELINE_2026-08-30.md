# s&box baseline for migration

Captured on 2026-08-30 through the live s&box MCP bridge.  This is the reproducible
reference state for every Godot porting milestone; it is not a claim that the Godot
build has reached feature parity yet.

## Scenario

- Scene: `Assets/scenes/minimal.scene`
- Seed: `20260830`
- Player selected after generation: `KOR` (South Korea)
- Date: 1 January 2024

## Initial world fingerprint

```
 day=0 date=1 January 2024 player=KOR treasury=184799700000.0
 divisions=33.3 tech=12 armies=401 fleets=108 wars=1 relations=770
 checksum=11826053123551516313
 firstArmy=id=0 province=6209 div=1.7
 firstWar=id=0 score=0.0
```

The original scenario loader is data-driven and may intentionally reuse a baked
scenario rather than regenerate geography for the numeric seed.  Therefore the
fingerprint, rather than the seed alone, is the correct acceptance target.

## Validation

The source campaign's in-game `test` command completed with **33 of 33 checks
passed**.  Coverage includes controllers, economy finite values, initial war/fronts,
army supply, naval placement, capitals, orders, war-score bounds, peace terms and
content loading.

## Porting rule

Every Godot simulation layer must preserve deterministic inputs, execute on a fixed
simulation clock, and have an automated comparison point before it replaces a source
mechanic.  Visual/map rendering may differ; simulation facts and player-visible
results may not drift silently.
