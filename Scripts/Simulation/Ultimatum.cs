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
public static class Ultimatum
{
    public sealed record Outcome(bool Accepted, string Note);

    /// <summary>
    /// Whether this region could even be demanded: it has to be the target's to give. A
    /// demand over somebody else's land threatens nobody, and is not remembered.
    /// </summary>
    public static bool CanDemand(Campaign campaign, Nation target, int stateMapIndex)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || target is null) return false;
        var state = campaign.States.FirstOrDefault(s => s.MapIndex == stateMapIndex);
        return state is not null && state.NationMapIndex == target.MapIndex;
    }

    /// <summary>
    /// The target yielded. Provinces move by the same road a peace conference moves them
    /// (so the bookkeeping - fronts, controllers, chronicle - is the one path), the target
    /// is humiliated rather than bled, and the world notices the taker.
    /// </summary>
    public static Outcome Accept(Campaign campaign, Nation demander, Nation target, int stateMapIndex)
    {
        if (campaign is null || demander is null || target is null)
            return new Outcome(false, "Nobody is demanding.");
        if (!CanDemand(campaign, target, stateMapIndex))
            return new Outcome(false, "They cannot yield what they do not hold.");

        var state = campaign.States.First(s => s.MapIndex == stateMapIndex);
        var provinces = new List<int>();
        for (var province = 1; province <= campaign.ProvinceMap.ProvinceCount; province++)
            if (campaign.ProvinceMap.ProvinceToState[province] == stateMapIndex &&
                campaign.ProvinceMap.ProvinceToNation[province] == (ushort)target.MapIndex)
                provinces.Add(province);
        if (provinces.Count == 0)
            return new Outcome(false, "They hold nothing there to yield.");

        // The one road land takes, so fronts, controllers and the map agree afterwards.
        StatePartition.Annex(campaign, provinces, demander, asConquest: false);

        // Yielding to a gun costs a government its standing at home - more than losing a
        // battle, because it chose this.
        target.Stability = M.Clamp01(target.Stability - campaign.Defines.UltimatumYieldStabilityCost);
        if (target.Population > 0)
            Statecraft.RecordAnnexation(campaign, demander, state.Population);

        var relation = CampaignDiplomacy.Get(campaign, demander.Id, target.Id);
        OpinionSystem.Remember(campaign, relation, "received_our_ultimatum");
        campaign.Chronicle.Record(campaign, "ultimatum_accepted", demander.Id, target.Id);
        return new Outcome(true, $"{target.Tag} yielded {state.Name}.");
    }

    /// <summary>
    /// The target called the bluff, or took its chances. Nothing moves - but the demand
    /// stung, and the refusal is exactly the reason for war a country writes down: the same
    /// machinery that remembers stolen land now remembers this.
    /// </summary>
    public static Outcome Refuse(Campaign campaign, Nation demander, Nation target, int stateMapIndex)
    {
        if (campaign is null || demander is null || target is null)
            return new Outcome(false, "Nobody is demanding.");
        if (!CanDemand(campaign, target, stateMapIndex))
            return new Outcome(false, "They cannot yield what they do not hold.");

        var relation = CampaignDiplomacy.Get(campaign, demander.Id, target.Id);
        OpinionSystem.Remember(campaign, relation, "received_our_ultimatum");
        OpinionSystem.Remember(campaign, relation, "refused_our_ultimatum");
        campaign.Chronicle.Record(campaign, "ultimatum_refused", demander.Id, target.Id);
        return new Outcome(true, $"{target.Tag} refused.");
    }
}
