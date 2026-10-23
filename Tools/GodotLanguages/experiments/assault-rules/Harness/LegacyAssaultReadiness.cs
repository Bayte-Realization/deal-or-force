// SPDX-License-Identifier: GPL-3.0-or-later
// Frozen C# oracle from ea4510cf, before the production F# migration. Test-only.
using System;
using Reason = DealOrForce.Simulation.AssaultReadiness.Reason;

namespace DealOrForce.Simulation;

internal static class LegacyAssaultReadiness
{
    public const float MinimumOrganisation = .16f;
    public const float MinimumSupply = .20f;

    /// <summary>Reads live condition; consumes no RNG and alters no orders, stores or campaign state.</summary>
    public static AssaultAssessment Assess(Campaign campaign, Army army)
    {
        if (campaign is null || army is null)
            return new(false, Reason.Unavailable, 0, .16f, 0, .20f, 0);
        var organisationFloor = Floor(campaign.Defines.AutonomousAssaultMinimumOrganisation, .16f);
        var supplyFloor = Floor(campaign.Defines.AutonomousAssaultMinimumSupply, .20f);
        var organisation = BattleReadout.Unit(army.Organisation);
        var supply = BattleReadout.Unit(army.SupplyLevel);
        var days = (int)Math.Min(int.MaxValue, Math.Max(0L, (long)army.ReadyDay - campaign.ElapsedDays));
        var reason = army.OrderKind is not (ArmyOrder.AdvanceFront or ArmyOrder.Spearhead) ? Reason.NoAssaultOrder :
            !army.PlanActive ? Reason.PlanPaused :
            days > 0 ? Reason.Recovering :
            !float.IsFinite(army.Organisation) || !float.IsFinite(army.SupplyLevel) ? Reason.Unavailable :
            army.DirectOrder ? Reason.DirectOrder :
            organisationFloor > 0 && organisation <= organisationFloor ? Reason.LowOrganisation :
            supplyFloor > 0 && supply <= supplyFloor ? Reason.LowSupply : Reason.Ready;
        return new(reason is Reason.Ready or Reason.DirectOrder, reason,
            organisation, organisationFloor, supply, supplyFloor, days);
    }

    private static float Floor(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}
