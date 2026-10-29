// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Refusal codes for bilateral diplomatic acts, evaluated purely without mutating campaign state.
type DiplomaticActRefusal =
    | Allowed = 0
    | InvalidTarget = 1
    | AtWar = 2
    | PendingOffer = 3
    | Cooldown = 4
    | NotEmbargoing = 5
    | TalksAlreadyActive = 6
    | OpinionTooLow = 7
    | InsufficientTreasury = 8

/// Pure deterministic diplomatic act pricing, willingness, and refusal rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type DiplomaticActRules private () =

    /// Minimum opinion required for a partner to receive a diplomatic act proposal.
    /// actKind matches DealOrForce.Simulation.DiplomaticAct integer values (0..16).
    static member MinimumOpinion(actKind: int) : single =
        match actKind with
        | 11 -> 15.0f   // IntelligenceSharing
        | 14 -> 20.0f   // TechnologyLicence
        | 15 -> 5.0f    // AirspaceCooperation
        | 10 -> -10.0f  // StudentExchange
        | 12 -> -20.0f  // BorderCommission
        | 13 -> -40.0f  // DisasterRelief
        | _ -> -100.0f

    /// Treasury cost of an act for the proposing country.
    static member Price(actKind: int, baseCost: single, subsidyCost: single) : single =
        let c = if Single.IsFinite baseCost then MathF.Max(0.0f, baseCost) else 0.35f
        let s = if Single.IsFinite subsidyCost then MathF.Max(0.0f, subsidyCost) else 1.50f
        match actKind with
        | 5 -> s          // Subsidy
        | 8 -> c * 1.5f   // StateVisit
        | 14 -> c * 2.5f  // TechnologyLicence
        | 13 -> s         // DisasterRelief
        | 9 -> c * 1.2f   // PortVisit
        | _ -> c

    /// AI willingness score for accepting a diplomatic act.
    /// stance: 0 = Neutral, 1 = Friendly, 2 = Allied, 3 = Rival, 4 = AtWar
    static member Willingness(opinion: single, stance: int, actKind: int) : single =
        let op = if Single.IsFinite opinion then opinion else 0.0f
        let mutable value = op / 100.0f
        if stance = 2 then value <- value + 0.25f
        if stance = 3 then value <- value - 0.35f
        if actKind = 5 then value <- value - 0.15f
        if actKind = 4 then value <- value + 0.10f
        value

    /// Pure eligibility gate for proposing a diplomatic act.
    static member EvaluateRefusal(validActors: bool, atWar: bool, pendingOffer: bool,
                                  availableDay: int, elapsedDays: int,
                                  actKind: int, embargoByTarget: bool,
                                  talksUntilDay: int, opinion: single,
                                  treasury: single, price: single) : DiplomaticActRefusal =
        if not validActors then DiplomaticActRefusal.InvalidTarget
        elif atWar then DiplomaticActRefusal.AtWar
        elif pendingOffer then DiplomaticActRefusal.PendingOffer
        elif availableDay > elapsedDays then DiplomaticActRefusal.Cooldown
        elif actKind = 4 && not embargoByTarget then DiplomaticActRefusal.NotEmbargoing
        elif actKind = 6 && talksUntilDay > elapsedDays then DiplomaticActRefusal.TalksAlreadyActive
        elif opinion < DiplomaticActRules.MinimumOpinion(actKind) then DiplomaticActRefusal.OpinionTooLow
        elif not (Single.IsFinite treasury) || treasury + 0.001f < price then DiplomaticActRefusal.InsufficientTreasury
        else DiplomaticActRefusal.Allowed
