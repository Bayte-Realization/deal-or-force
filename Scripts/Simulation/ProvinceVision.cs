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
    internal int TerritoryGeneration = -1;
    internal int TerritoryControllerVersion = -1;
    internal ProvinceTopology TerritoryMap;
    internal ProvinceTopology StampMap;
    public int Revision { get; internal set; }
    public bool Sees(int province) => province > 0 && province < Reach.Length && Reach[province] > 0;
}

public static partial class CampaignIntel
{
    private sealed class Cache
    {
        public readonly object SyncGate = new();
        public int Epoch;
        public int ManualEpoch;
        public readonly Dictionary<int, ProvinceVision> Viewers = new();
        // Provinces grouped by who controls them (ProvincesHeldBy), with what they were built from.
        public int HeldManualEpoch = -1, HeldRevision = -1;
        public ushort[] HeldSource;
        /// <summary>The controllers as they stood when the grouping was built.</summary>
        public ushort[] HeldCopy = Array.Empty<ushort>();
        public int[] HeldStart = Array.Empty<int>(), HeldNext = Array.Empty<int>(), HeldProvinces = Array.Empty<int>();
        public int[] ControllerVersion = Array.Empty<int>();
        public int ControllerGeneration = 1;

        public bool HasActorIndex;
        public bool HasActiveBattles;
        public int ActorStamp;
        public long ActorHour;
        public float ActorLandingRange;
        public ProvinceTopology ActorMap;
        public List<int>[] ArmiesByNationDense = Array.Empty<List<int>>();
        public readonly List<int> ActiveArmyNations = new();
        public List<int>[] BattlesByNationDense = Array.Empty<List<int>>();
        public readonly List<int> ActiveBattleNations = new();
        public int[] ArmyProvinceByIdDense = Array.Empty<int>();
        public int[] ArmyProvinceStampDense = Array.Empty<int>();
        public int ArmyProvinceStamp = 1;
        public readonly Dictionary<int, int> ArmyProvinceOverflow = new();
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
        lock (cache.SyncGate)
        {
            // Only check controller bytes when MapRevision changed, controllers reference changed,
            // or a caller explicitly invoked CampaignIntel.Invalidate(c). Hourly actor-position
            // refreshes (InvalidateActors) skip the 70 KB SequenceEqual completely.
            if (cache.HeldManualEpoch != cache.ManualEpoch || cache.HeldRevision != c.MapRevision || !ReferenceEquals(cache.HeldSource, controllers))
            {
                if (controllers.AsSpan().SequenceEqual(cache.HeldCopy))
                {
                    cache.HeldSource = controllers;
                    cache.HeldRevision = c.MapRevision;
                    cache.HeldManualEpoch = cache.ManualEpoch;
                }
                else
                {
                    // Rebuilt every time a province changes hands, which in a world at war is hourly:
                    // the arrays are kept and refilled rather than allocated again.
                    var top = 0;
                    var prevCopy = cache.HeldCopy;
                    var sameLength = prevCopy.Length == controllers.Length;
                    for (var p = 1; p < controllers.Length; p++)
                    {
                        var cur = controllers[p];
                        if (cur > top) top = cur;
                        if (sameLength)
                        {
                            var old = prevCopy[p];
                            if (old > top) top = old;
                        }
                    }
                    if (cache.HeldStart.Length < top + 2) { cache.HeldStart = new int[top + 2]; cache.HeldNext = new int[top + 2]; }
                    if (cache.HeldProvinces.Length < controllers.Length) cache.HeldProvinces = new int[controllers.Length];
                    if (cache.ControllerVersion.Length < top + 2) Array.Resize(ref cache.ControllerVersion, top + 2);

                    if (!sameLength)
                    {
                        cache.ControllerGeneration++;
                    }
                    else
                    {
                        var versions = cache.ControllerVersion;
                        for (var p = 1; p < controllers.Length; p++)
                        {
                            var cur = controllers[p];
                            var old = prevCopy[p];
                            if (cur != old)
                            {
                                versions[cur]++;
                                versions[old]++;
                            }
                        }
                    }

                    var start = cache.HeldStart;
                    Array.Clear(start);
                    for (var p = 1; p < controllers.Length; p++) start[controllers[p] + 1]++;
                    for (var i = 1; i < start.Length; i++) start[i] += start[i - 1];
                    var next = cache.HeldNext;
                    Array.Copy(start, next, start.Length);
                    var provinces = cache.HeldProvinces;
                    for (var p = 1; p < controllers.Length; p++) provinces[next[controllers[p]]++] = p;
                    cache.HeldSource = controllers;
                    if (!sameLength) cache.HeldCopy = new ushort[controllers.Length];
                    Array.Copy(controllers, cache.HeldCopy, controllers.Length);
                    cache.HeldRevision = c.MapRevision;
                    cache.HeldManualEpoch = cache.ManualEpoch;
                }
            }
            if (mapIndex < 0 || mapIndex + 1 >= cache.HeldStart.Length) return ReadOnlySpan<int>.Empty;
            var from = cache.HeldStart[mapIndex];
            return cache.HeldProvinces.AsSpan(from, cache.HeldStart[mapIndex + 1] - from);
        }
    }
    private static readonly ConditionalWeakTable<Campaign, Cache> Caches = new();

