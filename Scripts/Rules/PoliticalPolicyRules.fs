// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Refusal codes for political policy / law and agenda adoption, evaluated purely.
type PolicyAdoptRefusal =
    | Allowed = 0
    | MechanicUnavailable = 1
    | AgendaInProgress = 2
    | InvalidRegion = 3
    | InvalidForeignTarget = 4
    | RequiresActiveWar = 5
    | AlreadyEnacted = 6
    | TooManyTargetedLaws = 7
    | CoolingOff = 8
    | InsufficientTreasury = 9

/// Pure deterministic political policy & agenda evaluation rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type PoliticalPolicyRules private () =

    static member EvaluateAdoption(hasNation: bool, hasPolicy: bool, featureEnabled: bool,
                                   isAgenda: bool, activeAgendaIncomplete: bool,
                                   scopeCode: int, ownsRegion: bool, validForeignTarget: bool,
                                   requiresWar: bool, atWar: bool,
                                   alreadyEnacted: bool, lawCount: int, hasSlotKey: bool,
                                   readyOnDay: int, elapsedDays: int,
                                   treasury: single, gdp: single, costShare: single) : PolicyAdoptRefusal =
        let requiredCash =
            if Single.IsFinite gdp && Single.IsFinite costShare then MathF.Max(0.0f, gdp) * costShare
            else 0.0f
        if not hasNation || not hasPolicy || not featureEnabled then PolicyAdoptRefusal.MechanicUnavailable
        elif isAgenda && activeAgendaIncomplete then PolicyAdoptRefusal.AgendaInProgress
        elif scopeCode = 1 && not ownsRegion then PolicyAdoptRefusal.InvalidRegion
        elif scopeCode = 2 && not validForeignTarget then PolicyAdoptRefusal.InvalidForeignTarget
        elif requiresWar && not atWar then PolicyAdoptRefusal.RequiresActiveWar
        elif not isAgenda && alreadyEnacted then PolicyAdoptRefusal.AlreadyEnacted
        elif not isAgenda && lawCount >= 64 && not hasSlotKey then PolicyAdoptRefusal.TooManyTargetedLaws
        elif readyOnDay > elapsedDays then PolicyAdoptRefusal.CoolingOff
        elif not (Single.IsFinite treasury) || treasury < requiredCash then PolicyAdoptRefusal.InsufficientTreasury
        else PolicyAdoptRefusal.Allowed

    /// Catalogue identifiers remain case-sensitive; unknown or absent goals retain
    /// the historical stability fallback. No localised text enters the rule.
    static member GoalKind(goal: string) : int =
        match goal with
        | "living" -> 1
        | "debt_ratio" -> 2
        | "peace" -> 3
        | _ -> 0

    /// Evaluates whether a political agenda's goal is currently satisfied.
    /// goalKind: 0 = stability (default), 1 = living, 2 = debt_ratio, 3 = peace
    static member GoalMet(goalKind: int, goalValue: single,
                          standardOfLiving: single, publicDebt: single, gdp: single,
                          debtCondition: int, atWar: bool, stability: single,
                          defaultDebtCondition: int) : bool =
        match goalKind with
        | 1 -> standardOfLiving >= goalValue
        | 2 ->
            let denom = MathF.Max(0.01f, gdp)
            (publicDebt / denom) <= goalValue && debtCondition < defaultDebtCondition
        | 3 -> not atWar
        | _ -> stability >= goalValue

    /// Compatibility overload for callers of the original pure-rule API.
    static member GoalMet(goalKind: int, goalValue: single,
                          standardOfLiving: single, publicDebt: single, gdp: single,
                          debtCondition: int, atWar: bool, stability: single) : bool =
        PoliticalPolicyRules.GoalMet(goalKind, goalValue, standardOfLiving, publicDebt,
                                    gdp, debtCondition, atWar, stability, 2)
