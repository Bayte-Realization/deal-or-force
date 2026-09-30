using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    /// <summary>
    /// Who holds the region on the first day when it is not its owner - the tag of a country
    /// whose troops stand on ground that is not its own, the Gaza Strip on 1 January 2024.
    /// Set by history ("controller": "ISR"); empty for the ordinary case of an owner holding
    /// its own land. Read once, when the map is attached to a new campaign: after that who
    /// holds a province is the war's business, and a save keeps its own answer.
    /// </summary>
    public string StartController = "";
}

/// <summary>
/// Occupations a bookmark opens with.
///
/// The political map says who owns a province and, in its third column, who holds it - but a
/// province id is a number the map compiler hands out, and every recut hands them all out
/// again, so an occupation written against province 21563 would move to a field in Sudan the
/// next time anybody drew a border. A region's history names the region, and the region is
/// what an occupation is about anyway: this lays the holder over every province of it.
/// </summary>
public static class HeldGround
{
    public static int ApplyToGround(Campaign campaign, ProvinceTopology topology, ushort[] controllers)
    {
        if (campaign is null || topology is null || controllers is null) return 0;
        var held = new Dictionary<int, ushort>();
        foreach (var state in campaign.States)
        {
            if (string.IsNullOrEmpty(state.StartController)) continue;
            var holder = campaign.NationByTag(state.StartController);
            if (holder is null || holder.MapIndex == state.NationMapIndex) continue;
            held[state.MapIndex] = (ushort)holder.MapIndex;
        }
        if (held.Count == 0) return 0;
        var changed = 0;
        for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
        {
            if (topology.IsWater(province)) continue;
            if (!held.TryGetValue(topology.ProvinceToState[province], out var holder)) continue;
            controllers[province] = holder;
            changed++;
        }
        return changed;
    }
}
