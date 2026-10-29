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

    /// A side's fighting spirit in one year of a war: steady at 1 +/- spread, triangular - and,
    /// for a few, the year it turns out to be something nobody counted on. surpriseChance of
    /// them fight far above their weight (Finland in 1939, Ukraine in 2022) by surpriseSwing;
    /// collapseChance of them break (the Afghan army in 2021, Iraq's in Mosul in 2014) by
    /// collapseSwing. tailRoll decides which, once per side and year.
    static member FightingSpirit(spread: single, rollA: single, rollB: single, tailRoll: single,
                                 surpriseChance: single, collapseChance: single,
                                 surpriseSwing: single, collapseSwing: single) : single =
        let clamp (low: single) (high: single) (value: single) = max low (min high value)
        let spread = clamp 0.0f 0.5f spread
        let steady = 1.0f + spread * (rollA + rollB - 1.0f)
        let surprise = clamp 0.0f 1.0f surpriseChance
        let collapse = clamp 0.0f (1.0f - surprise) collapseChance
        if tailRoll < surprise then steady * (1.0f + clamp 0.0f 0.9f surpriseSwing)
        elif tailRoll >= 1.0f - collapse then steady * (1.0f - clamp 0.0f 0.9f collapseSwing)
        else steady

    /// How much ground a side fighting the war of the weak can hide in, 0 to 1: how lopsided
    /// the war is against it (asymmetry), how rough the province is (roughness), and whether it
    /// is fighting among its own people (homeGround) - each 0 to 1. The Hussites behind their
    /// wagons, the Taliban in the valleys: neither term alone, both together.
    static member GuerrillaCover(asymmetry: single, roughness: single, homeGround: single) : single =
        let clamp01 (value: single) = max 0.0f (min 1.0f value)
        clamp01 asymmetry * (0.5f * clamp01 roughness + 0.5f * clamp01 homeGround)

    /// The share of a formation's soldiers who desert in a week, 0 to cap: base times the square
    /// of the pressure on them - a war nobody at home believes in (1 - warSupport), a unit that
    /// has stopped being one (1 - cohesion), hunger below half supply, the shock of a broken
    /// line below 0.3 organisation, pay that has stopped (unpaid, 0 or 1), and a side whose
    /// spirit has gone below its own weight. A well-fed army in a popular war loses nobody.
    static member DesertionRate(warSupport: single, cohesion: single, supply: single,
                                organisation: single, unpaid: single, spirit: single,
                                baseRate: single, cap: single) : single =
        let clamp01 (value: single) = max 0.0f (min 1.0f value)
        let pressure =
            (1.0f - clamp01 warSupport) * 0.9f
            + (1.0f - clamp01 cohesion) * 0.6f
            + max 0.0f (0.5f - clamp01 supply) * 1.6f
            + max 0.0f (0.3f - clamp01 organisation)
            + clamp01 unpaid * 0.5f
            + max 0.0f (1.0f - spirit) * 1.5f
        max 0.0f (min (max 0.0f cap) (max 0.0f baseRate * pressure * pressure))
