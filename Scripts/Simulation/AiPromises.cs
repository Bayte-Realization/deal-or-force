using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Public promises made by the governments the player does not run - and by any cabinet the
/// player has handed to the AI (Delegation), which already takes decisions through the same
/// portfolio.
///
/// The mechanic was complete and one-sided: <see cref="MakePromiseCommand"/>, the deadline,
/// the continuous check and the kept/broken effects all worked - and the only government
/// that ever placed a bet was the player's. Two hundred and sixty of them sat through their
/// own crises without saying anything in public, so "kept its promise" and "missed its
/// promise" were news about one country. This is the other half.
///
/// A promise is a gamble on your own performance, and the appetite is read the way the war
/// decisions read character: an opportunistic, volatile government is likelier to buy calm
/// with a public pledge, and a far-sighted one waits. What it promises when it does gamble
/// is the shortest commitment on offer - the least exposure it can choose from what its own
/// crisis has made available, availability being the mechanic's own question
/// (<see cref="GovernmentPromises.BlockReason"/>), so an AI is refused for exactly the
/// reasons the player is and a mod's promise joins the list the day it is written.
///
/// Not claimed here: that the AI measures how far the promised outcome is from being true.
/// Judging a bet would need that reading, and the trait that would use it - cunning - has
/// nothing to bite on yet.
/// </summary>
public static class AiPromises
{
    /// <summary>One pass a day; each country keeps its own day of the week, the spread the
    /// rest of the cabinet already uses, so the whole world does not promise on one morning.</summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("government_promises")) return;
        var day = campaign.ElapsedDays;
        foreach (var nation in campaign.Nations)
        {
            // The player's own government decides for itself, unless its cabinet has been
            // handed to the AI (Delegation) - then it speaks like any other cabinet, because
            // it already spends like one. Movements are not a government any electorate holds
            // to a promise, and an entity without residents has nobody to promise anything to.
            if (!Delegation.RunByAi(nation) || nation.Movement is not null || nation.Population <= 0)
                continue;
            if ((day + nation.Id) % 7 != 0) continue;
            if (nation.Promises.Count >= Math.Max(1, campaign.Defines.MaxConcurrentPromises)) continue;
            var promise = Shortest(campaign, nation);
            if (promise is null) continue;
            if (!campaign.Random.Chance(Appetite(campaign, nation))) continue;
            new MakePromiseCommand { IssuerId = nation.Id, PromiseId = promise.Value.Id }.Apply(campaign);
            Count("promise_made");
        }
    }

    private static void Count(string what) =>
        AiStatecraft.Tally[what] = AiStatecraft.Tally.GetValueOrDefault(what) + 1;

    /// <summary>
    /// The available promise with the shortest term; ties go to the catalogue's own order, so
    /// two machines of one seed choose identically. Availability is the mechanic's question,
    /// not a second copy of it: whatever refuses the player refuses the AI.
    /// </summary>
    private static PromiseDefinition? Shortest(Campaign campaign, Nation nation)
    {
        PromiseDefinition? best = null;
        foreach (var definition in PromiseCatalog.All)
        {
            if (GovernmentPromises.BlockReason(campaign, nation, definition).Length > 0) continue;
            if (best is null || definition.DurationDays < best.Value.DurationDays) best = definition;
        }
        return best;
    }

    /// <summary>
    /// How likely this government is to promise, per its own week, at the plain character.
    ///
    /// Centred on the appetite the data sets - common/defines/75_government_promises carries
    /// the same figure - and bent by the same 1-centred multipliers every other decision
    /// reads. Capped at one half rather than left open, because a modded trait should change
    /// how often a government gambles, not turn the world into a chorus of pledges.
    /// </summary>
    private static float Appetite(Campaign campaign, Nation nation) =>
        M.Clamp(
            M.Max(0f, campaign.Defines.AiPromiseAppetite) *
            AiTemperament.OpportunismOf(campaign, nation) *
            AiTemperament.VolatilityOf(campaign, nation) /
            M.Max(0.05f, AiTemperament.ForesightOf(campaign, nation)),
            0f, .5f);
}
