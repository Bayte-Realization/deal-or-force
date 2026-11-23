using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// One number for the whole world.
///
/// Peers do not exchange the world; they exchange the orders given to it and each runs the
/// same simulation over the same seed. That works exactly as long as the two simulations
/// agree, and the failure when they stop agreeing is the worst kind: no error, no crash,
/// just two players slowly coming to believe different things about where the front is.
/// By the time anybody notices, neither save can be trusted and neither can be blamed.
///
/// So both sides compute this, the host says what it has, and a client that disagrees asks
/// for the world again rather than playing on in a different one. It is made of the things
/// a divergence shows up in first - who holds what, where every formation is, and what each
/// country is worth - and it is ordered by id rather than by whatever order the lists are
/// in, because list order is itself a thing two runs could differ about.
/// </summary>
public static partial class CampaignFingerprint
{
    public static ulong Of(Campaign campaign) => Of(campaign, longCampaignClock: true);

    /// <summary>
    /// The same number, with or without the long-campaign clock (Nation.GoneSinceDay): when each
    /// dead country was first found gone. Lockstep compares it with the clock - it decides the day
    /// a country is retired, and two machines that disagree about it would otherwise agree for
    /// five years and then split. Without it is for the bench alone: a build with the diet against
    /// a build or a run without one, where only the diet keeps that clock and nothing else may differ.
    /// </summary>
    public static ulong Of(Campaign campaign, bool longCampaignClock)
    {
        if (campaign is null) return 0;
        var hash = 14695981039346656037UL;

        // Hours, not days. A step can be a fraction of a day or several of them, so two
        // worlds can agree on the date and still be a step apart - which is a divergence
        // that would otherwise be invisible until it had grown into a real one.
        Mix(ref hash, campaign.ElapsedHours);
        MixHomelandsAndGlobalWorldRules(campaign, ref hash);
        MixNuclearAndVolunteers(campaign, ref hash);
        MixMilitaryRules(campaign, ref hash);
        MixDiplomacyRules(campaign, ref hash);
        MixEconomyAiRules(campaign, ref hash);
        MixMovementRules(campaign, ref hash);
        MixCivilWarOutbreakBudget(campaign, ref hash);
        MixUltimatums(campaign, ref hash);
        MixWorldCreditCycle(campaign, ref hash);
        MixSeaLifts(campaign, ref hash);
        MixLandArrangementsAndLeases(campaign, ref hash);
        MixDealOffersAndAssistance(campaign, ref hash);
        MixBtaBridgeAndScenarioFeatures(campaign, ref hash);
        MixStateCountAndInstantConstruction(campaign, ref hash);
        MixRelationsAndDiplomacyTables(campaign, ref hash);
        MixOrganizationsAndArmsShipments(campaign, ref hash);
        MixGrandProjects(campaign, ref hash);
        MixDormantRelationsOrder(campaign, ref hash);

        foreach (var nation in Ordered(campaign.Nations, nation => nation.Id))
            MixNation(nation, longCampaignClock, ref hash);

        foreach (var state in Ordered(campaign.States, state => state.MapIndex))
            MixState(state, ref hash);

        MixFrontLinesAndArmyGroups(campaign, ref hash);
        MixArmiesAndBattleWithdrawals(campaign, ref hash);
        MixArmyPockets(campaign, ref hash);
        MixPartisansAndSecessionEvents(campaign, ref hash);
        MixWars(campaign, ref hash);
        MixTradeAttribution(campaign, ref hash);
        MixLongRangeForces(campaign, ref hash);
        MixProvinceControllers(campaign, ref hash);
        return hash;
    }

    private static void MixNation(Nation nation, bool longCampaignClock, ref ulong hash)
    {
        MixNationIdentity(nation, longCampaignClock, ref hash);
        MixNationFinances(nation, ref hash);
        MixNationLawsAndCourse(nation, ref hash);
        MixNationCrisesAndParliament(nation, ref hash);
        MixNationCommitments(nation, ref hash);
        MixNationOutputSummary(nation, ref hash);
        MixNationMovement(nation, ref hash);
        MixNationStabilityAndFailure(nation, ref hash);
        MixNationDiplomacyAndCovert(nation, ref hash);
        MixNationAddressAndMigration(nation, ref hash);
        MixNationDoctrinesAndExperience(nation, ref hash);
        MixNationDemographicCollapse(nation, ref hash);
        MixNationSubjectState(nation, ref hash);
        MixNationCosmeticsAndRulers(nation, ref hash);
        MixNationStockpiles(nation, ref hash);
        MixNationElectionsAndCabinet(nation, ref hash);
    }

    private static void MixState(ScenarioState state, ref ulong hash)
    {
        Mix(ref hash, state.MapIndex);
        MixStateEconomyAndLivelihood(state, ref hash);
        MixStateAdministration(state, ref hash);
        MixStatePopulationAndPolitics(state, ref hash);
        MixStateBordersAndAutonomy(state, ref hash);
    }

    private static void Mix(ref ulong hash, long value)
    {
        for (var shift = 0; shift < 64; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * 1099511628211UL;
    }

    private static void MixFloat(ref ulong hash, float value) => Mix(ref hash, BitConverter.SingleToInt32Bits(value));

    private static void MixText(ref ulong hash, string value)
    {
        foreach (var character in value ?? "") Mix(ref hash, character);
        Mix(ref hash, -8);
    }

    private static void MixFloatDictionary(ref ulong hash, string label, Dictionary<int, float> table)
    {
        if (table is not { Count: > 0 }) return;
        MixText(ref hash, label);
        Mix(ref hash, table.Count);
        foreach (var entry in table.OrderBy(p => p.Key))
        {
            Mix(ref hash, entry.Key);
            MixFloat(ref hash, entry.Value);
        }
    }

    /// <summary>
    /// The same list, in id order, without building a new one every time.
    ///
    /// This is asked of the host every few days with fourteen hundred formations in it, and
    /// sorting by a key with LINQ would allocate a projection and an array each call - which
    /// is exactly the sort of habit the rest of this simulation has just spent an afternoon
    /// getting rid of.
    /// </summary>
    private static IEnumerable<T> Ordered<T>(List<T> source, Func<T, int> id)
    {
        if (source.Count < 2)
        {
            foreach (var item in source) yield return item;
            yield break;
        }
        // Selection by id, which is O(n²) in principle and fine in practice: the lists are
        // short, the comparison is an integer, and it runs once every few days rather than
        // once a tick. Correctness here is worth more than cleverness.
        var taken = new bool[source.Count];
        for (var emitted = 0; emitted < source.Count; emitted++)
        {
            var best = -1;
            for (var index = 0; index < source.Count; index++)
            {
                if (taken[index]) continue;
                if (best >= 0 && id(source[index]) >= id(source[best])) continue;
                best = index;
            }
            if (best < 0) yield break;
            taken[best] = true;
            yield return source[best];
        }
    }
}
