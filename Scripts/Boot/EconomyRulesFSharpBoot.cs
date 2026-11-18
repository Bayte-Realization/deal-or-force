using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the zero-alloc F# [&lt;Struct&gt;] rules (Scripts/Rules/EconomyRules.fs) into EconomyRulesHook.</summary>
internal static class EconomyRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            EconomyRulesHook.Install(
                EvaluateCreditTerms,
                DealOrForce.Rules.EconomyRules.DebtCapacity,
                DealOrForce.Rules.EconomyRules.AvailableCredit,
                EvaluateTradeCost,
                DealOrForce.Rules.EconomyRules.TaxCompliance,
                DealOrForce.Rules.EconomyRules.EvaluateBudgetLineAmount);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=economy");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=economy reason={error.GetType().Name}");
        }
    }

    private static void EvaluateCreditTerms(
        float publicDebt, float gdp, float stability, float inflation,
        float debtArrears, float paymentArrears, bool inDefaultOrWorse,
        float currentConfidence, float borrowingCostFactor, float maxSpread,
        out float targetConfidence, out float nextConfidence, out float annualSpread)
    {
        var terms = DealOrForce.Rules.EconomyRules.EvaluateCreditTerms(
            publicDebt, gdp, stability, inflation, debtArrears, paymentArrears,
            inDefaultOrWorse, currentConfidence, borrowingCostFactor, maxSpread);
        targetConfidence = terms.TargetConfidence;
        nextConfidence = terms.NextConfidence;
        annualSpread = terms.AnnualSpread;
    }

    private static void EvaluateTradeCost(
        float imports, float relieved, float price,
        float importMarkupDefine, float reliefDefine, float tariffRate,
        out float importMarkup, out float duty)
    {
        var outcome = DealOrForce.Rules.EconomyRules.EvaluateTradeCost(
            imports, relieved, price, importMarkupDefine, reliefDefine, tariffRate);
        importMarkup = outcome.ImportMarkup;
        duty = outcome.Duty;
    }
}
