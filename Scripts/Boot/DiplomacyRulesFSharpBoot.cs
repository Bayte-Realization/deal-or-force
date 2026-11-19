using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the zero-alloc F# [&lt;Struct&gt;] rules (Scripts/Rules/DiplomacyRules.fs) into DiplomacyRulesHook.</summary>
internal static class DiplomacyRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            DiplomacyRulesHook.Install(
                DealOrForce.Rules.DiplomacyRules.ConsolidationAmbition,
                DealOrForce.Rules.DiplomacyRules.ConsolidationWarValue,
                DealOrForce.Rules.DiplomacyRules.BuyPeaceOffer,
                EvaluateDeterrence);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=diplomacy");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=diplomacy reason={error.GetType().Name}");
        }
    }

    private static void EvaluateDeterrence(
        int tierA, int tierB, float mutualPriceDefine, bool enabled,
        out float mutualPrice, out float brinkmanship)
    {
        var outcome = DealOrForce.Rules.DiplomacyRules.EvaluateDeterrence(
            tierA, tierB, mutualPriceDefine, enabled);
        mutualPrice = outcome.MutualPrice;
        brinkmanship = outcome.Brinkmanship;
    }
}
