// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

/// Value-only result for war score evaluation and collapse determination.
[<Struct>]
type WarScoreOutcome =
    { Collapsed: int
      RawScore: single
      ClampedScore: single
      FinalScore: single }

/// Pure deterministic war score rules. No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type WarScoreRules private () =
    static member HasCollapsed(occupied: single, strengthLeft: single,
                               collapseOccupation: single, collapseOccupationBroken: single,
                               collapseArmy: single) : bool =
        if not (System.Single.IsFinite occupied) || not (System.Single.IsFinite strengthLeft) then false
        else
            occupied >= collapseOccupation ||
            (occupied >= collapseOccupationBroken && strengthLeft <= collapseArmy)

    static member ClampScore(score: single, minScore: single, maxScore: single) : single =
        if not (System.Single.IsFinite score) then 0.0f
        else
            let low = System.Math.Min(minScore, maxScore)
            let high = System.Math.Max(minScore, maxScore)
            System.Math.Clamp(score, low, high)

    static member Evaluate(occupationScore: single, capitalScore: single, armyScore: single,
                           battleScore: single, objectiveScore: single, tickingScore: single,
                           supportScore: single,
                           defenderOccupied: single, defenderStrengthLeft: single,
                           attackerOccupied: single, attackerStrengthLeft: single,
                           collapseOccupation: single, collapseOccupationBroken: single,
                           collapseArmy: single, minScore: single, maxScore: single) : WarScoreOutcome =
        let raw = occupationScore + capitalScore + armyScore + battleScore + objectiveScore + tickingScore + supportScore
        let defenderCollapsed =
            WarScoreRules.HasCollapsed(defenderOccupied, defenderStrengthLeft, collapseOccupation, collapseOccupationBroken, collapseArmy)
        let attackerCollapsed =
            WarScoreRules.HasCollapsed(attackerOccupied, attackerStrengthLeft, collapseOccupation, collapseOccupationBroken, collapseArmy)
        let collapsed =
            if defenderCollapsed then 1
            elif attackerCollapsed then -1
            else 0

        let low = System.Math.Min(minScore, maxScore)
        let high = System.Math.Max(minScore, maxScore)
        let clamped = WarScoreRules.ClampScore(raw, minScore, maxScore)
        let finalScore =
            if collapsed > 0 then high
            elif collapsed < 0 then low
            else clamped

        { Collapsed = collapsed
          RawScore = raw
          ClampedScore = clamped
          FinalScore = finalScore }
