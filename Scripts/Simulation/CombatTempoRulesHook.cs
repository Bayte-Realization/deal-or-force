// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;

namespace DealOrForce.Simulation;

internal delegate float CombatEncirclementEvaluator(bool hasDefines, bool cutOff, int daysCutOff, float maxCombatPenalty, int supplyCutoffDays);
internal delegate float CombatEnvelopmentEvaluator(bool hasDefines, int attackDirections, int maxAttackDirections, float flankingBonusPerDirection);
internal delegate float CombatBreakthroughEvaluator(
    bool hasDefines, float pressure, float planning, float supply,
    float defenderEntrenchment, int attackDirections, float terrainBreakthroughFactor,
    float planningFloor, float supplyFloor, float pressureFloor, float pressureSpan,
    float entrenchmentResistanceWeight);
internal delegate float CombatCounterattackEvaluator(
    bool hasDefines, float pressure, float entrenchment, float supply,
    float attackerOrg, float defenderOrg, bool desperateDefense,
    float entrenchmentFloor, float supplyFloor, float pressureFloor, float pressureSpan,
    float vulnerabilityWeight, float desperateBonus);
internal delegate void SupplyDeliveryEvaluator(
    float capacity, float demand, int hopDistance, int traceRange,
    float lakeMultiplier, float blockade, float fleetBlockadePenalty,
    float terrainSupply, float nationalSupplyFactor, float currentSupplyLevel,
    out float congestion, out float distanceFactor, out float blockadeFactor,
    out float targetSupply, out float nextSupplyLevel);

/// <summary>
/// Optional F# pure-rule hook for combat tempo and supply delivery formulas,
/// with bit-identical pure C# fallbacks.
/// </summary>
internal static class CombatTempoRulesHook
{
    private static CombatEncirclementEvaluator? _encirclement;
    private static CombatEnvelopmentEvaluator? _envelopment;
    private static CombatBreakthroughEvaluator? _breakthrough;
    private static CombatCounterattackEvaluator? _counterattack;
    private static SupplyDeliveryEvaluator? _supplyDelivery;

    internal static bool Installed =>
        _encirclement is not null && _envelopment is not null &&
        _breakthrough is not null && _counterattack is not null && _supplyDelivery is not null;

    internal static bool UsingFSharp => Installed;

    internal static void Install(
        CombatEncirclementEvaluator encirclement,
        CombatEnvelopmentEvaluator envelopment,
        CombatBreakthroughEvaluator breakthrough,
        CombatCounterattackEvaluator counterattack,
        SupplyDeliveryEvaluator supplyDelivery)
    {
        _encirclement = encirclement;
        _envelopment = envelopment;
        _breakthrough = breakthrough;
        _counterattack = counterattack;
        _supplyDelivery = supplyDelivery;
    }

    internal static float EncirclementFactor(bool hasDefines, bool cutOff, int daysCutOff, float maxCombatPenalty, int supplyCutoffDays)
    {
        var hook = _encirclement;
        if (hook is not null)
        {
            try { return hook(hasDefines, cutOff, daysCutOff, maxCombatPenalty, supplyCutoffDays); }
            catch (Exception error)
            {
                _encirclement = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=combat_encirclement reason={error.GetType().Name}");
            }
        }
        return ManagedEncirclementFactor(hasDefines, cutOff, daysCutOff, maxCombatPenalty, supplyCutoffDays);
    }

    internal static float EnvelopmentFactor(bool hasDefines, int attackDirections, int maxAttackDirections, float flankingBonusPerDirection)
    {
        var hook = _envelopment;
        if (hook is not null)
        {
            try { return hook(hasDefines, attackDirections, maxAttackDirections, flankingBonusPerDirection); }
            catch (Exception error)
            {
                _envelopment = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=combat_envelopment reason={error.GetType().Name}");
            }
        }
        return ManagedEnvelopmentFactor(hasDefines, attackDirections, maxAttackDirections, flankingBonusPerDirection);
    }

