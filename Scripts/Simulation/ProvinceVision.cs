using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Current contacts, not permanent exploration or permission to command.</summary>
public sealed class ProvinceVision
{
    internal byte[] Reach = Array.Empty<byte>();
    internal readonly Queue<int> Frontier = new();
    internal readonly HashSet<int> CombatContacts = new();
    internal int Stamp;

    // What the country's own ground reveals, kept between rebuilds. It is most of a rebuild
    // - every province the country holds, flooded outwards - and it only changes when who
    // holds what changes, while everything else here (armies, battles, landings) moves by
    // the hour. Kept with the exact controller map it was built from, compared whole.
    internal byte[] TerritoryReach = Array.Empty<byte>();
    internal int TerritoryMapRevision = -1;
    internal int TerritoryMapIndex = -1;
    internal int TerritoryDepth = -1;
    public int Revision { get; internal set; }
    public bool Sees(int province) => province > 0 && province < Reach.Length && Reach[province] > 0;
}

public static partial class CampaignIntel
{
    private sealed class Cache
    {
        public int Epoch;
        public readonly Dictionary<int, ProvinceVision> Viewers = new();
        // Provinces grouped by who controls them (ProvincesHeldBy), with what they were built from.
        public int HeldEpoch = -1, HeldRevision = -1;
        public ushort[] HeldSource;
        /// <summary>The controllers as they stood when the grouping was built.</summary>
        public ushort[] HeldCopy = Array.Empty<ushort>();
        public int[] HeldStart = Array.Empty<int>(), HeldNext = Array.Empty<int>(), HeldProvinces = Array.Empty<int>();

        public bool HasActorIndex;
        public int ActorStamp;
        public long ActorHour;
        public float ActorLandingRange;
        public readonly Dictionary<int, List<int>> ArmiesByNation = new();
        public readonly Dictionary<int, List<int>> BattlesByNation = new();
        public readonly Dictionary<int, int> ArmyProvinceById = new();
        public readonly Dictionary<int, List<int>> InvasionBeachesByNation = new();
    }

    /// <summary>
    /// The provinces a map index controls, in ascending order, from a grouping rebuilt only when
    /// the map or the day changes. An agent network reveals a share of its target's provinces,
    /// and walking all thirty-five thousand of them to find those was four fifths of every
    /// visibility rebuild - a hundred rebuilds a day in a world at war.
    /// </summary>
    internal static ReadOnlySpan<int> ProvincesHeldBy(Campaign c, ushort[] controllers, int mapIndex)
    {
        var cache = Caches.GetOrCreateValue(c);
        // The epoch moves at the end of every step, so the grouping was built again every hour
        // somebody looked - a hundred microseconds, a fifth of every country's sight rebuilt
        // (intel.visibility) - though it is made of nothing but the controllers. When the stamp
        // has moved the controllers are compared with the ones it was built from first: the
        // same controllers are the same grouping, and only a province that really changed
        // hands builds it again.
        if ((cache.HeldEpoch != cache.Epoch || cache.HeldRevision != c.MapRevision || !ReferenceEquals(cache.HeldSource, controllers)) &&
            controllers.AsSpan().SequenceEqual(cache.HeldCopy))
        {
            cache.HeldSource = controllers;
            cache.HeldRevision = c.MapRevision;
            cache.HeldEpoch = cache.Epoch;
        }
        if (cache.HeldEpoch != cache.Epoch || cache.HeldRevision != c.MapRevision || !ReferenceEquals(cache.HeldSource, controllers))
        {
            // Rebuilt every time a province changes hands, which in a world at war is hourly:
            // the arrays are kept and refilled rather than allocated again.
            var top = 0;
            for (var p = 1; p < controllers.Length; p++) if (controllers[p] > top) top = controllers[p];
            if (cache.HeldStart.Length < top + 2) { cache.HeldStart = new int[top + 2]; cache.HeldNext = new int[top + 2]; }
            if (cache.HeldProvinces.Length < controllers.Length) cache.HeldProvinces = new int[controllers.Length];
            var start = cache.HeldStart;
            Array.Clear(start);
            for (var p = 1; p < controllers.Length; p++) start[controllers[p] + 1]++;
            for (var i = 1; i < start.Length; i++) start[i] += start[i - 1];
            var next = cache.HeldNext;
            Array.Copy(start, next, start.Length);
            var provinces = cache.HeldProvinces;
            for (var p = 1; p < controllers.Length; p++) provinces[next[controllers[p]]++] = p;
            cache.HeldSource = controllers;
            if (cache.HeldCopy.Length != controllers.Length) cache.HeldCopy = new ushort[controllers.Length];
            Array.Copy(controllers, cache.HeldCopy, controllers.Length);
            cache.HeldRevision = c.MapRevision;
            cache.HeldEpoch = cache.Epoch;
        }
        if (mapIndex < 0 || mapIndex + 1 >= cache.HeldStart.Length) return ReadOnlySpan<int>.Empty;
        var from = cache.HeldStart[mapIndex];
        return cache.HeldProvinces.AsSpan(from, cache.HeldStart[mapIndex + 1] - from);
    }
    private static readonly ConditionalWeakTable<Campaign, Cache> Caches = new();

