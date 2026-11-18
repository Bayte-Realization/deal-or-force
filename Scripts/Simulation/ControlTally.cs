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

    /// <summary>
    /// One campaign's tally tables. They used to be static scratch stamped with the last
    /// campaign that asked, which is correct for exactly one world at a time: a second
    /// campaign stepping beside the first had its occupations answered from whichever
    /// tally happened to be warm - rebuilt at a different hour of its own day than the
    /// solo run would have used, after wars the other world had already seen. Per-world
    /// tables make "kept until something moves" mean the same thing in both runs.
    /// </summary>
    internal sealed class TallyState
    {
        internal int Day = int.MinValue;
        internal int Revision = int.MinValue;
        // Kept per owner AND region, not by region alone. Nine of the world's regions are cut by
        // a border and hold provinces belonging to two countries; the per-country call counts only
        // its own, and a tally keyed by region alone would hand both owners the sum. That is three
        // regions' worth of difference in what rises to defend them, which is a different game
        // rather than a faster one.
        //
        // Held as a slot per region for the first owner met in it, and a dictionary only for the
        // rest. The dictionary alone cost two hash lookups per province and a third for the
        // owner's enemies - a hundred thousand of them a day, which made this walk the whole of
        // the militia pass and eight per cent of a campaign day.
        internal RegionControl[] ByRegion = Array.Empty<RegionControl>();
        internal ushort[] RegionOwner = Array.Empty<ushort>();
        internal readonly Dictionary<long, RegionControl> SplitRegions = new();
        internal readonly Dictionary<ushort, HashSet<ushort>> HostileTo = new();

        /// <summary>The same as <see cref="HostileTo"/>, by flag: null for a country nobody is fighting.</summary>
        internal HashSet<ushort>[] HostileByFlag = Array.Empty<HashSet<ushort>>();
    }

    private static long Key(ushort owner, int region) => ((long)owner << 32) | (uint)region;

    /// <summary>
    /// Every region in the world, counted in one pass and kept until something moves.
    ///
    /// The per-country call below is correct and was being used inside a loop over
    /// countries, which walks the map once per country per system per day. This walks it
    /// once. The answer is the same in every respect, including the border count: which
    /// flags count as hostile is decided per owner, from the wars, before the walk starts.
    /// </summary>
    public static RegionControl World(Campaign campaign, int ownerMapIndex, int region)
    {
        // A world with no map yet - one being dealt, before its provinces are attached - has
        // nothing to count. Refresh leaves the tally alone for it, and the tally it leaves is the
        // last campaign's: the second campaign of a session would have been dealt the first
        // one's occupations.
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null) return default;
        var tally = campaign.ControlTallyTables ??= new TallyState();
        Refresh(tally, campaign);
        if (region < 0 || region >= tally.RegionOwner.Length) return default;
        var owner = (ushort)ownerMapIndex;
        if (tally.RegionOwner[region] == owner) return tally.ByRegion[region];
        tally.SplitRegions.TryGetValue(Key(owner, region), out var split);
        return split;
    }

    /// <summary>Where this owner's count for this region is kept, made if it is not there yet.</summary>
    private static ref RegionControl Slot(TallyState tally, ushort owner, int region)
    {
        ref var first = ref tally.RegionOwner[region];
        if (first == 0) first = owner;
        if (first == owner) return ref tally.ByRegion[region];
        return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            tally.SplitRegions, Key(owner, region), out _);
    }

    [ThreadStatic] private static List<ushort> _attackersScratch;
    [ThreadStatic] private static List<ushort> _defendersScratch;

    private static void Refresh(TallyState tally, Campaign campaign)
    {
        var topology = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (topology is null || control is null) return;

        if (tally.Day == campaign.ElapsedDays && tally.Revision == campaign.MapRevision)
            return;

        tally.Day = campaign.ElapsedDays;
        tally.Revision = campaign.MapRevision;

        // Who is at war with whom, by map flag, so the walk below can ask the question
        // about a province's owner without touching the war list again.
        foreach (var flag in tally.HostileTo.Keys) tally.HostileByFlag[flag] = null;
        tally.HostileTo.Clear();
        using (SimProfiler.Measure("  tally:wars"))
        {
            // Collect hostile map flags directly from each war's belligerent rosters instead of
            // scanning Nations x Nations per war, preserving exact war-order insertion into HostileTo.
            var attackers = _attackersScratch ??= new List<ushort>(8);
            var defenders = _defendersScratch ??= new List<ushort>(8);
            foreach (var war in Warfare.OpenWars(campaign))
            {
                if (war.Ended) continue;
                attackers.Clear();
                defenders.Clear();
                void AddSide(int id, bool attacking)
                {
                    if (campaign.NationById(id) is not { } side) return;
                    var flag = (ushort)side.MapIndex;
                    if (attacking) attackers.Add(flag);
                    else defenders.Add(flag);
                }
                AddSide(war.AttackerId, true);
                foreach (var ally in war.AttackerAllies) AddSide(ally, true);
                AddSide(war.DefenderId, false);
                foreach (var ally in war.DefenderAllies) AddSide(ally, false);
                if (attackers.Count == 0 || defenders.Count == 0) continue;
                for (var ai = 0; ai < attackers.Count; ai++)
                {
                    var af = attackers[ai];
                    if (!tally.HostileTo.TryGetValue(af, out var set))
                        tally.HostileTo[af] = set = new HashSet<ushort>();
                    for (var di = 0; di < defenders.Count; di++)
                    {
                        var df = defenders[di];
                        if (df != af) set.Add(df);
                    }
                }
                for (var di = 0; di < defenders.Count; di++)
                {
                    var df = defenders[di];
                    if (!tally.HostileTo.TryGetValue(df, out var set))
                        tally.HostileTo[df] = set = new HashSet<ushort>();
                    for (var ai = 0; ai < attackers.Count; ai++)
                    {
                        var af = attackers[ai];
                        if (af != df) set.Add(af);
                    }
                }
            }
        }

        // Every flag and every region a ushort can name, allocated once: a few hundred
        // kilobytes against a walk of the map every day.
        if (tally.RegionOwner.Length == 0)
        {
            tally.RegionOwner = new ushort[ushort.MaxValue + 1];
            tally.ByRegion = new RegionControl[ushort.MaxValue + 1];
            tally.HostileByFlag = new HashSet<ushort>[ushort.MaxValue + 1];
        }
        foreach (var (flag, enemies) in tally.HostileTo) tally.HostileByFlag[flag] = enemies.Count > 0 ? enemies : null;
        Array.Clear(tally.RegionOwner);
        Array.Clear(tally.ByRegion);
        tally.SplitRegions.Clear();

        using (SimProfiler.Measure("  tally:walk"))
        {
            // The walk stays serial: at ~0.5 ms a day the per-province work is far below
            // the hand-off cost, and the parallel version measured four times slower
            // (13.3 ms against 3.2 ms over six rebuilds). The wars loop above is the half
            // worth threading: its work items are whole wars, not provinces.
            {
                for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
                {
                    var region = topology.ProvinceToState[province];
                    if (region == 0) continue;
                    var owner = topology.ProvinceToNation[province];
                    if (owner == 0) continue;

                    ref var count = ref Slot(tally, owner, region);
                    count.Provinces++;
                    if (control[province] != owner) continue;
                    count.Held++;
                    var enemies = tally.HostileByFlag[owner];
                    if (enemies is null) continue;
                    foreach (var neighbour in topology.Neighbors[province])
                    {
                        if (neighbour <= 0 || neighbour >= control.Length) continue;
                        var other = control[neighbour];
                        if (other == 0 || other == owner) continue;
                        if (!enemies.Contains(other)) continue;
                        count.Bordered++;
                        break;
                    }
                }
            }
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
