using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A demand for land that arrives before any war does.
///
/// The game already has every half of this: a peace conference can take a region from a
/// loser, and a declaration of war can be justified or not. What it did not have was the
/// step between them - the demand a country makes while everyone can still pretend this is
/// diplomacy. <see cref="Accept"/> is the deal: land changes hands without a shot, and the
/// target pays for it in stability rather than blood. <see cref="Refuse"/> is the force:
/// the refusal is remembered as a reason for war by the same machinery a reconquest uses.
///
/// Both answers teach the same lesson about the demander - the world watched a country
/// demand land at gunpoint, whichever way it went - so both go through
/// <see cref="Statecraft.RecordAnnexation"/> or the opinion system, never through nothing.
/// </summary>
public static partial class Ultimatum
{
    // Accepted means the operation was valid; Response distinguishes yield/refusal.
    public sealed record Outcome(bool Accepted, string Note);
    public enum Answer { Yield, Refuse }

    public static Answer Consider(Campaign c, Nation demander, Nation target, int region) =>
        Assess(c, demander, target, new Demand { Regions = new[] { region } }).Response;

    // Region eligibility for the picker; the complete package is validated again on send.
    public static bool CanDemand(Campaign c, Nation target, int region)
    {
        if (c?.ProvinceMap is null || target is null ||
            !c.States.Any(s => s.MapIndex == region && s.NationMapIndex == target.MapIndex)) return false;
        var present = false;
        var retained = false;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
        {
            if (c.ProvinceMap.ProvinceToState[p] == region && c.ProvinceMap.ProvinceToNation[p] != target.MapIndex) return false;
            if (c.ProvinceMap.ProvinceToNation[p] != target.MapIndex) continue;
            if (c.ProvinceMap.ProvinceToState[p] != region) { retained = true; continue; }
            if ((c.ProvinceController ?? c.ProvinceMap.ProvinceToNation)[p] != target.MapIndex) return false;
            present = true;
        }
        return present && retained;
    }

    public static Outcome Accept(Campaign c, Nation demander, Nation target, int region) =>
        Resolve(c, demander, target, new Demand { Regions = new[] { region } }, Answer.Yield);
    public static Outcome Refuse(Campaign c, Nation demander, Nation target, int region) =>
        Resolve(c, demander, target, new Demand { Regions = new[] { region } }, Answer.Refuse);
}

/// <summary>A complete, atomically validated package through the replay/network queue.</summary>
public sealed class IssueUltimatumCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    // Compatibility with older recorded one-region commands.
    public int StateMapIndex { get; init; } = -1;
    public int[] StateMapIndices { get; init; } = Array.Empty<int>();
    public float Reparations { get; init; }
    public bool LiftEmbargo { get; init; }
    public bool NonAggression { get; init; }
    public Ultimatum.Outcome Result { get; private set; } = new(false, "No ultimatum was issued.");
    public Ultimatum.Answer? Response { get; private set; }

    public void Apply(Campaign campaign)
    {
        Result = new(false, "No ultimatum was issued.");
        Response = null;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var demander = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        var regions = StateMapIndices ?? Array.Empty<int>();
        if (regions.Length == 0 && StateMapIndex >= 0) regions = new[] { StateMapIndex };
        // Older commands without a region chose the largest eligible one.
        if (regions.Length == 0 && Reparations == 0 && !LiftEmbargo && !NonAggression && target is not null)
        {
            var state = campaign.States.Where(s => Ultimatum.CanDemand(campaign, target, s.MapIndex))
                .OrderByDescending(s => s.Population).ThenBy(s => s.MapIndex).FirstOrDefault();
            if (state is not null) regions = new[] { state.MapIndex };
        }
        var demand = new Ultimatum.Demand { Regions = regions, Reparations = Reparations,
            LiftEmbargo = LiftEmbargo, NonAggression = NonAggression };
        var assessment = Ultimatum.Assess(campaign, demander, target, demand);
        if (!assessment.Valid) { Result = new(false, assessment.Reason); return; }
        Response = assessment.Response;
        Result = Ultimatum.Resolve(campaign, demander, target, demand, assessment.Response);
    }
}
