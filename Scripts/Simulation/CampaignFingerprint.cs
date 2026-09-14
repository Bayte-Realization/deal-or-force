using System;
using System.Collections.Generic;

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
public static class CampaignFingerprint
{
    public static ulong Of(Campaign campaign)
    {
        if (campaign is null) return 0;
        var hash = 14695981039346656037UL;

        void Mix(long value)
        {
            for (var shift = 0; shift < 64; shift += 8)
                hash = (hash ^ (byte)(value >> shift)) * 1099511628211UL;
        }

        void MixFloat(float value) => Mix(BitConverter.SingleToInt32Bits(value));

        // Hours, not days. A step can be a fraction of a day or several of them, so two
        // worlds can agree on the date and still be a step apart - which is a divergence
        // that would otherwise be invisible until it had grown into a real one.
        Mix(campaign.ElapsedHours);
        foreach (var character in campaign.ScenarioId ?? "2024") Mix(character);
        Mix(-1);

        // The region list is no longer fixed: a border cuts a region in two and a border
        // going away puts it back. Two worlds that disagree about how many regions exist
        // disagree about everything downstream of that.
        Mix(campaign.States.Count);

        foreach (var nation in Ordered(campaign.Nations, nation => nation.Id))
        {
            Mix(nation.Id);
            foreach (var character in nation.FormedNationId ?? "") Mix(character);
            Mix(-1);
            Mix(nation.FormedNationDay);
            Mix(nation.Population);
            MixFloat(nation.Treasury);
            MixFloat(nation.Gdp);
            MixFloat(nation.Stability);
            // What the country is beyond its conditions, and how it has been governed. Both
            // feed stability, and a world that disagrees about them is a world whose
            // stability is about to disagree too.
            MixFloat(nation.StabilityBias);
            MixFloat(nation.StabilityGovernance);
            MixFloat(nation.WarSupport);
            MixFloat(nation.ManpowerReserve);
            // When it last came apart, which is what decides whether it may again.
            Mix(nation.LastSecessionDay);
        }

        // How far each region has gone towards leaving. Two peers that disagree about this
        // agree about everything for years and then one of them loses Tatarstan - which is
        // the worst shape a desync can take, because nothing before the moment it shows
        // looks wrong at all.
        foreach (var state in Ordered(campaign.States, state => state.MapIndex))
        {
            if (state.SecessionPressure <= 0f) continue;
            Mix(state.MapIndex);
            MixFloat(state.SecessionPressure);
        }
        foreach (var army in Ordered(campaign.Armies, army => army.Id))
        {
            Mix(army.Id);
            Mix(army.ProvinceId);
            Mix(army.Personnel);
            MixFloat(army.Organisation);
            MixFloat(army.Strength);
            MixFloat(army.Entrenchment);
            Mix(army.Route.Count);
        }
        foreach (var war in Ordered(campaign.Wars, war => war.Id))
        {
            Mix(war.Id);
            Mix(war.Ended ? 1 : 0);
            MixFloat(war.OccupationScore);
            MixFloat(war.BattleScore);
        }
        var controllers = campaign.ProvinceController;
        if (controllers is null) return hash;
        for (var province = 0; province < controllers.Length; province++) Mix(controllers[province]);
        return hash;
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
