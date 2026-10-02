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

    /// <summary>What the country under the gun decides to do about it.</summary>
    public enum Answer { Yield, Refuse }

    /// <summary>
    /// The target's answer, worked out the way the game works everything else out: from
    /// state it already keeps. Nobody is reading a dice roll - the same country, on the
    /// same day, with the same armies, gives the same answer, so a player can learn what
    /// makes a demand credible and a mod can read the rule instead of guessing it.
    /// Tuning lives in the flat defines beside <see cref="Defines.UltimatumYieldStabilityCost"/>,
    /// where a mod's defines file can reach it.
    /// </summary>
    public static Answer Consider(Campaign campaign, Nation demander, Nation target, int stateMapIndex)
    {
        if (campaign is null || demander is null || target is null) return Answer.Refuse;
        if (!CanDemand(campaign, target, stateMapIndex)) return Answer.Refuse;

        var defines = campaign.Defines;
        var mine = AiStatecraft.StrengthOf(campaign, target);
        var theirs = AiStatecraft.StrengthOf(campaign, demander);
        // Somebody else's promised army is part of the answer, the same way it is part of
        // the answer to a declaration of war.
        var defenders = Statecraft.DefendersOf(campaign, target);
        if (defenders > 0) mine *= 1f + MathF.Min(defenders * 0.15f, 0.6f);

        // Beaten before it starts: no allies worth the name and nothing to fight with.
        if (mine <= 0.01f || theirs > mine * defines.UltimatumYieldStrengthRatio) return Answer.Yield;
        // A government that has already lost its people's trust cannot ask them to bleed.
        if (target.Stability < defines.UltimatumYieldStability) return Answer.Yield;
        // Asked by a country it has every reason to hate, it takes its chances.
        var relation = CampaignDiplomacy.Peek(campaign, demander.Id, target.Id);
        if (relation is not null && relation.Opinion <= -defines.UltimatumRefuseGrudge) return Answer.Refuse;
        // Close to peers: refusing is the default, because yielding land costs a government
        // more at home than a war it might survive.
        return Answer.Refuse;
    }

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

/// <summary>
/// Demand a region in the player's name, and let the target answer for itself.
///
/// A command rather than a direct call so it travels the same queue as every other
/// diplomatic act: replayed identically in multiplayer, undoable by the same machinery,
/// and visible to the same listeners.
/// </summary>
public sealed class IssueUltimatumCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public int StateMapIndex { get; init; } = -1;

    public void Apply(Campaign campaign)
    {
        var demander = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (demander is null || target is null) return;
        var state = StateMapIndex >= 0 ? StateMapIndex : campaign.States
            .Where(s => s.NationMapIndex == target.MapIndex)
            .OrderByDescending(s => s.Population)
            .ThenBy(s => s.MapIndex)
            .FirstOrDefault()?.MapIndex ?? -1;
        if (Ultimatum.Consider(campaign, demander, target, state) == Ultimatum.Answer.Yield)
            Ultimatum.Accept(campaign, demander, target, state);
        else
            Ultimatum.Refuse(campaign, demander, target, state);
    }
}
