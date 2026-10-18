// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

/// Explicit values mirror the existing C# readout; this library owns no campaign state.
type Reason =
    | Ready = 0
    | DirectOrder = 1
    | PlanPaused = 2
    | Recovering = 3
    | LowOrganisation = 4
    | LowSupply = 5
    | NoAssaultOrder = 6
    | Unavailable = 7

/// Value-only result. Singles preserve the existing simulation's float32 arithmetic.
[<Struct>]
type Assessment =
    { Ready: bool
      Reason: Reason
      Organisation: single
      MinimumOrganisation: single
      Supply: single
      MinimumSupply: single
      RecoveryDays: int }

/// Deterministic game rules. No Godot, IO, RNG or reference to the mutable campaign.
[<AbstractClass; Sealed>]
type AssaultRules private () =
    static member Assess(
        hasCampaign: bool, hasArmy: bool, hasAssaultOrder: bool,
        planActive: bool, directOrder: bool, readyDay: int, elapsedDays: int,
        organisation: single, supply: single,
        minimumOrganisation: single, minimumSupply: single) : Assessment =

        let floor value fallback =
            if System.Single.IsFinite(value) then System.Math.Clamp(value, 0.0f, 1.0f)
            else fallback

        // Same readout normalization as BattleReadout.Unit, including nonfinite values.
        let unit value =
            let finite = if System.Single.IsFinite(value) then System.Math.Max(0.0f, value) else 0.0f
            System.Math.Clamp(finite, 0.0f, 1.0f)

        if not hasCampaign || not hasArmy then
            { Ready = false; Reason = Reason.Unavailable
              Organisation = 0.0f; MinimumOrganisation = 0.16f
              Supply = 0.0f; MinimumSupply = 0.20f; RecoveryDays = 0 }
        else
            let organisationFloor = floor minimumOrganisation 0.16f
            let supplyFloor = floor minimumSupply 0.20f
            let shownOrganisation = unit organisation
            let shownSupply = unit supply
            // Widen before subtraction; either input can be any signed int32.
            let days = int (System.Math.Min(int64 System.Int32.MaxValue,
                                           System.Math.Max(0L, int64 readyDay - int64 elapsedDays)))
            let reason =
                if not hasAssaultOrder then Reason.NoAssaultOrder
                elif not planActive then Reason.PlanPaused
                elif days > 0 then Reason.Recovering
                elif not (System.Single.IsFinite(organisation)) || not (System.Single.IsFinite(supply)) then Reason.Unavailable
                elif directOrder then Reason.DirectOrder
                elif organisationFloor > 0.0f && shownOrganisation <= organisationFloor then Reason.LowOrganisation
                elif supplyFloor > 0.0f && shownSupply <= supplyFloor then Reason.LowSupply
                else Reason.Ready

            { Ready = reason = Reason.Ready || reason = Reason.DirectOrder
              Reason = reason; Organisation = shownOrganisation
              MinimumOrganisation = organisationFloor; Supply = shownSupply
              MinimumSupply = supplyFloor; RecoveryDays = days }
