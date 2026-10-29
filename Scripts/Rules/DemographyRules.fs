// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Value-only result of stepping a region's age cohorts over one review interval.
[<Struct>]
type AgeCohortStep =
    { YoungShare: single
      OldShare: single
      WorkingShare: single }

/// Pure deterministic demographic cohort and vital multiplier rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type DemographyRules private () =

    /// Share of a region's population of working age, clamped to [0.05, 0.95].
    static member WorkingShare(youngShare: single, oldShare: single) : single =
        Math.Clamp(1.0f - youngShare - oldShare, 0.05f, 0.95f)

    /// Fertility multiplier from young cohort share relative to the 0.28 reference share.
    static member FertilityFactor(youngShare: single) : single =
        let reference = 0.28f
        Math.Clamp(1.0f - (reference - youngShare) * 2.0f, 0.35f, 1.25f)

    /// Old-age mortality multiplier above the reference old cohort share.
    static member MortalityFactor(oldShare: single, referenceOldShare: single, mortalityOldWeight: single) : single =
        let excess = Math.Max(0.0f, oldShare - referenceOldShare)
        1.0f + excess * mortalityOldWeight * 3.0f

    /// Advances young, working and old cohort shares over one demographic review interval.
    static member StepCohorts(population: single,
                              reviewBirths: int64,
                              reviewDeaths: int64,
                              youngShare: single,
                              oldShare: single,
                              maturationAnnual: single,
                              ageingAnnual: single,
                              mortalityOldWeight: single,
                              interval: single) : AgeCohortStep =
        let pop = Math.Max(1.0f, population)
        let born = Math.Clamp((single reviewBirths) / pop, 0.0f, 0.2f)
        let died = Math.Clamp((single reviewDeaths) / pop, 0.0f, 0.2f)
        let working = DemographyRules.WorkingShare(youngShare, oldShare)
        let matured = youngShare * maturationAnnual * interval
        let aged = working * ageingAnnual * interval
        let oldDeaths = Math.Min(oldShare, died * mortalityOldWeight)
        let rest = Math.Max(0.0f, died - oldDeaths)
        let nextYoung = Math.Clamp(youngShare - matured - rest * 0.5f + born, 0.05f, 0.62f)
        let nextOld = Math.Clamp(oldShare + aged - oldDeaths, 0.02f, 0.45f)
        { YoungShare = nextYoung
          OldShare = nextOld
          WorkingShare = DemographyRules.WorkingShare(nextYoung, nextOld) }
