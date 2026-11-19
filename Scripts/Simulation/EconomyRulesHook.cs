// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;

namespace DealOrForce.Simulation;

internal delegate void EconomyCreditTermsEvaluator(
    float publicDebt, float gdp, float stability, float inflation,
    float debtArrears, float paymentArrears, bool inDefaultOrWorse,
    float currentConfidence, float borrowingCostFactor, float maxSpread,
    out float targetConfidence, out float nextConfidence, out float annualSpread);

internal delegate float EconomyDebtCapacityEvaluator(
    float gdp, float baseCapacity, float confidenceCapacity,
    float creditConfidence, float capacityFactor);

internal delegate float EconomyAvailableCreditEvaluator(
    float capacity, float publicDebt, float gdp,
    float weeklyIssueShare, float debtIssuedWeekly);

internal delegate void EconomyTradeCostEvaluator(
    float imports, float relieved, float price,
    float importMarkupDefine, float reliefDefine, float tariffRate,
    out float importMarkup, out float duty);

internal delegate float EconomyTaxComplianceEvaluator(
    bool hasPopulation, float burden, float taxEvasion, float taxTolerance);

internal delegate float EconomyBudgetLineAmountEvaluator(
    float basis, float rate, float yieldValue, float scale,
    bool isDebtInterest, float publicDebt, float debtSpreadAnnual,
    bool isLevy, float compliance, float taxEfficiency, bool allowNegative);

/// <summary>
/// Optional F# pure-rule hook for sovereign finance, market tariffs, and tax compliance,
/// with bit-identical pure C# fallbacks when no boot hook is installed (e.g., SimBench) or on fault.
/// </summary>
internal static class EconomyRulesHook
{
    private static EconomyCreditTermsEvaluator? _creditTerms;
    private static EconomyDebtCapacityEvaluator? _debtCapacity;
    private static EconomyAvailableCreditEvaluator? _availableCredit;
    private static EconomyTradeCostEvaluator? _tradeCost;
    private static EconomyTaxComplianceEvaluator? _taxCompliance;
    private static EconomyBudgetLineAmountEvaluator? _budgetLineAmount;

    internal static bool Installed =>
        _creditTerms is not null && _debtCapacity is not null && _availableCredit is not null &&
        _tradeCost is not null && _taxCompliance is not null && _budgetLineAmount is not null;

    internal static bool UsingFSharp => Installed;

    internal static void Install(
        EconomyCreditTermsEvaluator creditTerms,
        EconomyDebtCapacityEvaluator debtCapacity,
        EconomyAvailableCreditEvaluator availableCredit,
        EconomyTradeCostEvaluator tradeCost,
        EconomyTaxComplianceEvaluator taxCompliance,
        EconomyBudgetLineAmountEvaluator budgetLineAmount)
    {
        _creditTerms = creditTerms;
        _debtCapacity = debtCapacity;
        _availableCredit = availableCredit;
        _tradeCost = tradeCost;
        _taxCompliance = taxCompliance;
        _budgetLineAmount = budgetLineAmount;
    }

    internal static void EvaluateCreditTerms(
        float publicDebt, float gdp, float stability, float inflation,
        float debtArrears, float paymentArrears, bool inDefaultOrWorse,
        float currentConfidence, float borrowingCostFactor, float maxSpread,
        out float targetConfidence, out float nextConfidence, out float annualSpread)
    {
        var hook = _creditTerms;
        if (hook is not null)
        {
            try
            {
                hook(publicDebt, gdp, stability, inflation, debtArrears, paymentArrears,
                    inDefaultOrWorse, currentConfidence, borrowingCostFactor, maxSpread,
                    out targetConfidence, out nextConfidence, out annualSpread);
                return;
            }
            catch (Exception error)
            {
                _creditTerms = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_credit reason={error.GetType().Name}");
            }
        }
        ManagedEvaluateCreditTerms(publicDebt, gdp, stability, inflation, debtArrears, paymentArrears,
            inDefaultOrWorse, currentConfidence, borrowingCostFactor, maxSpread,
            out targetConfidence, out nextConfidence, out annualSpread);
    }

    internal static float DebtCapacity(
        float gdp, float baseCapacity, float confidenceCapacity,
        float creditConfidence, float capacityFactor)
    {
        var hook = _debtCapacity;
        if (hook is not null)
        {
            try
            {
                return hook(gdp, baseCapacity, confidenceCapacity, creditConfidence, capacityFactor);
            }
            catch (Exception error)
            {
                _debtCapacity = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_debt_capacity reason={error.GetType().Name}");
            }
        }
        return ManagedDebtCapacity(gdp, baseCapacity, confidenceCapacity, creditConfidence, capacityFactor);
    }

    internal static float AvailableCredit(
        float capacity, float publicDebt, float gdp,
        float weeklyIssueShare, float debtIssuedWeekly)
    {
        var hook = _availableCredit;
        if (hook is not null)
        {
            try
            {
                return hook(capacity, publicDebt, gdp, weeklyIssueShare, debtIssuedWeekly);
            }
            catch (Exception error)
            {
                _availableCredit = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_available_credit reason={error.GetType().Name}");
            }
        }
        return ManagedAvailableCredit(capacity, publicDebt, gdp, weeklyIssueShare, debtIssuedWeekly);
    }

