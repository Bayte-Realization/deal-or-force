using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Who holds what, counted per region in one pass.
///
/// Several systems want the same two figures about a region - how much of it its owner
/// still controls, and how much of it has an enemy on the far side of a border - and each
/// of them used to work it out by walking every province in the world. There are 37,909 of
/// them and 1,826 regions; done per region per system per day, that is the most expensive
/// arithmetic in the game and none of it is interesting.
/// </summary>
public struct RegionControl
{
    public int Provinces;
    public int Held;
    public int Bordered;

    /// <summary>How much of the region somebody else is standing on.</summary>
    public readonly float OccupiedShare => Provinces == 0 ? 0f : (Provinces - Held) / (float)Provinces;

    /// <summary>How much of it is next to ground an enemy holds.</summary>
    public readonly float BorderedShare => Provinces == 0 ? 0f : Bordered / (float)Provinces;
}

public static class ControlTally
{
    /// <summary>
    /// Count one country's regions. `enemies` may be empty, in which case nothing is
    /// bordered and the tally is only about occupation.
    /// </summary>
    // ---------------------------------------------------------------- the whole world ----

    private static Campaign _worldCampaign;
    private static int _worldDay = int.MinValue;
    private static int _worldRevision = int.MinValue;
    // Keyed by owner AND region, not by region alone. Nine of the world's regions are cut
    // by a border and hold provinces belonging to two countries; the per-country call counts
    // only its own, and a tally keyed by region alone would hand both owners the sum. That is
    // three regions' worth of difference in what rises to defend them, which is a different
    // game rather than a faster one.
    private static readonly Dictionary<long, RegionControl> WorldTally = new();

    private static long Key(ushort owner, int region) => ((long)owner << 32) | (uint)region;
    private static readonly Dictionary<ushort, HashSet<ushort>> HostileTo = new();
    private static readonly HashSet<ushort> NoOne = new();

    /// <summary>
    /// Every region in the world, counted in one pass and kept until something moves.
    ///
    /// The per-country call below is correct and was being used inside a loop over
    /// countries, which walks the map once per country per system per day. This walks it
    /// once. The answer is the same in every respect, including the border count: which
    /// flags count as hostile is decided per owner, from the wars, before the walk starts.
    ///
    /// Stamped with the campaign as well as the day, because the bench steps two peer
    /// campaigns side by side and a tally from one of them is a wrong answer in the other -
    /// the same mistake that once made the army index report another world's divisions.
    /// </summary>
    public static RegionControl World(Campaign campaign, int ownerMapIndex, int region)
    {
        Refresh(campaign);
        WorldTally.TryGetValue(Key((ushort)ownerMapIndex, region), out var tally);
        return tally;
    }

    private static void Refresh(Campaign campaign)
    {
        var topology = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (topology is null || control is null) return;

        if (ReferenceEquals(_worldCampaign, campaign) && _worldDay == campaign.ElapsedDays &&
            _worldRevision == campaign.MapRevision)
            return;

        _worldCampaign = campaign;
        _worldDay = campaign.ElapsedDays;
        _worldRevision = campaign.MapRevision;

        // Who is at war with whom, by map flag, so the walk below can ask the question
        // about a province's owner without touching the war list again.
        HostileTo.Clear();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            foreach (var side in campaign.Nations)
            {
                if (!war.Involves(side.Id)) continue;
                if (!HostileTo.TryGetValue((ushort)side.MapIndex, out var set))
                    HostileTo[(ushort)side.MapIndex] = set = new HashSet<ushort>();
                foreach (var other in campaign.Nations)
                    if (other.Id != side.Id && war.Involves(other.Id)) set.Add((ushort)other.MapIndex);
            }
        }

        WorldTally.Clear();
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            var owner = topology.ProvinceToNation[province];
            if (owner == 0) continue;

            var key = Key(owner, region);
            WorldTally.TryGetValue(key, out var tally);
            tally.Provinces++;
            if (control[province] == owner)
            {
                tally.Held++;
                var enemies = HostileTo.TryGetValue(owner, out var hostile) ? hostile : NoOne;
                if (enemies.Count > 0)
                    foreach (var neighbour in topology.Neighbors[province])
                    {
                        if (neighbour <= 0 || neighbour >= control.Length) continue;
                        var other = control[neighbour];
                        if (other == 0 || other == owner) continue;
                        if (!enemies.Contains(other)) continue;
                        tally.Bordered++;
                        break;
                    }
            }
            WorldTally[key] = tally;
        }
    }

    public static void Of(Campaign campaign, Nation owner, HashSet<ushort> enemies,
        Dictionary<int, RegionControl> into)
    {
        into.Clear();
        var topology = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (topology is null || control is null || owner is null) return;

        var flag = (ushort)owner.MapIndex;
        var look = enemies is not null && enemies.Count > 0;
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            var owned = topology.ProvinceToNation[province] == flag;
            if (!owned) continue;

            into.TryGetValue(region, out var tally);
            tally.Provinces++;
            if (control[province] == flag)
            {
                tally.Held++;
                if (look)
                    foreach (var neighbour in topology.Neighbors[province])
                    {
                        if (neighbour <= 0 || neighbour >= control.Length) continue;
                        var other = control[neighbour];
                        if (other == 0 || other == flag) continue;
                        if (!enemies.Contains(other)) continue;
                        tally.Bordered++;
                        break;
                    }
            }
            into[region] = tally;
        }
    }
}
