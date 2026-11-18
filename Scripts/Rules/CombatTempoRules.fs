// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Value-only bundle returning all four combat tempo modifiers together without heap allocation.
[<Struct>]
type CombatTempoBundle =
    { Encirclement: single
      Envelopment: single
      Breakthrough: single
      Counterattack: single }

/// Value-only result of evaluating formation supply congestion, distance, blockade, and target readiness.
[<Struct>]
type SupplyDeliveryOutcome =
    { Congestion: single
      DistanceFactor: single
      BlockadeFactor: single
      TargetSupply: single
      NextSupplyLevel: single }

/// Pure deterministic combat tempo, encirclement, envelopment, breakthrough, counterattack, and supply rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type CombatTempoRules private () =

    static member private ClampExact(value: single, minVal: single, maxVal: single) : single =
        if value < minVal then minVal
        elif value > maxVal then maxVal
        else value

    static member private Clamp01Exact(value: single) : single =
        CombatTempoRules.ClampExact(value, 0.0f, 1.0f)

    static member private Clamp01(v: single) : single =
        if not (Single.IsFinite v) then 0.0f
        else Math.Clamp(v, 0.0f, 1.0f)

    static member private Range(value: single, floor: single, span: single) : single =
        CombatTempoRules.Clamp01((value - floor) / MathF.Max(0.01f, span))

    static member private Ratio(value: single, floor: single) : single =
        CombatTempoRules.Clamp01(value / MathF.Max(0.01f, floor))

    /// Cauldron / encirclement combat multiplier (0.15..1.00).
    static member EncirclementFactor(hasDefines: bool, cutOff: bool, daysCutOff: int,
                                     maxCombatPenalty: single, supplyCutoffDays: int) : single =
        if not hasDefines || not cutOff || daysCutOff <= 0 then 1.0f
        else
            let maxPenalty =
                if Single.IsFinite maxCombatPenalty then Math.Clamp(maxCombatPenalty, 0.0f, 0.85f)
                else 0.30f
            let span = Math.Max(1, supplyCutoffDays)
            let maturity = CombatTempoRules.Clamp01((single daysCutOff) / (single span))
            Math.Clamp(1.0f - maxPenalty * maturity, 0.15f, 1.0f)

    /// Multi-directional envelopment multiplier for attackers striking from multiple adjacent provinces.
    static member EnvelopmentFactor(hasDefines: bool, attackDirections: int,
                                    maxAttackDirections: int, flankingBonusPerDirection: single) : single =
        if not hasDefines || attackDirections <= 1 then 1.0f
        else
            let extraAxes = Math.Clamp(attackDirections - 1, 0, Math.Max(0, maxAttackDirections - 1))
            let perAxis =
                if Single.IsFinite flankingBonusPerDirection then Math.Clamp(flankingBonusPerDirection, 0.0f, 0.50f)
                else 0.10f
            1.0f + (single extraAxes) * perAxis

    /// Evaluates breakthrough fraction [0..1] when an attack has superiority, planning, and supply.
    static member Breakthrough(hasDefines: bool, pressure: single, planning: single, supply: single,
                               defenderEntrenchment: single, attackDirections: int, terrainBreakthroughFactor: single,
                               planningFloor: single, supplyFloor: single,
                               pressureFloor: single, pressureSpan: single,
                               entrenchmentResistanceWeight: single) : single =
        if not hasDefines || not (Single.IsFinite pressure) || not (Single.IsFinite planning) || not (Single.IsFinite supply) then 0.0f
        elif planning < planningFloor || supply < supplyFloor then 0.0f
        else
            let entResistance =
                if Single.IsFinite defenderEntrenchment && Single.IsFinite entrenchmentResistanceWeight then
                    MathF.Max(0.0f, defenderEntrenchment * entrenchmentResistanceWeight)
                else 0.0f
            let flankingReduction = single (Math.Max(0, attackDirections - 1)) * 0.20f
            let effectiveFloor = pressureFloor + MathF.Max(0.0f, entResistance - flankingReduction)
            let superiority = CombatTempoRules.Range(pressure, effectiveFloor, pressureSpan)
            let prepared = CombatTempoRules.Ratio(planning, planningFloor)
            let supplied = CombatTempoRules.Ratio(supply, supplyFloor)
            let terrainFactor =
                if Single.IsFinite terrainBreakthroughFactor && terrainBreakthroughFactor > 0.01f then
                    MathF.Min(1.0f, terrainBreakthroughFactor)
                else 1.0f
            CombatTempoRules.Clamp01(superiority * prepared * supplied * terrainFactor)

    /// Evaluates counterattack fraction [0..1] from defender superiority or attacker exhaustion.
    static member Counterattack(hasDefines: bool, pressure: single, entrenchment: single, supply: single,
                                attackerOrg: single, defenderOrg: single, desperateDefense: bool,
                                entrenchmentFloor: single, supplyFloor: single,
                                pressureFloor: single, pressureSpan: single,
                                vulnerabilityWeight: single, desperateBonus: single) : single =
        if not hasDefines || not (Single.IsFinite pressure) || not (Single.IsFinite entrenchment) || not (Single.IsFinite supply) then 0.0f
        elif entrenchment < entrenchmentFloor || supply < supplyFloor then 0.0f
        else
            let superiority = CombatTempoRules.Range(pressure, pressureFloor, pressureSpan)
            let mutable vulnerability = 0.0f
            if Single.IsFinite attackerOrg && Single.IsFinite defenderOrg then
                let orgDelta = defenderOrg - attackerOrg
                if attackerOrg < 0.60f && orgDelta > -0.15f then
                    let exhaustion = CombatTempoRules.Clamp01((0.60f - attackerOrg) / 0.40f)
                    let tenacity = CombatTempoRules.Clamp01((defenderOrg + 0.15f) / 0.50f)
                    let w = if Single.IsFinite vulnerabilityWeight then vulnerabilityWeight else 1.50f
                    vulnerability <- exhaustion * tenacity * w
            if desperateDefense && Single.IsFinite desperateBonus then
                vulnerability <- vulnerability + desperateBonus
            let effectiveDrive = MathF.Max(superiority, CombatTempoRules.Clamp01(vulnerability))
            if effectiveDrive <= 0.001f then 0.0f
            else
                let prepared = CombatTempoRules.Ratio(entrenchment, entrenchmentFloor)
                let supplied = CombatTempoRules.Ratio(supply, supplyFloor)
                CombatTempoRules.Clamp01(effectiveDrive * prepared * supplied)

    /// Evaluates encirclement, envelopment, breakthrough, and counterattack in one value struct.
    static member EvaluateTempoBundle(hasDefines: bool, cutOff: bool, daysCutOff: int,
                                      maxCombatPenalty: single, supplyCutoffDays: int,
                                      attackDirections: int, maxAttackDirections: int, flankingBonusPerDirection: single,
                                      pressure: single, planning: single, supply: single,
                                      defenderEntrenchment: single, terrainBreakthroughFactor: single,
                                      breakthroughPlanningFloor: single, breakthroughSupplyFloor: single,
                                      breakthroughPressureFloor: single, breakthroughPressureSpan: single,
                                      entrenchmentResistanceWeight: single,
                                      counterPressure: single, counterEntrenchment: single, counterSupply: single,
                                      attackerOrg: single, defenderOrg: single, desperateDefense: bool,
                                      counterEntrenchmentFloor: single, counterSupplyFloor: single,
                                      counterPressureFloor: single, counterPressureSpan: single,
                                      vulnerabilityWeight: single, desperateBonus: single) : CombatTempoBundle =
        { Encirclement =
            CombatTempoRules.EncirclementFactor(hasDefines, cutOff, daysCutOff, maxCombatPenalty, supplyCutoffDays)
          Envelopment =
            CombatTempoRules.EnvelopmentFactor(hasDefines, attackDirections, maxAttackDirections, flankingBonusPerDirection)
          Breakthrough =
            CombatTempoRules.Breakthrough(
                hasDefines, pressure, planning, supply, defenderEntrenchment, attackDirections, terrainBreakthroughFactor,
                breakthroughPlanningFloor, breakthroughSupplyFloor, breakthroughPressureFloor, breakthroughPressureSpan,
                entrenchmentResistanceWeight)
          Counterattack =
            CombatTempoRules.Counterattack(
                hasDefines, counterPressure, counterEntrenchment, counterSupply, attackerOrg, defenderOrg, desperateDefense,
                counterEntrenchmentFloor, counterSupplyFloor, counterPressureFloor, counterPressureSpan,
                vulnerabilityWeight, desperateBonus) }

    /// Evaluates supply congestion, route-distance attenuation, blockade penalty, and army supply step.
    static member EvaluateSupplyDelivery(capacity: single,
                                         demand: single,
                                         hopDistance: int,
                                         traceRange: int,
                                         lakeMultiplier: single,
                                         blockade: single,
                                         fleetBlockadePenalty: single,
                                         terrainSupply: single,
                                         nationalSupplyFactor: single,
                                         currentSupplyLevel: single) : SupplyDeliveryOutcome =
        let safeDemand = MathF.Max(0.01f, demand)
        let congestion = CombatTempoRules.ClampExact(capacity / safeDemand, 0.08f, 1.0f)
        let safeRange = Math.Max(1, traceRange)
        let distanceFactor =
            CombatTempoRules.ClampExact(1.0f - single hopDistance / single safeRange * 0.62f, 0.22f, 1.0f)
        let blockadeFactor = 1.0f - blockade * fleetBlockadePenalty
        let target =
            CombatTempoRules.Clamp01Exact(
                congestion * distanceFactor * lakeMultiplier * blockadeFactor * terrainSupply * nationalSupplyFactor)
        let nextLevel = currentSupplyLevel + (target - currentSupplyLevel) * CombatTempoRules.Clamp01Exact(0.34f)
        { Congestion = congestion
          DistanceFactor = distanceFactor
          BlockadeFactor = blockadeFactor
          TargetSupply = target
          NextSupplyLevel = nextLevel }