    internal static float Breakthrough(
        bool hasDefines, float pressure, float planning, float supply,
        float defenderEntrenchment, int attackDirections, float terrainBreakthroughFactor,
        float planningFloor, float supplyFloor, float pressureFloor, float pressureSpan,
        float entrenchmentResistanceWeight)
    {
        var hook = _breakthrough;
        if (hook is not null)
        {
            try
            {
                return hook(hasDefines, pressure, planning, supply, defenderEntrenchment, attackDirections,
                    terrainBreakthroughFactor, planningFloor, supplyFloor, pressureFloor, pressureSpan,
                    entrenchmentResistanceWeight);
            }
            catch (Exception error)
            {
                _breakthrough = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=combat_breakthrough reason={error.GetType().Name}");
            }
        }
        return ManagedBreakthrough(hasDefines, pressure, planning, supply, defenderEntrenchment, attackDirections,
            terrainBreakthroughFactor, planningFloor, supplyFloor, pressureFloor, pressureSpan,
            entrenchmentResistanceWeight);
    }

    internal static float Counterattack(
        bool hasDefines, float pressure, float entrenchment, float supply,
        float attackerOrg, float defenderOrg, bool desperateDefense,
        float entrenchmentFloor, float supplyFloor, float pressureFloor, float pressureSpan,
        float vulnerabilityWeight, float desperateBonus)
    {
        var hook = _counterattack;
        if (hook is not null)
        {
            try
            {
                return hook(hasDefines, pressure, entrenchment, supply, attackerOrg, defenderOrg, desperateDefense,
                    entrenchmentFloor, supplyFloor, pressureFloor, pressureSpan, vulnerabilityWeight, desperateBonus);
            }
            catch (Exception error)
            {
                _counterattack = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=combat_counterattack reason={error.GetType().Name}");
            }
        }
        return ManagedCounterattack(hasDefines, pressure, entrenchment, supply, attackerOrg, defenderOrg,
            desperateDefense, entrenchmentFloor, supplyFloor, pressureFloor, pressureSpan, vulnerabilityWeight, desperateBonus);
    }

    internal static void EvaluateSupplyDelivery(
        float capacity, float demand, int hopDistance, int traceRange,
        float lakeMultiplier, float blockade, float fleetBlockadePenalty,
        float terrainSupply, float nationalSupplyFactor, float currentSupplyLevel,
        out float congestion, out float distanceFactor, out float blockadeFactor,
        out float targetSupply, out float nextSupplyLevel)
    {
        var hook = _supplyDelivery;
        if (hook is not null)
        {
            try
            {
                hook(capacity, demand, hopDistance, traceRange, lakeMultiplier, blockade, fleetBlockadePenalty,
                    terrainSupply, nationalSupplyFactor, currentSupplyLevel,
                    out congestion, out distanceFactor, out blockadeFactor, out targetSupply, out nextSupplyLevel);
                return;
            }
            catch (Exception error)
            {
                _supplyDelivery = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=supply_delivery reason={error.GetType().Name}");
            }
        }
        ManagedEvaluateSupplyDelivery(capacity, demand, hopDistance, traceRange, lakeMultiplier, blockade,
            fleetBlockadePenalty, terrainSupply, nationalSupplyFactor, currentSupplyLevel,
            out congestion, out distanceFactor, out blockadeFactor, out targetSupply, out nextSupplyLevel);
    }

    private static float Clamp01(float v) => !float.IsFinite(v) ? 0f : Math.Clamp(v, 0f, 1f);
    private static float Range(float value, float floor, float span) => Clamp01((value - floor) / MathF.Max(0.01f, span));
    private static float Ratio(float value, float floor) => Clamp01(value / MathF.Max(0.01f, floor));

    internal static float ManagedEncirclementFactor(bool hasDefines, bool cutOff, int daysCutOff, float maxCombatPenalty, int supplyCutoffDays)
    {
        if (!hasDefines || !cutOff || daysCutOff <= 0) return 1f;
        var maxPenalty = float.IsFinite(maxCombatPenalty) ? Math.Clamp(maxCombatPenalty, 0f, 0.85f) : 0.30f;
        var span = Math.Max(1, supplyCutoffDays);
        var maturity = Clamp01((float)daysCutOff / (float)span);
        return Math.Clamp(1f - maxPenalty * maturity, 0.15f, 1f);
    }

    internal static float ManagedEnvelopmentFactor(bool hasDefines, int attackDirections, int maxAttackDirections, float flankingBonusPerDirection)
    {
        if (!hasDefines || attackDirections <= 1) return 1f;
        var extraAxes = Math.Clamp(attackDirections - 1, 0, Math.Max(0, maxAttackDirections - 1));
        var perAxis = float.IsFinite(flankingBonusPerDirection) ? Math.Clamp(flankingBonusPerDirection, 0f, 0.50f) : 0.10f;
        return 1f + (float)extraAxes * perAxis;
    }

