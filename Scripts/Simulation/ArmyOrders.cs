namespace DealOrForce.Simulation;

/// <summary>
/// Stop marching, and keep everything else.
///
/// Three different things used to be one button, and the player could not predict which of
/// them they were about to get: stop this march, take this formation off the plan it is
/// working to, and delete the plan itself. They are not degrees of the same action - the
/// first is "hold here for a moment", the second is "you are on your own now", and the
/// third throws away a drawn line that may have a dozen other formations on it.
///
/// This is the first of them. The route is dropped where the formation stands; whatever
/// front or operation it belongs to still has it, and the planner will give it something
/// to do again on its own terms.
/// </summary>
public sealed class HaltArmyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }

    /// <summary>Whether anything was actually stopped, so the interface can say so.</summary>
    public bool Halted { get; private set; }

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;

        // A formation in a battle cannot walk away from it by being told to stop; that is
        // what retreat is, and retreat is the battle's decision rather than the map's.
        Halted = army.Route.Count > 0 || army.OrderProvinceId >= 0;
        if (army.DirectOrder)
        {
            army.OrderKind = ArmyOrder.Manual;
            army.PlanActive = false;
            army.AssignedWarId = -1;
        }
        army.DirectOrder = false;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.MoveProgress = 0f;
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
        army.OrderRouteSteps = 0;
        army.OrderStepsCompleted = 0;
    }
}

/// <summary>
/// Take a formation off its plan and leave it standing where it is.
///
/// The second of the three. After this the front planner will not move it, will not count
/// it towards the line it was holding, and will not quietly put it back on the next tick -
/// which is the behaviour that makes hand-driven spearheads possible at all. It keeps its
/// army and its commander; it simply stops taking orders from the operation.
/// </summary>
public sealed class DetachArmyFromPlanCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }

    public bool Detached { get; private set; }

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;

        Detached = army.OrderKind != ArmyOrder.Manual || army.FrontLineId >= 0;
        army.OrderKind = ArmyOrder.Manual;
        army.FrontLineId = -1;
        army.ObjectiveStateMapIndex = -1;
        // The march it is on is its own business and is left alone: a player who wants it
        // stopped as well has a key for that, and doing both from one press is how the old
        // single button earned its reputation.
    }
}

/// <summary>
/// Give a formation back to the plan.
///
/// The way out of hand control, and the reason hand control is safe to enter. Without it a
/// player who took six divisions off the line to make one attack had no way to hand them
/// back, and either drove them by hand for the rest of the war or deleted and redrew the
/// front - which moves everyone else too.
/// </summary>
public sealed class ReturnArmyToPlanCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }

    /// <summary>The front to stand on, or -1 to let the war decide.</summary>
    public int FrontLineId { get; init; } = -1;

    public bool Returned { get; private set; }

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;

        army.FrontLineId = FrontLineId;
        // Holding the line is the answer that is never wrong. An advance is a decision, and
        // a formation handed back to the plan should not begin one on its own; if the
        // operation is executing, the planner will order it forward on its next pass.
        army.OrderKind = ArmyOrder.DefendFront;
        army.DirectOrder = false;
        army.Route.Clear();
        army.PlannedRoute.Clear();
        army.MoveProgress = 0f;
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
        Returned = true;
    }
}
