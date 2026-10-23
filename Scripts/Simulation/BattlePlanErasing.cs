using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Delete one owned plan, or only its offensive objective. Safe for stale rosters.</summary>
public sealed class DeleteBattlePlanCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FrontLineId { get; init; }
    public bool ObjectiveOnly { get; init; }

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var line = campaign.FrontLines.Find(l => l.Id == FrontLineId && l.NationId == IssuerId);
        if (line is null || ObjectiveOnly && line.Kind != FrontLineKind.Offensive) return;
        // Never depend solely on a cache that can contain dead, detached or transferred units.
        var members = campaign.Armies.Where(a => a.NationId == IssuerId && a.FrontLineId == line.Id).ToArray();
        if (ObjectiveOnly)
        {
            line.Kind = FrontLineKind.Defence;
            line.Provinces.Clear();
            line.Provinces.UnionWith(line.SourceProvinces);
            line.SourceProvinces.Clear();
            line.ObjectiveSequence.Clear();
            line.Objectives.Clear();
            line.Posts.Clear();
            line.PostsDay = int.MinValue;
            line.BorderRevision = int.MinValue;
            line.ArmyIds.Clear();
            line.ArmyIds.AddRange(members.Select(a => a.Id));
        }
        else campaign.FrontLines.Remove(line);
        foreach (var army in members)
        {
            if (!ObjectiveOnly) army.FrontLineId = -1;
            army.OrderKind = ObjectiveOnly ? ArmyOrder.DefendFront : ArmyOrder.Manual;
            army.PlanActive = false;
            army.DirectOrder = false;
            army.SupportOnly = false;
            army.ObjectiveStateMapIndex = -1;
            new HaltArmyCommand { IssuerId = IssuerId, ArmyId = army.Id }.Apply(campaign);
            // Explicitly erasing a plan also erases its future route. Unlike H, this is
            // not merely "cancel my queued map order", and must work during combat too.
            army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0f;
            army.OrderProvinceId = army.OrderStateMapIndex = -1;
            army.OrderRouteSteps = army.OrderStepsCompleted = 0;
        }
        // Erasing a plan never teleports units or silently withdraws an ongoing battle.
    }
}