    internal static float ManagedBreakthrough(
        bool hasDefines, float pressure, float planning, float supply,
        float defenderEntrenchment, int attackDirections, float terrainBreakthroughFactor,
        float planningFloor, float supplyFloor, float pressureFloor, float pressureSpan,
        float entrenchmentResistanceWeight)
    {
        if (!hasDefines || !float.IsFinite(pressure) || !float.IsFinite(planning) || !float.IsFinite(supply)) return 0f;
        if (planning < planningFloor || supply < supplyFloor) return 0f;
        var entResistance = float.IsFinite(defenderEntrenchment) && float.IsFinite(entrenchmentResistanceWeight)
            ? MathF.Max(0f, defenderEntrenchment * entrenchmentResistanceWeight)
            : 0f;
        var flankingReduction = (float)Math.Max(0, attackDirections - 1) * 0.20f;
        var effectiveFloor = pressureFloor + MathF.Max(0f, entResistance - flankingReduction);
        var superiority = Range(pressure, effectiveFloor, pressureSpan);
        var prepared = Ratio(planning, planningFloor);
        var supplied = Ratio(supply, supplyFloor);
        var terrainFactor = float.IsFinite(terrainBreakthroughFactor) && terrainBreakthroughFactor > 0.01f
            ? MathF.Min(1f, terrainBreakthroughFactor)
            : 1f;
        return Clamp01(superiority * prepared * supplied * terrainFactor);
    }

    internal static float ManagedCounterattack(
        bool hasDefines, float pressure, float entrenchment, float supply,
        float attackerOrg, float defenderOrg, bool desperateDefense,
        float entrenchmentFloor, float supplyFloor, float pressureFloor, float pressureSpan,
        float vulnerabilityWeight, float desperateBonus)
    {
        if (!hasDefines || !float.IsFinite(pressure) || !float.IsFinite(entrenchment) || !float.IsFinite(supply)) return 0f;
        if (entrenchment < entrenchmentFloor || supply < supplyFloor) return 0f;
        var superiority = Range(pressure, pressureFloor, pressureSpan);
        var vulnerability = 0f;
        if (float.IsFinite(attackerOrg) && float.IsFinite(defenderOrg))
        {
            var orgDelta = defenderOrg - attackerOrg;
            if (attackerOrg < 0.60f && orgDelta > -0.15f)
            {
                var exhaustion = Clamp01((0.60f - attackerOrg) / 0.40f);
                var tenacity = Clamp01((defenderOrg + 0.15f) / 0.50f);
                var w = float.IsFinite(vulnerabilityWeight) ? vulnerabilityWeight : 1.50f;
                vulnerability = exhaustion * tenacity * w;
            }
        }
        if (desperateDefense && float.IsFinite(desperateBonus)) vulnerability += desperateBonus;
        var effectiveDrive = MathF.Max(superiority, Clamp01(vulnerability));
        if (effectiveDrive <= 0.001f) return 0f;
        var prepared = Ratio(entrenchment, entrenchmentFloor);
        var supplied = Ratio(supply, supplyFloor);
        return Clamp01(effectiveDrive * prepared * supplied);
    }

    internal static void ManagedEvaluateSupplyDelivery(
        float capacity, float demand, int hopDistance, int traceRange,
        float lakeMultiplier, float blockade, float fleetBlockadePenalty,
        float terrainSupply, float nationalSupplyFactor, float currentSupplyLevel,
        out float congestion, out float distanceFactor, out float blockadeFactor,
        out float targetSupply, out float nextSupplyLevel)
    {
        var safeDemand = MathF.Max(0.01f, demand);
        congestion = M.Clamp(capacity / safeDemand, 0.08f, 1f);
        var safeRange = Math.Max(1, traceRange);
        distanceFactor = M.Clamp(1f - (float)hopDistance / (float)safeRange * 0.62f, 0.22f, 1f);
        blockadeFactor = 1f - blockade * fleetBlockadePenalty;
        targetSupply = M.Clamp01(congestion * distanceFactor * lakeMultiplier * blockadeFactor * terrainSupply * nationalSupplyFactor);
        nextSupplyLevel = currentSupplyLevel + (targetSupply - currentSupplyLevel) * M.Clamp01(0.34f);
    }
}