    public static void Invalidate(Campaign c)
    {
        if (c is not null && Caches.TryGetValue(c, out var cache)) cache.Epoch++;
    }

    public static ProvinceVision Vision(Campaign c, int viewerId)
    {
        if (c is null) return null;
        var cache = Caches.GetOrCreateValue(c);
        if (!cache.Viewers.TryGetValue(viewerId, out var view))
            cache.Viewers[viewerId] = view = new ProvinceVision();
        var stamp = HashCode.Combine(c.ElapsedDays, c.MapRevision, cache.Epoch, c.Armies.Count,
            c.Battles.Count, c.Fleets.Count, c.ProvinceMap);
        if (view.Revision > 0 && view.Stamp == stamp) return view;
        view.Stamp = stamp;
        view.Revision++;
        using var timer = SimProfiler.Measure("intel.visibility");
        var map = c.ProvinceMap;
        var nation = c.NationById(viewerId);
        if (map is null || nation is null) { view.Reach = Array.Empty<byte>(); return view; }
        if (view.Reach.Length != map.ProvinceCount + 1) view.Reach = new byte[map.ProvinceCount + 1];
        else Array.Clear(view.Reach);
        view.Frontier.Clear();
        view.CombatContacts.Clear();
        var borderDepth = Math.Clamp(c.Defines.BorderVisionDepth, 0, 6);
        var armyDepth = Math.Clamp(c.Defines.ArmyVisionDepth, 0, 6);
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        void Reveal(int province, int depth)
        {
            if (province <= 0 || province >= view.Reach.Length || view.Reach[province] >= depth + 1) return;
            view.Reach[province] = (byte)(depth + 1);
            view.Frontier.Enqueue(province);
        }
        void Flood()
        {
            while (view.Frontier.Count > 0)
            {
                var p = view.Frontier.Dequeue();
                var nextDepth = view.Reach[p] - 2;
                if (nextDepth < 0) continue;
                foreach (var next in map.Neighbors[p]) Reveal(next, nextDepth);
            }
        }

        // The ground first, from the copy when nobody's ground has changed hands since. The
        // flood is a maximum - each province keeps the deepest reach anything gives it - so
        // flooding the armies over a finished territory arrives exactly where flooding
        // everything at once did.
        if (view.TerritoryMapIndex == nation.MapIndex && view.TerritoryDepth == borderDepth &&
            view.TerritoryMapRevision == c.MapRevision &&
            view.TerritoryReach.Length == view.Reach.Length)
        {
            Array.Copy(view.TerritoryReach, view.Reach, view.Reach.Length);
        }
        else
        {
            for (var p = 1; p < controllers.Length; p++)
                if (controllers[p] == nation.MapIndex) Reveal(p, borderDepth);
            Flood();
            if (view.TerritoryReach.Length != view.Reach.Length) view.TerritoryReach = new byte[view.Reach.Length];
            Array.Copy(view.Reach, view.TerritoryReach, view.Reach.Length);
            view.TerritoryMapRevision = c.MapRevision;
            view.TerritoryMapIndex = nation.MapIndex;
            view.TerritoryDepth = borderDepth;
        }
        var hasBattles = EnsureActorIndex(c, cache, stamp, map, Math.Clamp(c.Defines.LandingVisionRangeDegrees, .1f, 3f));
        if (cache.ArmiesByNation.TryGetValue(viewerId, out var myArmies))
            for (var i = 0; i < myArmies.Count; i++) Reveal(myArmies[i], armyDepth);
        if (hasBattles && cache.BattlesByNation.TryGetValue(viewerId, out var myBattles))
        {
            for (var i = 0; i < myBattles.Count; i++)
            {
                var battle = c.Battles[myBattles[i]];
                Reveal(battle.ProvinceId, armyDepth);
                view.CombatContacts.UnionWith(battle.Attackers);
                view.CombatContacts.UnionWith(battle.Defenders);
            }
        }
        if (view.CombatContacts.Count > 0)
            foreach (var contactId in view.CombatContacts)
                if (cache.ArmyProvinceById.TryGetValue(contactId, out var prov)) Reveal(prov, 0);
        // An invasion order alone is NOT reconnaissance across the world. The fleet
        // must actually have approached its beach, or combat must already have begun.
        if (cache.InvasionBeachesByNation.TryGetValue(viewerId, out var beaches))
            for (var i = 0; i < beaches.Count; i++) Reveal(beaches[i], armyDepth);
        Flood();
        // What agents report and what reconnaissance flights photograph: seen, not seen past.
        IntelNetworks.RevealInto(c, viewerId, view.Reach);
        AirReconnaissance.RevealInto(c, viewerId, view.Reach);
        return view;
    }

