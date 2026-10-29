<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
# Production pure rules (F#)

Shared managed library for the net10 Godot game and net8 simulation tools.
Referenced through `Simulation/StructuredData.Dependencies.props`.
FSharp.Core is pinned to 10.1.402. Players need no F# SDK/compiler.

C# `Simulation.AssaultReadiness` retains the public game/mod API and passes only
primitive values into `Rules.AssaultRules`. Its struct result maps back to the
existing C# readout. No Godot, mutable world, IO, clock, RNG, save or command
authority crosses this boundary. Scenes still use C#/GDScript: not Attach Script.

`CatalogueWindow.Plan` supplies an overflow-safe value-only page slice for the
retained decisions/promises/goals catalogue. It receives only counts, never
controls or campaign objects. `--agenda-only` covers 729 integer boundary cases,
zero-allocation warmed calls, translated search and read-only world invariants.
This is a clarity/reuse boundary, not a claim that F# makes UI rendering faster.

This migration isolates readable rules; it does not claim F# arithmetic is
faster than C#. The allocation improvements in this pass are separate C#
catalogue/traversal changes. Rust game migration is paused by the author.

`PeaceRules.StalemateDiscount` is the newest tenant and the pattern for the
next ones: how much a long, stalling or exhausting war discounts a winner's
resolve over an achievable aim. It is five numbers in and one out - days since
the war began, whether the term is a return to the status quo, whether the front
has stalled, how weary both sides are, how much of the responder's ground is
held - and the surrounding `PeaceNegotiation.WarAimResolve` still decides
*whether* the penalty applies, because that needs the campaign.
`PeaceRulesHook.InstallStalemate` wires it, `PeaceNegotiation.ManagedStalemateDiscount`
stays as the fallback when the F# library did not load, and `--peace-stalemate-only`
compares the two on 4 160 cases and requires the exact float, plus eight checks
that the branches do what their sentences say. A port that cannot drift is the
only port worth making.

## Verify from the repository root

```powershell
dotnet run -c Release --project Tools/GodotLanguages/experiments/assault-rules/Harness/AssaultRulesHarness.csproj
powershell -ExecutionPolicy Bypass -File Tools/dev.ps1 -Test -TestArgs '--core-refactor-only'
powershell -ExecutionPolicy Bypass -File Tools/dev.ps1 -Build -Test
powershell -ExecutionPolicy Bypass -File Tools/dev.ps1 -Game
```

The differential harness compares the real F# method AND the actual C# adapter
against a frozen C# oracle from ea4510cf (624,502 boundary/fuzz cases).
Do not modify the oracle just to make a regression pass.

`bin/`, `obj/` and DLLs are ignored build products. ProjectReference carries
DealOrForce.Rules.dll and FSharp.Core.dll into managed publishing. A source probe
does not prove standalone export: run Tools/build.ps1 with matching .NET export
templates. The dependency's MIT notice ships in game/THIRD_PARTY_NOTICES.md.
