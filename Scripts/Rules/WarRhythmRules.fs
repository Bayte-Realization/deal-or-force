// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

/// The rhythm and the fortune of war: when a side throws its weight forward, and how a battle's
/// luck falls. Pure and deterministic - every random number arrives as a roll in [0, 1) drawn by
/// the caller from the campaign seed, so the same inputs always give the same answer.
[<AbstractClass; Sealed>]
type WarRhythmRules private () =

    /// How much of its army a side sends forward today, as a factor on its usual share: its own
    /// cycle of 90 to 180 days (periodRoll), at its own phase (phaseRoll), a third of it on the
    /// offensive at 1 + strength and a quarter of it resting at 1 - 0.4 strength (never below 0.3).
    static member OffensivePulse(strength: single, periodRoll: single, phaseRoll: single, daysSinceStart: int) : single =
        if strength <= 0.0f then 1.0f
        else
            let period = 90.0f + 90.0f * periodRoll
            let position = single daysSinceStart / period + phaseRoll
            let t = position - floor position
            if t < 0.32f then 1.0f + strength
            elif t > 0.75f then max 0.3f (1.0f - strength * 0.4f)
            else 1.0f

    /// One side's fortune for a day of a battle: the day's, triangular 1 +/- daySpread, times the
    /// battle's own, triangular 1 +/- battleSpread, and - for decisiveChance of battles - the one
    /// moment it turns on, up or down by swing.
    static member BattleFortune(daySpread: single, dayRollA: single, dayRollB: single,
                                battleSpread: single, battleRollA: single, battleRollB: single,
                                decisiveChance: single, decisiveRoll: single,
                                swing: single, swingRoll: single) : single =
        let clamp (low: single) (high: single) (value: single) = max low (min high value)
        let daySpread = clamp 0.0f 0.6f daySpread
        let today = if daySpread <= 0.0f then 1.0f else 1.0f + daySpread * (dayRollA + dayRollB - 1.0f)
        let battleSpread = clamp 0.0f 0.6f battleSpread
        let whole = 1.0f + battleSpread * (battleRollA + battleRollB - 1.0f)
        let turned =
            if decisiveRoll < clamp 0.0f 1.0f decisiveChance then
                let swing = clamp 0.0f 0.9f swing
                if swingRoll < 0.5f then whole * (1.0f - swing) else whole * (1.0f + swing)
            else whole
        today * turned