    private static bool EnsureActorIndex(Campaign c, Cache cache, int stamp, ProvinceTopology map, float landingRange)
    {
        if (cache.HasActorIndex && cache.ActorStamp == stamp && cache.ActorHour == c.ElapsedHours && cache.ActorLandingRange == landingRange)
            return cache.ArmyProvinceById.Count > 0;

        foreach (var list in cache.ArmiesByNation.Values) list.Clear();
        foreach (var list in cache.BattlesByNation.Values) list.Clear();
        cache.ArmyProvinceById.Clear();
        foreach (var list in cache.InvasionBeachesByNation.Values) list.Clear();

        var hasBattles = false;
        for (var i = 0; i < c.Battles.Count; i++)
        {
            var battle = c.Battles[i];
            if (battle.Ended) continue;
            hasBattles = true;
            if (!cache.BattlesByNation.TryGetValue(battle.AttackerId, out var atkList))
                cache.BattlesByNation[battle.AttackerId] = atkList = new List<int>();
            atkList.Add(i);
            if (battle.DefenderId != battle.AttackerId)
            {
                if (!cache.BattlesByNation.TryGetValue(battle.DefenderId, out var defList))
                    cache.BattlesByNation[battle.DefenderId] = defList = new List<int>();
                defList.Add(i);
            }
        }

        for (var i = 0; i < c.Armies.Count; i++)
        {
            var army = c.Armies[i];
            if (!cache.ArmiesByNation.TryGetValue(army.NationId, out var list))
                cache.ArmiesByNation[army.NationId] = list = new List<int>();
            list.Add(army.ProvinceId);
            if (hasBattles) cache.ArmyProvinceById[army.Id] = army.ProvinceId;
        }

        for (var i = 0; i < c.Fleets.Count; i++)
        {
            var fleet = c.Fleets[i];
            if (fleet.Hulls <= 0 || fleet.Mission != FleetMission.Invasion ||
                fleet.TargetProvinceId <= 0 || fleet.TargetProvinceId > map.ProvinceCount) continue;
            var beach = map.CenterOf(fleet.TargetProvinceId);
            if (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, beach.Latitude, beach.Longitude) <= landingRange)
            {
                if (!cache.InvasionBeachesByNation.TryGetValue(fleet.NationId, out var list))
                    cache.InvasionBeachesByNation[fleet.NationId] = list = new List<int>();
                list.Add(fleet.TargetProvinceId);
            }
        }

        cache.HasActorIndex = true;
        cache.ActorStamp = stamp;
        cache.ActorHour = c.ElapsedHours;
        cache.ActorLandingRange = landingRange;
        return hasBattles;
    }

    public static bool CanSeeProvince(Campaign c, int viewerId, int province) => Vision(c, viewerId)?.Sees(province) == true;
    public static bool CanSeeArmy(Campaign c, int viewerId, Army army)
    {
        if (army is null || viewerId < 0) return false;
        if (CanInspect(viewerId, army.NationId)) return true;
        var vision = Vision(c, viewerId);
        return vision is not null && (vision.Sees(army.ProvinceId) || vision.CombatContacts.Contains(army.Id));
    }
    public static bool PlayerCanSeeArmy(Campaign c, Army army) => c?.Player is not null &&
        (!c.Player.IsHuman || CanSeeArmy(c, c.Player.Id, army));
    public static bool PlayerCanSeeProvince(Campaign c, int province) => c?.Player is not null &&
        (!c.Player.IsHuman || CanSeeProvince(c, c.Player.Id, province));
    public static bool PlayerCanSeeBattle(Campaign c, LandBattle battle) => c?.Player is not null && battle is not null &&
        (!c.Player.IsHuman || battle.AttackerId == c.Player.Id || battle.DefenderId == c.Player.Id ||
         CanSeeProvince(c, c.Player.Id, battle.ProvinceId));
}
