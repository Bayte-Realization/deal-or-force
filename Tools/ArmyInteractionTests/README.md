# Army interaction regression stand

Engine-independent fixtures compile the simulation directly; no Godot editor or
running campaign is required. From the repository root:

```powershell
dotnet run --project Tools/ArmyInteractionTests -c Release -- --move-battle-preflight-only
dotnet run --project Tools/ArmyInteractionTests -c Release -- --coalition-combat-only
```

## Battle movement preflight — 20 October 2026

`MoveArmyCommand` checks access and the available land corridor before attempting
withdrawal. Refused neutral, disconnected and invalid destinations preserve the
units, combat rosters/losses, front assignments, controller map and random state.
Valid friendly retreats, enemy retargets, trapped-unit queued breakout orders and
Shift-queued waypoints remain available. The 25 assertions compare serialized
state as well as the command result: the campaign fingerprint alone intentionally
does not cover every feedback or assignment field.

Verified in an isolated Release console runner using the same simulation and test
sources, without rebuilding the game's Debug assembly or launching Godot:

| Test switch | PASS assertions | Result |
| --- | ---: | --- |
| `--move-battle-preflight-only` | 25 | Passed |
| `--coalition-combat-only` | 37 | Passed |
| `--withdrawal-only` | 26 | Passed |
| `--pocket-only` | 91 | Passed |
| `--march-continuity-only` | 12 | Passed |
| `--open-advance-only` | 31 | Passed |
| `--ai-front-recovery-only` | 33 | Passed |
| `--advance-rotation-only` | 8 | Passed |
| `--combat-attrition-only` | 10 | Passed |
| `--assault-readiness-only` | 40 | Passed |

Counts include any shared setup regression checks executed by the selected switch;
they are not counts of independent campaigns or a complete gameplay playtest.

Two existing expectations were reconciled with the actual rules, not removed:

- A retreat skips a hostile-occupied province even when its controller is marked
  friendly. The coalition fixture now expects its other safe neighbour.
- The strict assault-readiness golden is now `0xE51AA42BB91EDE15` (21 October).
  Removing only `culture_union` from `Defines.Features` and `MechanicFeatures.BuiltIns`
  restores `0x8A569EFA7BB8A70D`. Additionally removing the registered
  `CommissionNewFleetCommand` and `ConsolidateSubjectsCommand` bindings restores
  `0xDA7C6F9772777A1D` exactly. These are counterfactual diagnostics, not a list of
  interchangeable accepted values. Default assault thresholds still add no bytes.

## Partial withdrawal and ended-battle integrity — 21 October 2026

`--partial-front-only`: 54 assertions covering conserved formation splits,
automatic coverage, manual-order boundaries, safe routes, actual save roundtrip,
and repair of stale battle assignments. `--peace-battle-assignment-only`: 40
assertions using actual standing/conference peace and terrain destruction, without
a reconciliation tick masking missing immediate participant release.

Native button/resource checks and the frozen multi-seed observer are documented in
`docs/PARTIAL_FRONT_STABILITY_2026_10_21_RU.md`. They are not a claim that every
campaign, hardware configuration or crash path has been accepted.
