---
name: deal-or-force-architecture
description: >-
  The Campaign tick order, the staging seams that spread a day across frames, and the
  cache-keying rule for Deal or Force. Use when adding or reordering a simulation system,
  debugging why one system sees another's stale state, profiling a slow day, or writing a
  cache in the presentation layer.
---

# Deal or Force — architecture

Ported into the project on 16 October 2026 from `~/.config/opencode/skills`, where it was
the only copy. The tick order below was re-read from `Scripts/Simulation/SimulationCore.cs`
(`TickStages`) rather than carried over: the copy in OpenCode listed an idealised
fifteen-step order that the code does not follow, and a wrong order is worse than none when
you are deciding where a new system goes.

## The two kinds of pass

`TickStages` is an `IEnumerable<int>`; every `yield return 0` is a frame seam that
`StepFor(milliseconds)` / `RunStages` uses to spread one day across frames. That is what
PERF-01 bought — do not collapse the seams back into a straight-line call.

**Per step** (accumulate, so a six-hour step moves an army a quarter of the way):
`Commands.ApplyAll` → `EvaluateFeatureUnlocks` → `SupportCore.IndexReach` → advance
hours/date and compute `daysCrossed` → `LogisticsSystem` → `MilitaryOperations` →
`AiAdmiralty` → `NavalOperations` → `Warfare` → `Attrition` → `ConstructionSystem`.

`SupportCore.IndexReach` is rebuilt right after the commands and before the fighting, so a
client restored from a snapshot answers "who reaches this battle" the same way a host does.

**Per whole day crossed**, in this order: `RecruitmentSystem`, `Contractors`,
`SupportCore`, `MilitiaCore`, `ModifierSystem`, `SocialMemory`, `OpinionSystem`,
`AllianceMaintenance`, `EconomySystem` (staged), `BrainDrain`, then — every 7th day — the
`NumericIntegrity.FirstLiveIssue` guard, which calls `BlockSimulation` and throws
`CampaignIntegrityException` **before** political actions read thresholds, because a NaN
comparison would otherwise authorise every uprising.

Then `WarDamage`, `WarWeariness`, `CivilWarPersistence`, `Escalation` (staged), `Factions`,
`Coups`, `ProxyWars`, `InternationalOrganizations`, `OrganizationDynamics`,
`DealNegotiations`, `Ultimatum`, `ForeignAssistance`, `MovementRules`, `MovementAi`,
`MovementMethods`, `ResearchSystem`, `Shipbuilding`, `Statecraft`, `SubjectSystem`,
`AiStatecraft` (staged), `Consolidation`, `Corridors`, `WorldWar`, `IntelNetworks`,
`CovertOperations`, `AddressToNation`, `MovementPower`, `EventSystem`, `History.Capture`.

**The tail is ordered by consequence, and the comments in the code say why:**
`SecessionEvents` runs after history because it redraws the map the day was worked out on;
`CivilWarOutbreak` next; `CountryNames.Remember`; `GovernanceEvents` after secession because
a state that just lost a province is in exactly the condition it reads; `DomesticCrises`;
`PoliticalMood`; `GovernmentPromises` (checked continuously, so early fulfilment is credited
the day it happens); `ColonisationCore`; `SubmissionSystem`; `Occupations` (ground held
against a country nobody is at war with goes home); `LostLands`; and `CapitalCore` **last**,
because a region ceded today takes its capital with it.

Then `CampaignIntel.Invalidate`, and either `Changed?.Invoke()` if
`SomethingAwaitsAPlayer()` or just `InterfaceDirty = true`. The interface redraws on the
next frame, not once per step — at top speed a frame runs several steps, and redrawing the
whole HUD for a state the player never saw was the cost this avoids.

## Placing a new system

Ask what it reads. If it reads the map as the day worked it out, it belongs in the tail after
`History.Capture`. If it must run before the fighting, it belongs in the per-step block —
but then it cannot be moved into the day loop, and `StepOpensNewDay` is how such a pass
knows it is the once-a-day one.

## Cache keying

A cache in the presentation layer must be keyed by the `Campaign` reference **and**
`ElapsedDays` / `MapRevision`, so two worlds in one process never read each other's state.
Live examples: `Scripts/ui/DealInboxUI.cs` keys on
`language|player|ElapsedDays|MapRevision|…`; `Scripts/ui/ResourceAtlasUI.cs` folds
`MapRevision` into its fingerprint; `Scripts/gfx/GlobePresentation.cs` keeps separate
`_stateIndexRevision`, `_controlRevision` and `_labelRevision` against `campaign.MapRevision`.

## Presentation conventions

- `Ui.Colour("token.name")` / `InterfaceSkin` for colour — no raw `Color` literals in panels.
- `Fmt` (`Scripts/Simulation/Format.cs`) for compact numbers; `Loc.Get` / `Loc.Format`
  (`Tr(...)`) for EN/RU, with both tables under `game/localization/`.
- `PresentationFonts.Tabular()` for any number that ticks, or it jitters horizontally.

## Build note

The 64-bit .NET SDK must precede the x86 one in `PATH`
(`C:\Program Files\dotnet` before `C:\Program Files (x86)\dotnet`), or the build silently
picks the wrong host.
