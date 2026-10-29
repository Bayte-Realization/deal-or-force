// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Pure deterministic statecraft, union consolidation, and peace-purchase rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type DiplomacyRules private () =

    static member private Clamp01(v: single) : single =
        if not (Single.IsFinite v) then 0.0f
        else Math.Clamp(v, 0.0f, 1.0f)

    /// Computes a government's ambition [0..1] to pursue a national union project (Consolidation.Ambition).
    static member ConsolidationAmbition(authority: single,
                                        isNationalist: bool,
                                        stability: single,
                                        restraint: single) : single =
        let auth = Math.Clamp(authority, -1.0f, 1.0f)
        let natBonus = if isNationalist then 0.25f else 0.0f
        let settled = Math.Clamp((stability - 0.40f) * 0.8f, -0.3f, 0.4f)
        DiplomacyRules.Clamp01(0.36f + 0.35f * auth + natBonus + settled - 0.30f * restraint)

    /// Computes the war-judgement value of a union member holding out (Consolidation.WarValue).
    static member ConsolidationWarValue(ambition: single,
                                        forceAmbition: single,
                                        theirShare: single,
                                        kinship: single,
                                        appetite: single) : single =
        if ambition < forceAmbition || theirShare <= 0.0f || kinship < 0.5f then 0.0f
        else
            DiplomacyRules.Clamp01(ambition)
            * DiplomacyRules.Clamp01(theirShare * 2.0f)
            * kinship
            * appetite

    /// Computes the cash offer a weaker nation puts on the table to buy a non-aggression pact (AiStatecraft.BuyPeace).
    static member BuyPeaceOffer(treasury: single,
                                discretionaryCash: single,
                                ourStrength: single,
                                theirStrength: single) : single =
        if ourStrength >= theirStrength || treasury <= 0.0f || discretionaryCash <= 0.0f then 0.0f
        else
            let gap = Math.Clamp((theirStrength - ourStrength) / Math.Max(0.01f, theirStrength), 0.0f, 1.0f)
            Math.Clamp(treasury * (0.1f + 0.4f * gap), 0.0f, discretionaryCash)
