using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public static partial class FrontPlanner
{
    private sealed class StateProvincesCache
    {
        public ProvinceTopology Map;
        public int OwnershipRevision = int.MinValue;
        public int ProvinceCount = -1;
        public int StateMax = -1;
        public int[] Counts = Array.Empty<int>();
        public int[] Offsets = Array.Empty<int>();
        public int[] Cursor = Array.Empty<int>();
        public int[] Provinces = Array.Empty<int>();

        public void Ensure(Campaign c, ProvinceTopology map)
        {
            if (ReferenceEquals(Map, map) && OwnershipRevision == c.OwnershipRevision && ProvinceCount == map.ProvinceCount)
                return;
            var pCount = map.ProvinceCount;
            var p2s = map.ProvinceToState;
            var limit = Math.Min(pCount, p2s.Length - 1);
            var maxState = 0;
            for (var p = 1; p <= limit; p++)
            {
                var st = p2s[p];
                if (st > maxState) maxState = st;
            }
            if (Counts.Length <= maxState) Counts = new int[maxState + 1];
            else Array.Clear(Counts, 0, maxState + 1);
            if (Offsets.Length <= maxState + 1) Offsets = new int[maxState + 2];
            if (Cursor.Length <= maxState) Cursor = new int[maxState + 1];
            for (var p = 1; p <= limit; p++) Counts[p2s[p]]++;
            var running = 0;
            for (var s = 0; s <= maxState; s++)
            {
                Offsets[s] = running;
                Cursor[s] = running;
                running += Counts[s];
            }
            Offsets[maxState + 1] = running;
            if (Provinces.Length < running) Provinces = new int[running + 128];
            for (var p = 1; p <= limit; p++)
            {
                var st = p2s[p];
                Provinces[Cursor[st]++] = p;
            }
            StateMax = maxState;
            ProvinceCount = pCount;
            OwnershipRevision = c.OwnershipRevision;
            Map = map;
        }
    }

    private static readonly ConditionalWeakTable<Campaign, StateProvincesCache> StateProvincesByCampaign = new();

    [ThreadStatic] private static HashSet<int> _objectiveEnemiesHereScratch;
    [ThreadStatic] private static List<int> _objectiveOwnFrontScratch;

    private static bool HasHostileGround(Campaign c, ProvinceTopology map, ushort[] control, StrategicWar war,
        int nationId, int state)
    {
        if (state < 0) return false;
        var cache = StateProvincesByCampaign.GetOrCreateValue(c);
        lock (cache)
        {
            cache.Ensure(c, map);
            if (state > cache.StateMax) return false;
            var start = cache.Offsets[state];
            var end = cache.Offsets[state + 1];
            var provinces = cache.Provinces;
            for (var i = start; i < end; i++)
            {
                if (WarCoalition.HostileController(c, war, nationId, control[provinces[i]]))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Each participant operates on its own front, not necessarily
    /// the place named by the coalition's original declaration. No new campaign state.</summary>
    internal static int ObjectiveFor(Campaign c, StrategicWar war, Nation nation)
    {
        var map = c?.ProvinceMap;
        var control = c?.ProvinceController;
        if (map is null || control is null || nation is null || war is null)
            return war?.ClaimedStateMapIndex ?? -1;
        var frontier = war.FriendlyFrontSet(nation.Id);
        var declaredIsHostile = HasHostileGround(c, map, control, war, nation.Id, war.ClaimedStateMapIndex);
        if (frontier.Count == 0) return declaredIsHostile ? war.ClaimedStateMapIndex : -1;
        var enemiesHere = _objectiveEnemiesHereScratch ??= new HashSet<int>();
        enemiesHere.Clear();
        var ownFront = _objectiveOwnFrontScratch ??= new List<int>();
        ownFront.Clear();
        foreach (var post in frontier)
        {
            if (post <= 0 || post > map.ProvinceCount ||
                control[post] != nation.MapIndex) continue;
            ownFront.Add(post);
            var neighbors = map.Neighbors[post];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                if (WarCoalition.HostileController(c, war, nation.Id, control[next]))
                {
                    enemiesHere.Add(map.ProvinceToNation[next]);
                    enemiesHere.Add(control[next]);
                }
            }
        }
        var declared = c.StateByMapIndex(war.ClaimedStateMapIndex);
        if (declared is not null && enemiesHere.Contains(declared.NationMapIndex) &&
            declaredIsHostile) return declared.MapIndex;
        if (war.Limited) return declaredIsHostile ? war.ClaimedStateMapIndex : -1;
        var best = -1;
        var distance = float.MaxValue;
        var attacking = war.IsAttacking(nation.Id);
        if (attacking || war.IsDefending(nation.Id))
        {
            foreach (var enemyId in war.Side(!attacking))
            {
                var enemy = c.NationById(enemyId);
                if (enemy is null || !enemiesHere.Contains(enemy.MapIndex)) continue;
                var capital = enemy.CapitalProvinceId;
                if (capital <= 0 || capital > map.ProvinceCount ||
                    !WarCoalition.HostileController(c, war, nation.Id, control[capital])) continue;
                var centre = map.CenterOf(capital);
                var near = SetArmyOrderCommand.FrontDistance(map, ownFront, centre.Latitude, centre.Longitude);
                if (near > distance || near == distance && best >= 0 && map.ProvinceToState[capital] >= best) continue;
                distance = near;
                best = map.ProvinceToState[capital];
            }
        }
        return best;
    }
}
