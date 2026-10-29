// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Value-only result for sovereign credit confidence and annual borrowing spread.
[<Struct>]
type SovereignCreditTerms =
    { TargetConfidence: single
      NextConfidence: single
      AnnualSpread: single }

/// Pure deterministic sovereign finance and macroeconomic rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type EconomyRules private () =

    static member private Clamp01(v: single) : single =
        if not (Single.IsFinite v) then 0.0f
        else Math.Clamp(v, 0.0f, 1.0f)

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