    public static void Invalidate(Campaign c)
    {
        if (c is not null && Caches.TryGetValue(c, out var cache))
        {
            lock (cache.SyncGate)
            {
                cache.Epoch++;
                cache.ManualEpoch++;
            }
        }
    }

    internal static void InvalidateActors(Campaign c)
    {
        if (c is not null && Caches.TryGetValue(c, out var cache))
        {
            lock (cache.SyncGate)
            {
                cache.Epoch++;
            }
        }
    }

    public static ProvinceVision Vision(Campaign c, int viewerId)
    {
        if (c is null) return null;
        var cache = Caches.GetOrCreateValue(c);
        lock (cache.SyncGate)
        {
            if (!cache.Viewers.TryGetValue(viewerId, out var view))
                cache.Viewers[viewerId] = view = new ProvinceVision();
            var map = c.ProvinceMap;
            var stamp = HashCode.Combine(c.ElapsedDays, c.MapRevision, cache.Epoch, c.Armies.Count,
                c.Battles.Count, c.Fleets.Count, map?.ProvinceCount ?? 0);
            if (view.Revision > 0 && view.Stamp == stamp && ReferenceEquals(view.StampMap, map)) return view;
            view.Stamp = stamp;
            view.StampMap = map;
            view.Revision++;
            using var timer = SimProfiler.Measure("intel.visibility");
            var nation = c.NationById(viewerId);
            if (map is null || nation is null) { view.Reach = Array.Empty<byte>(); return view; }
            if (view.Reach.Length != map.ProvinceCount + 1) view.Reach = new byte[map.ProvinceCount + 1];
            view.Frontier.Clear();
            view.CombatContacts.Clear();
            var borderDepth = Math.Clamp(c.Defines.BorderVisionDepth, 0, 6);
            var armyDepth = Math.Clamp(c.Defines.ArmyVisionDepth, 0, 6);
            var controllers = c.ProvinceController ?? map.ProvinceToNation;

            // Refresh the per-controller index once; nations whose own controlled provinces did not
            // change reuse their cached TerritoryReach directly instead of re-flooding 35k provinces.
            var owned = ProvincesHeldBy(c, controllers, nation.MapIndex);
            var controllerVersion = (uint)nation.MapIndex < (uint)cache.ControllerVersion.Length
                ? cache.ControllerVersion[nation.MapIndex] : 0;

            if (view.TerritoryMapIndex == nation.MapIndex &&
                view.TerritoryDepth == borderDepth &&
                view.TerritoryGeneration == cache.ControllerGeneration &&
                view.TerritoryControllerVersion == controllerVersion &&
                ReferenceEquals(view.TerritoryMap, map) &&
                view.TerritoryReach.Length == view.Reach.Length)
            {
                Array.Copy(view.TerritoryReach, view.Reach, view.Reach.Length);
            }
            else
            {
                var reach = view.Reach;
                Array.Clear(reach);
                var ownLevel = (byte)(borderDepth + 1);
                var reachLen = reach.Length;
                for (var i = 0; i < owned.Length; i++)
                {
                    var p = owned[i];
                    if (p > 0 && p < reachLen) reach[p] = ownLevel;
                }
                if (borderDepth > 0)
                {
                    // Only enqueue border provinces (those adjacent to at least one non-owned tile);
                    // interior tiles have all neighbors at ownLevel and would pop as no-ops in FloodVision.
                    var neighbors = map.Neighbors;
                    var frontier = view.Frontier;
                    for (var i = 0; i < owned.Length; i++)
                    {
                        var p = owned[i];
                        if (p <= 0 || p >= neighbors.Length) continue;
                        var adj = neighbors[p];
                        if (adj is null) continue;
                        for (var j = 0; j < adj.Length; j++)
                        {
                            var n = adj[j];
                            if (n > 0 && n < reachLen && reach[n] < ownLevel)
                            {
                                frontier.Enqueue(p);
                                break;
                            }
                        }
                    }
                    FloodVision(reach, frontier, map);
                }
                if (view.TerritoryReach.Length != reach.Length) view.TerritoryReach = new byte[reach.Length];
                Array.Copy(reach, view.TerritoryReach, reach.Length);
                view.TerritoryMapRevision = c.MapRevision;
                view.TerritoryMapIndex = nation.MapIndex;
                view.TerritoryDepth = borderDepth;
                view.TerritoryGeneration = cache.ControllerGeneration;
                view.TerritoryControllerVersion = controllerVersion;
                view.TerritoryMap = map;
            }
            var hasBattles = EnsureActorIndex(c, cache, stamp, map, Math.Clamp(c.Defines.LandingVisionRangeDegrees, .1f, 3f));
            if ((uint)viewerId < (uint)cache.ArmiesByNationDense.Length && cache.ArmiesByNationDense[viewerId] is { Count: > 0 } myArmies)
                for (var i = 0; i < myArmies.Count; i++) RevealProvince(view.Reach, view.Frontier, myArmies[i], armyDepth);
            if (hasBattles && (uint)viewerId < (uint)cache.BattlesByNationDense.Length && cache.BattlesByNationDense[viewerId] is { Count: > 0 } myBattles)
            {
                for (var i = 0; i < myBattles.Count; i++)
                {
                    var bi = myBattles[i];
                    if ((uint)bi >= (uint)c.Battles.Count) continue;
                    var battle = c.Battles[bi];
                    RevealProvince(view.Reach, view.Frontier, battle.ProvinceId, armyDepth);
                    view.CombatContacts.UnionWith(battle.Attackers);
                    view.CombatContacts.UnionWith(battle.Defenders);
                }
            }
            if (view.CombatContacts.Count > 0)
            {
                var denseProv = cache.ArmyProvinceByIdDense;
                var denseStamp = cache.ArmyProvinceStampDense;
                var curStamp = cache.ArmyProvinceStamp;
                foreach (var contactId in view.CombatContacts)
                {
                    if ((uint)contactId < (uint)denseProv.Length)
                    {
                        if (denseStamp[contactId] == curStamp) RevealProvince(view.Reach, view.Frontier, denseProv[contactId], 0);
                    }
                    else if (cache.ArmyProvinceOverflow.TryGetValue(contactId, out var prov))
                        RevealProvince(view.Reach, view.Frontier, prov, 0);
                }
            }
            // An invasion order alone is NOT reconnaissance across the world. The fleet
            // must actually have approached its beach, or combat must already have begun.
            if (cache.InvasionBeachesByNation.TryGetValue(viewerId, out var beaches))
                for (var i = 0; i < beaches.Count; i++) RevealProvince(view.Reach, view.Frontier, beaches[i], armyDepth);
            FloodVision(view.Reach, view.Frontier, map);
            // What agents report and what reconnaissance flights photograph: seen, not seen past.
            IntelNetworks.RevealInto(c, viewerId, view.Reach);
            AirReconnaissance.RevealInto(c, viewerId, view.Reach);
            return view;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RevealProvince(byte[] reach, Queue<int> frontier, int province, int depth)
    {
        if (province <= 0 || province >= reach.Length || reach[province] >= depth + 1) return;
        reach[province] = (byte)(depth + 1);
        frontier.Enqueue(province);
    }

    private static void FloodVision(byte[] reach, Queue<int> frontier, ProvinceTopology map)
    {
        var neighborsByProv = map.Neighbors;
        while (frontier.Count > 0)
        {
            var p = frontier.Dequeue();
            var nextDepth = reach[p] - 2;
            if (nextDepth < 0) continue;
            var adj = neighborsByProv[p];
            for (var i = 0; i < adj.Length; i++)
                RevealProvince(reach, frontier, adj[i], nextDepth);
        }
    }

    private static bool EnsureActorIndex(Campaign c, Cache cache, int stamp, ProvinceTopology map, float landingRange)
    {
        if (cache.HasActorIndex && cache.ActorStamp == stamp && cache.ActorHour == c.ElapsedHours &&
            cache.ActorLandingRange == landingRange && ReferenceEquals(cache.ActorMap, map))
            return cache.HasActiveBattles;

        for (var i = 0; i < cache.ActiveArmyNations.Count; i++)
        {
            var nid = cache.ActiveArmyNations[i];
            if ((uint)nid < (uint)cache.ArmiesByNationDense.Length)
                cache.ArmiesByNationDense[nid]?.Clear();
        }
        cache.ActiveArmyNations.Clear();
        for (var i = 0; i < cache.ActiveBattleNations.Count; i++)
        {
            var nid = cache.ActiveBattleNations[i];
            if ((uint)nid < (uint)cache.BattlesByNationDense.Length)
                cache.BattlesByNationDense[nid]?.Clear();
        }
        cache.ActiveBattleNations.Clear();
        cache.ArmyProvinceOverflow.Clear();
        foreach (var list in cache.InvasionBeachesByNation.Values) list.Clear();

        var nationCap = Math.Max(c.Nations.Count, 64);
        if (cache.ArmiesByNationDense.Length < nationCap)
            Array.Resize(ref cache.ArmiesByNationDense, nationCap + 64);
        if (cache.BattlesByNationDense.Length < nationCap)
            Array.Resize(ref cache.BattlesByNationDense, nationCap + 64);

        var hasBattles = false;
        var battlesByNation = cache.BattlesByNationDense;
        var activeBattleNations = cache.ActiveBattleNations;
        for (var i = 0; i < c.Battles.Count; i++)
        {
            var battle = c.Battles[i];
            if (battle.Ended) continue;
            hasBattles = true;
            var atk = battle.AttackerId;
            if ((uint)atk >= (uint)battlesByNation.Length)
            {
                Array.Resize(ref cache.BattlesByNationDense, atk + 64);
                battlesByNation = cache.BattlesByNationDense;
            }
            var atkList = battlesByNation[atk] ??= new List<int>(4);
            if (atkList.Count == 0) activeBattleNations.Add(atk);
            atkList.Add(i);

            var def = battle.DefenderId;
            if (def != atk)
            {
                if ((uint)def >= (uint)battlesByNation.Length)
                {
                    Array.Resize(ref cache.BattlesByNationDense, def + 64);
                    battlesByNation = cache.BattlesByNationDense;
                }
                var defList = battlesByNation[def] ??= new List<int>(4);
                if (defList.Count == 0) activeBattleNations.Add(def);
                defList.Add(i);
            }
        }

        var armyStamp = 0;
        if (hasBattles)
        {
            if (cache.ArmyProvinceStamp == int.MaxValue)
            {
                Array.Clear(cache.ArmyProvinceStampDense);
                cache.ArmyProvinceStamp = 1;
            }
            else cache.ArmyProvinceStamp++;
            armyStamp = cache.ArmyProvinceStamp;
        }

        var armiesByNation = cache.ArmiesByNationDense;
        var activeArmyNations = cache.ActiveArmyNations;
        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            var nid = army.NationId;
            if ((uint)nid >= (uint)armiesByNation.Length)
            {
                Array.Resize(ref cache.ArmiesByNationDense, nid + 64);
                armiesByNation = cache.ArmiesByNationDense;
            }
            var list = armiesByNation[nid] ??= new List<int>(8);
            if (list.Count == 0) activeArmyNations.Add(nid);
            list.Add(army.ProvinceId);

            if (hasBattles)
            {
                var aid = army.Id;
                if ((uint)aid < 65536u)
                {
                    if (aid >= cache.ArmyProvinceByIdDense.Length)
                    {
                        var nextCap = Math.Max(aid + 256, cache.ArmyProvinceByIdDense.Length * 2);
                        if (nextCap > 65536) nextCap = 65536;
                        Array.Resize(ref cache.ArmyProvinceByIdDense, nextCap);
                        Array.Resize(ref cache.ArmyProvinceStampDense, nextCap);
                    }
                    cache.ArmyProvinceByIdDense[aid] = army.ProvinceId;
                    cache.ArmyProvinceStampDense[aid] = armyStamp;
                }
                else cache.ArmyProvinceOverflow[aid] = army.ProvinceId;
            }
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
        cache.HasActiveBattles = hasBattles;
        cache.ActorStamp = stamp;
        cache.ActorHour = c.ElapsedHours;
        cache.ActorLandingRange = landingRange;
        cache.ActorMap = map;
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
