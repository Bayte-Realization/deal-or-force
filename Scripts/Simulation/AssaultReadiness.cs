using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Minimum organisation for an autonomous defended assault. Zero disables the floor.</summary>
    public float AutonomousAssaultMinimumOrganisation { get; set; } = .16f;

    /// <summary>Minimum supply for an autonomous defended assault. Zero disables the floor.</summary>
    public float AutonomousAssaultMinimumSupply { get; set; } = .20f;
}

/// <summary>Readiness result; the single-bool constructor remains available to integrations.</summary>
public readonly record struct AssaultAssessment(bool Ready, AssaultReadiness.Reason Reason,
    float Organisation, float MinimumOrganisation, float Supply, float MinimumSupply, int RecoveryDays)
{
    public AssaultAssessment(bool ready) : this(ready,
        ready ? AssaultReadiness.Reason.Ready : AssaultReadiness.Reason.Unavailable,
        0, AssaultReadiness.MinimumOrganisation, 0, AssaultReadiness.MinimumSupply, 0) { }
}

/// <summary>
/// The minimum condition a formation needs before choosing to assault defended ground.
/// This is not a battle forecast: passing it says nothing about local superiority.
/// A direct order expresses the player's decision to accept the condition of the unit;
/// a paused plan and a recovery interval remain paused even under a direct order.
/// </summary>
public static class AssaultReadiness
{
    public const float MinimumOrganisation = .16f;
    public const float MinimumSupply = .20f;
    public enum Reason
    {
        Ready, DirectOrder, PlanPaused, Recovering, LowOrganisation, LowSupply,
        NoAssaultOrder, Unavailable
    }

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
