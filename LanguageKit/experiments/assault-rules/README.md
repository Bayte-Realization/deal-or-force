<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Tools-only F# assault eligibility experiment

This is an optional pure-rules readability experiment, not a game backend or a performance claim.
The original C# `AssaultReadiness.Assess` remains authoritative. No production project references
this folder, and no DLL is installed into the game.

`Rules/AssaultRules.fs` takes only `bool`, `int32` and `float32` arguments and returns an immutable
value record. It has no Godot, campaign, filesystem, clock or RNG dependency. The C# harness
compiles the actual `Scripts/Simulation/**/*.cs`, including `AssaultReadiness.cs` and
`BattleReadout.cs`, as the existing ArmyInteractionTests do. It does not carry a copied reference
assessor. Only its fixture uses reflection to set the campaign clock for extreme signed day values.

The preserved priority is no assault order, paused plan, recovery interval, invalid condition,
direct order, organisation floor, supply floor, ready. The readout uses finite/clamped float32
values, nonfinite floors use the existing defaults, zero disables a floor, comparisons include
equality, and recovery subtracts widened int64 values before saturation. Every result field is
compared; float fields must match bit for bit, including signed zero behavior.

From the repository root, with a .NET SDK providing F# and a .NET 8 runtime installed
(tested SDK 10.0.402; other SDK versions have not been exercised here):

```powershell
dotnet run -c Release --project Tools/GodotLanguages/experiments/assault-rules/Harness/AssaultRulesHarness.csproj
```

All generated `bin/` and `obj/` files stay below this experiment and are ignored. Package restore
uses the existing simulation's YamlDotNet dependency plus FSharp.Core. A first restore may need
network access. The harness and rules target net8.0 because the existing simulation tools target
net8.0; the actual Godot game targets net10.0. No game-framework retargeting is performed.

## Coverage and recorded result

Validated on 2026-10-16, Windows x64, .NET SDK 10.0.402, .NET 8.0.32 runtime.
Resolved dependencies: FSharp.Core 10.1.402 and YamlDotNet 18.1.0. The F# compiler/package
default can change with the selected SDK; these are the versions actually exercised.

| Check | Differential cases |
| --- | ---: |
| Explicit reason/threshold/overflow expectations | 17 |
| 22 organisation values × 22 supply values × 22 values for each floor × 2 direct-order settings | 468,512 |
| All 5 current orders plus 2 invalid orders × plan/direct flags × 4 recovery settings × 22 values for both conditions | 54,208 |
| All pairs of 9 signed day boundaries × 3 orders × plan/direct flags | 972 |
| All context-presence combinations × 9 ready-day boundaries × 22 conditions | 792 |
| Deterministic raw float/int bit patterns, xorshift seed `0xA55A2026` | 100,000 |
| Read-only fixture setup | 1 |
| **Total** | **624,502** |

Boundary values include infinities, several NaN payloads, signed zero, subnormal epsilon,
out-of-range finite values, and the immediate float32 neighbors of .16, .20 and 1. Every
differential case also checks the input fields, campaign day and RNG for mutation. A separate
4,096-call-per-assessor loop checks campaign fingerprint, serialized army, exact clock,
thresholds and RNG remain unchanged.

The recorded execution exited 0:

```text
PASS state/RNG unchanged: 4096 repeated calls per assessor; fingerprint, army, clock and floors unchanged
ASSAULT_RULES_EXPERIMENT_PASS cases=624502 failures=0 random_seed=0xA55A2026 random_cases=100000
```

The C# source files exercised had these SHA-256 values (the harness always compiles the current
files, so it detects subsequent semantic changes instead of freezing a copied C# implementation):

```text
Scripts/Simulation/AssaultReadiness.cs D50F805D712E412BFEC603A719D9134D9E052CB32545CB3F8A91C161605DA5A0
Scripts/Simulation/BattleReadout.cs   76D70F9A8FFDC91D2A30B782ACAFB7A0145FF812066B85F2D70A24883078B500
```

The finite test matrix exhausts the selected boundary sets, not all 2^32 float patterns or every
possible campaign. Deterministic raw-bit fuzzing extends that set. This does not validate Godot
export, save/replay/network integration, performance, or F# as an attachable scene script.

## Deployment and license

The harness output includes `FSharp.Core.dll`; a future accepted F# game integration would need
that managed dependency shipped with the matching .NET host and verified in an exported build.
Players would not need an F# compiler/SDK, Nim, Java, or an additional F# runtime installer.
This experiment does not add that dependency to ordinary players' builds.

New source and project files in this folder are GPL-3.0-or-later, as marked by their SPDX headers;
see the repository-root `LICENSE`. FSharp.Core and YamlDotNet retain their upstream licenses.
