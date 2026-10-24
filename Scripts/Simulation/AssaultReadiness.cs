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
        // A grouping that has eaten through its cauldron is on the organisation floor.
        // A direct order does not buy it another attack: player and AI share the dry-out.
        var cutoffDays = Math.Max(1, campaign.Defines.SupplyCutoffDays);
        if (army.CutOff && army.DaysCutOff >= cutoffDays)
        {
            var minimumOrganisation = campaign.Defines.AutonomousAssaultMinimumOrganisation;
            var minimumSupply = campaign.Defines.AutonomousAssaultMinimumSupply;
            return new(false, Reason.LowOrganisation, army.Organisation,
                minimumOrganisation > 0f ? minimumOrganisation : MinimumOrganisation,
                army.SupplyLevel, minimumSupply > 0f ? minimumSupply : MinimumSupply, 0);
        }
        // The authoritative rule lives in Rules/AssaultRules.fs. It is a pure predicate over
        // primitives, so the C# side keeps the campaign, the clock and the fallback; if the
        // managed rule ever cannot be loaded or throws, the identical managed path below
        // answers instead. The experiment in Tools/GodotLanguages validated the two against
        // each other in 624,502 cases; this bridge is the deployment of that result.
        if (AssaultRulesHook.TryAssess(true, true,
                army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead,
                army.PlanActive, army.DirectOrder, army.ReadyDay, campaign.ElapsedDays,
                army.Organisation, army.SupplyLevel,
                campaign.Defines.AutonomousAssaultMinimumOrganisation,
                campaign.Defines.AutonomousAssaultMinimumSupply,
                out var assessment))
            return assessment;
        return AssessManaged(campaign, army);
    }

    /// <summary>The managed reference implementation, kept as the fallback and the oracle.</summary>
    internal static AssaultAssessment AssessManaged(Campaign campaign, Army army)
    {
        if (campaign is null || army is null)
            return new(false, Reason.Unavailable, 0, .16f, 0, .20f, 0);
        // C# owns the live world; the value-only F# rule owns eligibility and priority.
        var result = Rules.AssaultRules.Assess(true, true,
            army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead,
            army.PlanActive, army.DirectOrder, army.ReadyDay, campaign.ElapsedDays,
            army.Organisation, army.SupplyLevel,
            campaign.Defines.AutonomousAssaultMinimumOrganisation,
            campaign.Defines.AutonomousAssaultMinimumSupply);
        return new(result.Ready, (Reason)(int)result.Reason,
            result.Organisation, result.MinimumOrganisation, result.Supply,
            result.MinimumSupply, result.RecoveryDays);
    }
}
