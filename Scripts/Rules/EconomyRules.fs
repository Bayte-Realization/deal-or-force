// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Value-only result for sovereign credit confidence and annual borrowing spread.
[<Struct>]
type SovereignCreditTerms =
    { TargetConfidence: single
      NextConfidence: single
      AnnualSpread: single }

/// Value-only result for market import markup and customs duty evaluation.
[<Struct>]
type TradeCostOutcome =
    { ImportMarkup: single
      Duty: single }

/// Pure deterministic sovereign finance and macroeconomic rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type EconomyRules private () =

    static member private Clamp01(v: single) : single =
        if not (Single.IsFinite v) then 0.0f
        else Math.Clamp(v, 0.0f, 1.0f)

    static member private ClampExact(value: single, minVal: single, maxVal: single) : single =
        if value < minVal then minVal
        elif value > maxVal then maxVal
        else value

    static member private Clamp01Exact(value: single) : single =
        EconomyRules.ClampExact(value, 0.0f, 1.0f)

    /// Evaluates sovereign credit confidence and annual borrowing spread (SovereignFinance.Prepare).
    static member EvaluateCreditTerms(publicDebt: single,
                                      gdp: single,
                                      stability: single,
                                      inflation: single,
                                      debtArrears: single,
                                      paymentArrears: single,
                                      inDefaultOrWorse: bool,
                                      currentConfidence: single,
                                      borrowingCostFactor: single,
                                      maxSpread: single) : SovereignCreditTerms =
        let safeGdp = MathF.Max(0.01f, gdp)
        let ratio = publicDebt / safeGdp
        let rawTarget =
            0.35f
            + stability * 0.55f
            - MathF.Max(0.0f, inflation - 0.04f) * 0.7f
            - ratio * 0.22f
            - (debtArrears + paymentArrears) / safeGdp * 3.0f
        let mutable target = EconomyRules.Clamp01(rawTarget)
        if inDefaultOrWorse then target <- target * 0.35f
        let nextConfidence = currentConfidence + (target - currentConfidence) * 0.08f
        let spreadCap = Math.Clamp(maxSpread, 0.0f, 1.0f)
        let rawSpread =
            ((1.0f - nextConfidence) * 0.05f + MathF.Max(0.0f, ratio - 0.6f) * 0.04f) * borrowingCostFactor
        let spread = Math.Clamp(rawSpread, 0.0f, spreadCap)
        { TargetConfidence = target
          NextConfidence = nextConfidence
          AnnualSpread = spread }

    /// Sovereign debt capacity given GDP, credit confidence and national modifier factor.
    static member DebtCapacity(gdp: single,
                               baseCapacity: single,
                               confidenceCapacity: single,
                               creditConfidence: single,
                               capacityFactor: single) : single =
        MathF.Max(0.0f, gdp)
        * MathF.Max(0.0f, baseCapacity + MathF.Max(0.0f, confidenceCapacity) * creditConfidence)
        * capacityFactor

    /// Weekly borrowing headroom remaining for a solvent state.
    static member AvailableCredit(capacity: single,
                                  publicDebt: single,
                                  gdp: single,
                                  weeklyIssueShare: single,
                                  debtIssuedWeekly: single) : single =
        let weeklyCap = gdp * Math.Clamp(weeklyIssueShare, 0.0f, 0.05f) - debtIssuedWeekly
        MathF.Max(0.0f, MathF.Min(capacity - publicDebt, weeklyCap))

    /// Evaluates effective import markup multiplier and customs duty for one market quote.
    static member EvaluateTradeCost(imports: single,
                                    relieved: single,
                                    price: single,
                                    importMarkupDefine: single,
                                    reliefDefine: single,
                                    tariffRate: single) : TradeCostOutcome =
        let importMarkup =
            if imports > 0.001f then
                let ratio = EconomyRules.Clamp01Exact(relieved / imports)
                let cut = EconomyRules.Clamp01Exact(reliefDefine * ratio)
                1.0f + (importMarkupDefine - 1.0f) * (1.0f - cut)
            else
                1.0f
        let duty = MathF.Max(0.0f, imports - relieved) * price * tariffRate
        { ImportMarkup = importMarkup
          Duty = duty }

    /// Evaluates tax compliance factor from cumulative levy burden, evasion rate, and tolerance.
    static member TaxCompliance(hasPopulation: bool,
                                burden: single,
                                taxEvasion: single,
                                taxTolerance: single) : single =
        if not hasPopulation || taxEvasion <= 0.0f then
            1.0f
        else
            1.0f / (1.0f + taxEvasion * MathF.Max(0.0f, burden - taxTolerance))

    /// Evaluates weekly budget line amount after interest, levy compliance, and sign rules.
    static member EvaluateBudgetLineAmount(basis: single,
                                           rate: single,
                                           yieldValue: single,
                                           scale: single,
                                           isDebtInterest: bool,
                                           publicDebt: single,
                                           debtSpreadAnnual: single,
                                           isLevy: bool,
                                           compliance: single,
                                           taxEfficiency: single,
                                           allowNegative: bool) : single =
        let mutable amount = basis * rate * yieldValue * scale
        if isDebtInterest then
            amount <- amount + publicDebt * debtSpreadAnnual / 52.0f
        if isLevy then
            amount <- amount * (compliance * taxEfficiency)
        if allowNegative then amount else MathF.Max(0.0f, amount)