    internal static void EvaluateTradeCost(
        float imports, float relieved, float price,
        float importMarkupDefine, float reliefDefine, float tariffRate,
        out float importMarkup, out float duty)
    {
        var hook = _tradeCost;
        if (hook is not null)
        {
            try
            {
                hook(imports, relieved, price, importMarkupDefine, reliefDefine, tariffRate,
                    out importMarkup, out duty);
                return;
            }
            catch (Exception error)
            {
                _tradeCost = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_trade_cost reason={error.GetType().Name}");
            }
        }
        ManagedEvaluateTradeCost(imports, relieved, price, importMarkupDefine, reliefDefine, tariffRate,
            out importMarkup, out duty);
    }

    internal static float TaxCompliance(bool hasPopulation, float burden, float taxEvasion, float taxTolerance)
    {
        var hook = _taxCompliance;
        if (hook is not null)
        {
            try
            {
                return hook(hasPopulation, burden, taxEvasion, taxTolerance);
            }
            catch (Exception error)
            {
                _taxCompliance = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_tax_compliance reason={error.GetType().Name}");
            }
        }
        return ManagedTaxCompliance(hasPopulation, burden, taxEvasion, taxTolerance);
    }

    internal static float EvaluateBudgetLineAmount(
        float basis, float rate, float yieldValue, float scale,
        bool isDebtInterest, float publicDebt, float debtSpreadAnnual,
        bool isLevy, float compliance, float taxEfficiency, bool allowNegative)
    {
        var hook = _budgetLineAmount;
        if (hook is not null)
        {
            try
            {
                return hook(basis, rate, yieldValue, scale, isDebtInterest, publicDebt, debtSpreadAnnual,
                    isLevy, compliance, taxEfficiency, allowNegative);
            }
            catch (Exception error)
            {
                _budgetLineAmount = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=economy_budget_line reason={error.GetType().Name}");
            }
        }
        return ManagedEvaluateBudgetLineAmount(basis, rate, yieldValue, scale, isDebtInterest, publicDebt,
            debtSpreadAnnual, isLevy, compliance, taxEfficiency, allowNegative);
    }

    internal static void ManagedEvaluateCreditTerms(
        float publicDebt, float gdp, float stability, float inflation,
        float debtArrears, float paymentArrears, bool inDefaultOrWorse,
        float currentConfidence, float borrowingCostFactor, float maxSpread,
        out float targetConfidence, out float nextConfidence, out float annualSpread)
    {
        var safeGdp = MathF.Max(0.01f, gdp);
        var ratio = publicDebt / safeGdp;
        var rawTarget =
            0.35f
            + stability * 0.55f
            - MathF.Max(0f, inflation - 0.04f) * 0.7f
            - ratio * 0.22f
            - (debtArrears + paymentArrears) / safeGdp * 3.0f;
        var target = !float.IsFinite(rawTarget) ? 0f : Math.Clamp(rawTarget, 0f, 1f);
        if (inDefaultOrWorse) target *= 0.35f;
        var next = currentConfidence + (target - currentConfidence) * 0.08f;
        var spreadCap = Math.Clamp(maxSpread, 0f, 1f);
        var rawSpread =
            ((1f - next) * 0.05f + MathF.Max(0f, ratio - 0.6f) * 0.04f) * borrowingCostFactor;
        targetConfidence = target;
        nextConfidence = next;
        annualSpread = Math.Clamp(rawSpread, 0f, spreadCap);
    }

    internal static float ManagedDebtCapacity(
        float gdp, float baseCapacity, float confidenceCapacity,
        float creditConfidence, float capacityFactor) =>
        MathF.Max(0f, gdp) *
        MathF.Max(0f, baseCapacity + MathF.Max(0f, confidenceCapacity) * creditConfidence) *
        capacityFactor;

    internal static float ManagedAvailableCredit(
        float capacity, float publicDebt, float gdp,
        float weeklyIssueShare, float debtIssuedWeekly)
    {
        var weeklyCap = gdp * Math.Clamp(weeklyIssueShare, 0f, 0.05f) - debtIssuedWeekly;
        return MathF.Max(0f, MathF.Min(capacity - publicDebt, weeklyCap));
    }

    internal static void ManagedEvaluateTradeCost(
        float imports, float relieved, float price,
        float importMarkupDefine, float reliefDefine, float tariffRate,
        out float importMarkup, out float duty)
    {
        importMarkup = imports > 0.001f
            ? 1f + (importMarkupDefine - 1f) * (1f - M.Clamp01(reliefDefine * M.Clamp01(relieved / imports)))
            : 1f;
        duty = MathF.Max(0f, imports - relieved) * price * tariffRate;
    }

    internal static float ManagedTaxCompliance(
        bool hasPopulation, float burden, float taxEvasion, float taxTolerance) =>
        !hasPopulation || taxEvasion <= 0f
            ? 1f
            : 1f / (1f + taxEvasion * MathF.Max(0f, burden - taxTolerance));

    internal static float ManagedEvaluateBudgetLineAmount(
        float basis, float rate, float yieldValue, float scale,
        bool isDebtInterest, float publicDebt, float debtSpreadAnnual,
        bool isLevy, float compliance, float taxEfficiency, bool allowNegative)
    {
        var amount = basis * rate * yieldValue * scale;
        if (isDebtInterest) amount += publicDebt * debtSpreadAnnual / 52f;
        if (isLevy) amount *= compliance * taxEfficiency;
        return allowNegative ? amount : MathF.Max(0f, amount);
    }
}
