using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the zero-alloc F# [&lt;Struct&gt;] rules (Scripts/Rules/CombatTempoRules.fs) into CombatTempoRulesHook.</summary>
internal static class CombatTempoRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            CombatTempoRulesHook.Install(
                DealOrForce.Rules.CombatTempoRules.EncirclementFactor,
                DealOrForce.Rules.CombatTempoRules.EnvelopmentFactor,
                DealOrForce.Rules.CombatTempoRules.Breakthrough,
                DealOrForce.Rules.CombatTempoRules.Counterattack,
                EvaluateSupplyDelivery);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=combat_tempo");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=combat_tempo reason={error.GetType().Name}");
        }
    }

    private static void EvaluateSupplyDelivery(
        float capacity, float demand, int hopDistance, int traceRange,
        float lakeMultiplier, float blockade, float fleetBlockadePenalty,
        float terrainSupply, float nationalSupplyFactor, float currentSupplyLevel,
        out float congestion, out float distanceFactor, out float blockadeFactor,
        out float targetSupply, out float nextSupplyLevel)
    {
        var outcome = DealOrForce.Rules.CombatTempoRules.EvaluateSupplyDelivery(
            capacity, demand, hopDistance, traceRange, lakeMultiplier, blockade, fleetBlockadePenalty,
            terrainSupply, nationalSupplyFactor, currentSupplyLevel);
        congestion = outcome.Congestion;
        distanceFactor = outcome.DistanceFactor;
        blockadeFactor = outcome.BlockadeFactor;
        targetSupply = outcome.TargetSupply;
        nextSupplyLevel = outcome.NextSupplyLevel;
    }
}
