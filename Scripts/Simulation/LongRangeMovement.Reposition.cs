using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation.Kernels;

namespace DealOrForce.Simulation;

public static partial class LongRangeMovement
{
    private sealed class NeighborComponentsEntry
    {
        public int Revision = int.MinValue;
        public int[] ByProvince = Array.Empty<int>();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProvinceTopology, NeighborComponentsEntry> NeighborComponentsTable = new();

    private static int[] NeighborComponentsOf(ProvinceTopology map)
    {
        var entry = NeighborComponentsTable.GetValue(map, static _ => new NeighborComponentsEntry());
        var rev = map.AdjacencyRevision;
        if (entry.Revision == rev && entry.ByProvince.Length > map.ProvinceCount)
            return entry.ByProvince;

        lock (entry)
        {
            if (entry.Revision == rev && entry.ByProvince.Length > map.ProvinceCount)
                return entry.ByProvince;

            var count = map.ProvinceCount;
            var comp = new int[count + 1];
            var queue = new Queue<int>(256);
            var neighborsAll = map.Neighbors;
            var currentComp = 0;
            for (var start = 1; start <= count; start++)
            {
                if (comp[start] != 0) continue;
                currentComp++;
                comp[start] = currentComp;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    var neighbors = neighborsAll[p];
                    for (var ni = 0; ni < neighbors.Length; ni++)
                    {
                        var next = neighbors[ni];
                        if ((uint)next <= (uint)count && next > 0 && comp[next] == 0)
                        {
                            comp[next] = currentComp;
                            queue.Enqueue(next);
                        }
                    }
                }
            }
            entry.ByProvince = comp;
            entry.Revision = rev;
            return comp;
        }
    }

    public static void Reposition(Campaign c)
    {
        foreach (var _ in RepositionStages(c)) { }
    }

    public static IEnumerable<int> RepositionStages(Campaign c)
    {
        var map = c?.ProvinceMap;
        if (map is null || c.SupportFormations.Count == 0) yield break;
        var wars = Warfare.LiveWars(c);
        if (wars.Count == 0) yield break;
        var d = c.Defines;
        var cycle = Math.Max(1, d.AiLongRangeRepositionDays);
        var pool = c.Caches.SupportStep;
        var nations = pool.RepositionNations;
        nations.Clear();

        var allNations = c.ActiveNations;
        for (var i = 0; i < allNations.Count; i++)
        {
            var n = allNations[i];
            if (n.IsHuman) continue;
            var offset = (n.Id % cycle + cycle) % cycle;
            if ((c.ElapsedDays + offset) % cycle != 0) continue;
            var inWar = false;
            for (var wi = 0; wi < wars.Count; wi++)
            {
                var war = wars[wi];
                if (!war.Ended && war.Involves(n.Id)) { inWar = true; break; }
            }
            if (inWar) nations.Add(n);
        }
        if (nations.Count == 0) yield break;
        nations.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        var ordered = InIdOrder(c);
        var byNation = pool.RepositionByNation;
        var byId = pool.RepositionById;
        var battlesByWar = pool.RepositionBattlesByWar;
        pool.ReturnFormationLists(byNation);
        byId.Clear();
        pool.ReturnIntLists(battlesByWar);
        for (var fi = 0; fi < ordered.Count; fi++)
        {
            var f = ordered[fi];
            byId[f.Id] = f;
            if (!byNation.TryGetValue(f.NationId, out var list)) byNation[f.NationId] = list = pool.RentFormationList();
            list.Add(f);
        }
        var cBattles = c.Battles;
        for (var bi = 0; bi < cBattles.Count; bi++)
        {
            var battle = cBattles[bi];
            if (battle.Ended) continue;
            if (!battlesByWar.TryGetValue(battle.WarId, out var list)) battlesByWar[battle.WarId] = list = pool.RentIntList();
            list.Add(battle.ProvinceId);
        }
        var battles = BattleProvincesPooled(c, pool);
        var controls = c.ProvinceController ?? map.ProvinceToNation;
        var scratch = c.Caches.RepositionScratch;
        Dictionary<ushort, List<int>> landByController = null;
        Dictionary<int, LongRangeStrikes.Sides> strikeSides = null;
        List<(ushort OwnerMap, int Province, float Reach, float Lat)> allStrikers = null;
        Dictionary<int, List<ScenarioState>> statesByOwnerMap = null;
        var grounds = pool.WithdrawGrounds;
        try
        {
            for (var ni = 0; ni < nations.Count; ni++)
            {
                var nation = nations[ni];
                if (!byNation.TryGetValue(nation.Id, out var own)) continue;
                var g = GroundOf(c, nation.Id, grounds, battles, pool);
                if (g is null) continue;
                landByController ??= BuildLandByControllerPooled(map, controls, pool);
                foreach (var _ in RepositionNationStages(c, g, own, wars, battlesByWar, landByController, grounds, battles, byId, scratch, pool,
                             () => strikeSides ??= ResetStrikeSides(scratch.StrikeSides),
                             () => allStrikers ??= FillAllStrikers(c, map, scratch.AllStrikers),
                             () => statesByOwnerMap ??= FillStatesByOwnerMap(c, scratch.StatesByOwnerMap, scratch.StatesListPool)))
                    yield return 0;
                if (ni + 1 < nations.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
        finally
        {
            pool.ReturnGrounds(grounds);
            pool.ReturnFormationLists(byNation);
            byId.Clear();
            pool.ReturnIntLists(battlesByWar);
            if (landByController is not null) pool.ReturnIntLists(landByController);
            nations.Clear();
            if (strikeSides is not null) strikeSides.Clear();
            if (allStrikers is not null) allStrikers.Clear();
            if (statesByOwnerMap is not null)
            {
                foreach (var list in statesByOwnerMap.Values)
                {
                    list.Clear();
                    if (scratch.StatesListPool.Count < 256) scratch.StatesListPool.Push(list);
                }
                statesByOwnerMap.Clear();
            }
        }
    }

    private static Dictionary<int, LongRangeStrikes.Sides> ResetStrikeSides(Dictionary<int, LongRangeStrikes.Sides> dict)
    {
        dict.Clear();
        return dict;
    }

    private static List<(ushort OwnerMap, int Province, float Reach, float Lat)> FillAllStrikers(
        Campaign c, ProvinceTopology map, List<(ushort OwnerMap, int Province, float Reach, float Lat)> list)
    {
        list.Clear();
        var forms = c.SupportFormations;
        for (var i = 0; i < forms.Count; i++)
        {
            var f = forms[i];
            var at = f.BaseProvinceId > 0 ? f.BaseProvinceId : At(f);
            if (at <= 0 || at > map.ProvinceCount) continue;
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || type.Strike <= 0f || !SupportCore.Enabled(c, type)) continue;
            var owner = c.NationById(f.NationId);
            if (owner is null || owner.MapIndex <= 0) continue;
            list.Add(((ushort)owner.MapIndex, at, SupportCore.ReachKm(c, type), map.CenterOf(at).Latitude));
        }
        return list;
    }

    private static Dictionary<int, List<ScenarioState>> FillStatesByOwnerMap(
        Campaign c, Dictionary<int, List<ScenarioState>> map, Stack<List<ScenarioState>> listPool)
    {
        map.Clear();
        var states = c.States;
        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (s.NationMapIndex <= 0) continue;
            if (!map.TryGetValue(s.NationMapIndex, out var list))
            {
                list = listPool.Count > 0 ? listPool.Pop() : new List<ScenarioState>(4);
                list.Clear();
                map[s.NationMapIndex] = list;
            }
            list.Add(s);
        }
        return map;
    }

    private static Dictionary<ushort, List<int>> BuildLandByControllerPooled(ProvinceTopology map, ushort[] controls, SupportStepCache pool)
    {
        var byController = pool.RepositionLandByController;
        pool.ReturnIntLists(byController);
        var limit = Math.Min(map.ProvinceCount, controls.Length - 1);
        for (var p = 1; p <= limit; p++)
        {
            if (map.IsWater(p)) continue;
            var code = controls[p];
            if (code == 0) continue;
            if (!byController.TryGetValue(code, out var list)) byController[code] = list = pool.RentIntList();
            list.Add(p);
        }
        return byController;
    }

    private static Dictionary<ushort, List<int>> BuildLandByController(ProvinceTopology map, ushort[] controls)
    {
        var byController = new Dictionary<ushort, List<int>>();
        var limit = Math.Min(map.ProvinceCount, controls.Length - 1);
        for (var p = 1; p <= limit; p++)
        {
            if (map.IsWater(p)) continue;
            var code = controls[p];
            if (code == 0) continue;
            if (!byController.TryGetValue(code, out var list)) byController[code] = list = new List<int>();
            list.Add(p);
        }
        return byController;
    }

    private static IEnumerable<int> RepositionNationStages(Campaign c, Ground g, List<SupportFormation> own,
        IReadOnlyList<StrategicWar> wars, Dictionary<int, List<int>> battlesByWar,
        Dictionary<ushort, List<int>> landByController, Dictionary<int, Ground> grounds, BattleSet battles,
        Dictionary<int, SupportFormation> byId, RepositionScratch scratch, SupportStepCache pool,
        Func<Dictionary<int, LongRangeStrikes.Sides>> getStrikeSides,
        Func<List<(ushort OwnerMap, int Province, float Reach, float Lat)>> getAllStrikers,
        Func<Dictionary<int, List<ScenarioState>>> getStatesByOwnerMap)
    {
        var map = g.Map;
        var d = c.Defines;
        var wordsNeeded = (map.ProvinceCount + 65) >> 6;
        if (scratch.TargetBits.Length < wordsNeeded) scratch.TargetBits = new ulong[wordsNeeded + 8];
        else Array.Clear(scratch.TargetBits, 0, wordsNeeded);
        var targetBits = scratch.TargetBits;
        var targets = scratch.Targets;
        targets.Clear();

        foreach (var war in wars)
        {
            if (war.Ended || !war.Involves(g.Nation.Id) || !battlesByWar.TryGetValue(war.Id, out var list)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var province = list[i];
                if (!g.Valid(province)) continue;
                var w = province >> 6;
                var mask = 1UL << (province & 63);
                if ((targetBits[w] & mask) == 0) { targetBits[w] |= mask; targets.Add(province); }
            }
        }
        if (targets.Count == 0)
            foreach (var code in g.Passable)
                if (landByController.TryGetValue(code, out var owned))
                    for (var i = 0; i < owned.Count; i++)
                    {
                        var p = owned[i];
                        var w = p >> 6;
                        var mask = 1UL << (p & 63);
                        if ((targetBits[w] & mask) == 0 && g.BordersEnemy(p)) { targetBits[w] |= mask; targets.Add(p); }
                    }
        if (targets.Count == 0) yield break;
        targets.Sort();

        var idle = scratch.Idle;
        var sams = scratch.Sams;
        idle.Clear();
        sams.Clear();
        scratch.PlanMemo.Clear();
        LongRangeStrikes.Sides mySides = null;
        for (var oi = 0; oi < own.Count; oi++)
        {
            var f = own[oi];
            if (!SupportTypeCatalog.TryGet(f.TypeId, out var type) || !SupportCore.Enabled(c, type)) continue;
            // Only the standing: nothing on the road, deploying, or cut off is re-planned.
            if (f.Route.Count > 0 || f.DeployDaysLeft > 0 || f.CutOff || f.BaseProvinceId <= 0 || SupportCore.Displaced(c, f)) continue;
            if (type.Kind == SupportKind.AirDefence) { sams.Add((f, type)); continue; }
            // A missile brigade or a long-range drone group on strike work answers to its targets,
            // not to the battles: depots and power stations a thousand kilometres behind the line
            // are what it is for, and while any of them is in reach it stays where it is.
            if (f.Mission == SupportCore.MissionStrike && type.Strike > 0f)
            {
                if (mySides is null)
                {
                    var strikeSides = getStrikeSides();
                    if (!strikeSides.TryGetValue(g.Nation.Id, out mySides))
                        strikeSides[g.Nation.Id] = mySides = LongRangeStrikes.SidesOf(c, g.Nation.Id);
                }
                if (!LongRangeStrikes.HasTargetInReach(c, f, type, mySides, scratch.StrikePlan)) idle.Add((f, type));
                if ((oi & 7) == 7 && oi + 1 < own.Count && Campaign.StageOverdue()) yield return 0;
                continue;
            }
            var reach = SupportCore.ReachKm(c, type);
            var latBand = reach / 111.0f;
            var originLat = map.CenterOf(f.BaseProvinceId).Latitude;
            var covers = false;
            for (var ti = 0; ti < targets.Count; ti++)
            {
                var target = targets[ti];
                if (MathF.Abs(map.CenterOf(target).Latitude - originLat) <= latBand &&
                    SupportCore.DistanceKm(c, f.BaseProvinceId, target) <= reach)
                { covers = true; break; }
            }
            if (!covers) idle.Add((f, type));
        }
        // SAM brigades guard the cities first; the ones no city needs - nothing of the enemy's
        // can reach any, or every threatened one is covered - go where the fighting is, to keep
        // the enemy's aircraft off it, like any other formation that reaches no battle.
        if (sams.Count > 0)
        {
            foreach (var (f, type) in CoverWithSams(c, g, own, sams, landByController, grounds, battles, byId, scratch, pool,
                         getAllStrikers(), getStatesByOwnerMap()))
            {
                var reach = SupportCore.ReachKm(c, type);
                var latBand = reach / 111.0f;
                var originLat = map.CenterOf(f.BaseProvinceId).Latitude;
                var covers = false;
                for (var ti = 0; ti < targets.Count; ti++)
                {
                    var target = targets[ti];
                    if (MathF.Abs(map.CenterOf(target).Latitude - originLat) <= latBand &&
                        SupportCore.DistanceKm(c, f.BaseProvinceId, target) <= reach)
                    { covers = true; break; }
                }
                if (!covers) idle.Add((f, type));
            }
            if (idle.Count > 0 && Campaign.StageOverdue()) yield return 0;
        }

        if (idle.Count > 0)
        {
            var bound = 0f;
            for (var i = 0; i < idle.Count; i++) bound = MathF.Max(bound, Standoff(c, idle[i].Type));
            foreach (var _ in DistanceFieldStages(c, map, targets, bound, scratch)) yield return 0;
            var near = scratch.Near;
            var within = scratch.Within;
            var compByProv = NeighborComponentsOf(map);
            scratch.RoadMemo.Clear();
            scratch.SeaMemo.Clear();
            scratch.AirMemo.Clear();
            scratch.CompSuitMemo.Clear();
            for (var i = 0; i < idle.Count; i++)
            {
                var (f, type) = idle[i];
                var standoff = Standoff(c, type);
                var mayTouch = type.RangeKm < d.AiLongRangeFrontlineMaxRangeKm;
                var origin = f.BaseProvinceId;
                var anySuit = false;
                for (var wi = 0; wi < within.Count; wi++)
                {
                    var p = within[wi];
                    if (p != origin && Suits(g, p, near, standoff, mayTouch)) { anySuit = true; break; }
                }
                if (!anySuit) continue;
                int ordered;
                if (type.Movement == SupportMoveMode.Air)
                {
                    ordered = FlyToward(c, g, f, type, origin, within, near, standoff, mayTouch, landByController, grounds, battles, byId, scratch);
                }
                else
                {
                    foreach (var _ in DriveTowardStages(c, g, f, type, origin, within, compByProv, near, standoff, mayTouch, grounds, battles, byId, scratch))
                        yield return 0;
                    ordered = scratch.LastDriveOrdered;
                }
                var ordDist = (uint)ordered < (uint)near.Length ? near[ordered] : float.PositiveInfinity;
                var orgDist = (uint)origin < (uint)near.Length ? near[origin] : float.PositiveInfinity;
                if (ordered > 0 && ordDist < orgDist)
                    AiOrdersTowardFront++;
                if (i + 1 < idle.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
    }

    /// <summary>
    /// A formation that drives: the suiting province the road reaches soonest; with none by road -
    /// a war across the water - the nearest suiting ones in a straight line, the first the order
    /// accepts, by ship if it must. Sets <see cref="RepositionScratch.LastDriveOrdered"/>.
    /// </summary>
    private static IEnumerable<int> DriveTowardStages(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type, int origin,
        List<int> within, int[] compByProv, float[] near, float standoff, bool mayTouch,
        Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId, RepositionScratch scratch)
    {
        scratch.LastDriveOrdered = -1;
        var memoKey = (origin, standoff, mayTouch, type.Speed);
        if (!scratch.RoadMemo.TryGetValue(memoKey, out var chosen))
        {
            var canReachByNeighbor = true;
            var originComp = (uint)origin < (uint)compByProv.Length ? compByProv[origin] : 0;
            if (originComp != 0)
            {
                var compKey = (originComp, standoff, mayTouch);
                if (!scratch.CompSuitMemo.TryGetValue(compKey, out canReachByNeighbor))
                {
                    canReachByNeighbor = false;
                    for (var wi = 0; wi < within.Count; wi++)
                    {
                        var p = within[wi];
                        if ((uint)p < (uint)compByProv.Length && compByProv[p] == originComp &&
                            p != origin && Suits(g, p, near, standoff, mayTouch))
                        { canReachByNeighbor = true; break; }
                    }
                    scratch.CompSuitMemo[compKey] = canReachByNeighbor;
                }
            }
            if (!canReachByNeighbor)
            {
                chosen = -1;
            }
            else
            {
                foreach (var _ in NearestByRoadStages(c, g, type, origin, near, standoff, mayTouch, scratch))
                    yield return 0;
                chosen = scratch.LastRoadChosen;
            }
            scratch.RoadMemo[memoKey] = chosen;
        }
        if (chosen > 0 && chosen != origin)
        {
            scratch.LastDriveOrdered = OrderFormationFast(c, g, f, type, chosen, grounds, battles, byId, scratch) ? chosen : -1;
            yield break;
        }

        var seaKey = (origin, standoff, mayTouch);
        if (!scratch.SeaMemo.TryGetValue(seaKey, out var seaChosen))
        {
            seaChosen = ChooseSeaTarget(c, g, f, origin, within, near, standoff, mayTouch, grounds, battles, byId, scratch);
            scratch.SeaMemo[seaKey] = seaChosen;
        }
        if (seaChosen > 0)
            scratch.LastDriveOrdered = OrderFormationFast(c, g, f, type, seaChosen, grounds, battles, byId, scratch) ? seaChosen : -1;
    }

    private static int ChooseSeaTarget(Campaign c, Ground g, SupportFormation f, int origin,
        List<int> within, float[] near, float standoff, bool mayTouch,
        Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId, RepositionScratch scratch)
    {
        var seaChosen = -1;
        Span<int> topP = stackalloc int[4];
        Span<float> topKm = stackalloc float[4];
        var found = 0;
        for (var wi = 0; wi < within.Count; wi++)
        {
            var p = within[wi];
            if (p == origin || !Suits(g, p, near, standoff, mayTouch)) continue;
            var km = SupportCore.DistanceKm(c, origin, p);
            if (found == 4 && (km > topKm[3] || (km == topKm[3] && p >= topP[3]))) continue;
            var insert = found < 4 ? found++ : 3;
            while (insert > 0 && (km < topKm[insert - 1] || (km == topKm[insert - 1] && p < topP[insert - 1])))
            {
                topKm[insert] = topKm[insert - 1];
                topP[insert] = topP[insert - 1];
                insert--;
            }
            topKm[insert] = km;
            topP[insert] = p;
        }
        for (var ti = 0; ti < found; ti++)
        {
            var province = topP[ti];
            if (!PreviewOneInto(c, scratch.PreviewScratch, g.Nation.Id, f.Id, province, false, grounds, battles, byId, includeBasing: false, planMemo: scratch.PlanMemo).Accepted)
                continue;
            seaChosen = province;
            break;
        }
        return seaChosen;
    }

    /// <summary>
    /// A formation that flies: the suiting province nearest in one flight; with none that close,
    /// the nearest at all, reached in hops - each to a province of its side within one ferry
    /// flight and nearer the destination - given as one order with its waypoints, the way a
    /// player gives it with Shift+right-click. Returns where it was sent, or -1.
    /// </summary>
    private static int FlyToward(Campaign c, Ground g, SupportFormation f, SupportTypeDefinition type, int origin,
        List<int> within, float[] near, float standoff, bool mayTouch, Dictionary<ushort, List<int>> landByController,
        Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId, RepositionScratch scratch)
    {
        var ferry = FerryKm(c, type);
        var airKey = (origin, standoff, mayTouch, ferry);
        if (!scratch.AirMemo.TryGetValue(airKey, out var hopsArr))
        {
            var chosen = NearestByAir(c, g, type, origin, within, near, standoff, mayTouch);
            if (chosen > 0 && chosen != origin)
            {
                hopsArr = new[] { chosen };
            }
            else
            {
                var goal = -1; var goalKm = float.MaxValue;
                for (var wi = 0; wi < within.Count; wi++)
                {
                    var province = within[wi];
                    if (province == origin || !Suits(g, province, near, standoff, mayTouch)) continue;
                    var km = SupportCore.DistanceKm(c, origin, province);
                    if (km < goalKm) { goal = province; goalKm = km; }
                }
                if (goal <= 0)
                {
                    hopsArr = Array.Empty<int>();
                }
                else
                {
                    var map = g.Map;
                    var goalLat = map.CenterOf(goal).Latitude;
                    var hops = new List<int>();
                    var at = origin;
                    var reachable = true;
                    // Six legs is a wing across a continent; a war further than that is not this wing's.
                    while (SupportCore.DistanceKm(c, at, goal) > ferry && hops.Count < 6)
                    {
                        var atLat = map.CenterOf(at).Latitude;
                        var next = -1; var nextKm = SupportCore.DistanceKm(c, at, goal) - 100f;
                        foreach (var code in g.Passable)
                            if (landByController.TryGetValue(code, out var bucket))
                                for (var i = 0; i < bucket.Count; i++)
                                {
                                    var p = bucket[i];
                                    if (g.Destroyed(p) || g.Battles.Contains(p)) continue;
                                    var pLat = map.CenterOf(p).Latitude;
                                    if (MathF.Abs(pLat - atLat) * 111.0f > ferry || MathF.Abs(pLat - goalLat) * 111.0f > nextKm) continue;
                                    if (SupportCore.DistanceKm(c, at, p) > ferry) continue;
                                    var left = SupportCore.DistanceKm(c, p, goal);
                                    if (left < nextKm || (next > 0 && left == nextKm && p < next)) { next = p; nextKm = left; }
                                }
                        if (next <= 0) { reachable = false; break; }
                        hops.Add(next);
                        at = next;
                    }
                    if (!reachable || SupportCore.DistanceKm(c, at, goal) > ferry)
                    {
                        hopsArr = Array.Empty<int>();
                    }
                    else
                    {
                        hops.Add(goal);
                        hopsArr = hops.ToArray();
                    }
                }
            }
            scratch.AirMemo[airKey] = hopsArr;
        }
        if (hopsArr.Length == 0) return -1;
        if (!OrderFast(c, g.Nation.Id, f.Id, hopsArr[0], grounds, battles, byId, null)) return -1;
        for (var leg = 1; leg < hopsArr.Length; leg++)
            new RelocateSupportCommand { IssuerId = g.Nation.Id, FormationIds = new[] { f.Id }, ProvinceId = hopsArr[leg], Append = true }
                .ApplyFast(c, grounds, battles, byId, null);
        return hopsArr[^1];
    }

    /// <summary>
    /// How far from the fighting the computer puts a formation: a share of its reach, so the front
    /// can move and it still reaches - except for the guns that belong on the front itself, which
    /// take their whole reach. A province is about 65 km across, and three fifths of a 45 km gun
    /// line's reach is less than one: held to the share, it could never stand next to a battle.
    /// </summary>
    private static float Standoff(Campaign c, SupportTypeDefinition type)
    {
        var d = c.Defines;
        var reach = SupportCore.ReachKm(c, type);
        return type.RangeKm < d.AiLongRangeFrontlineMaxRangeKm ? reach : d.AiLongRangeStandoffShare * reach;
    }

    private static readonly RelocateSupportCommand FeatureProbe = new();

    private static bool OrderFormationFast(Campaign c, Ground g, SupportFormation formation, SupportTypeDefinition type,
        int province, Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId,
        RepositionScratch scratch)
    {
        if (!MechanicFeatures.Allows(c, FeatureProbe)) return false;
        var plan = PreviewOneInto(c, scratch.PreviewScratch, g.Nation.Id, formation.Id, province, false,
            grounds, battles, byId, includeBasing: false, planMemo: scratch.PlanMemo);
        if (!plan.Accepted) return false;
        if (!Commit(c, formation, type, plan)) return false;
        c.MarkStrategicChange();
        AiOrders++;
        return true;
    }

    private static bool OrderFast(Campaign c, int nationId, int formationId, int province,
        Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId,
        Dictionary<(int, int), CachedGroundPlan> planMemo)
    {
        var order = new RelocateSupportCommand { IssuerId = nationId, FormationIds = new[] { formationId }, ProvinceId = province };
        order.ApplyFast(c, grounds, battles, byId, planMemo);
        if (order.Moved == 0) return false;
        AiOrders++;
        return true;
    }

    /// <summary>
    /// Kilometres from the nearest battle or front province, along the province graph, out to a
    /// bound - never shorter than the straight line, so a province inside the bound by this
    /// measure is inside it in fact. Ties never matter: a shortest distance is the same however
    /// equal-cost provinces are popped.
    /// </summary>
    internal static Dictionary<int, float> DistanceField(Campaign c, ProvinceTopology map, List<int> sources, float bound, out List<int> within)
    {
        if (NativeKernelsLoader.EnableDistanceFieldDefault &&
            NativeProvinceGraph.ForMap(map) is { } nativeGraph &&
            nativeGraph.TryDistanceField(sources, bound, out var nativeDistances, out var nativeWithin) &&
            nativeDistances is not null && nativeWithin is not null)
        {
            within = nativeWithin;
            return nativeDistances;
        }
        return DistanceFieldManaged(c, map, sources, bound, out within);
    }

    internal static Dictionary<int, float> DistanceFieldManaged(Campaign c, ProvinceTopology map, List<int> sources, float bound, out List<int> within)
    {
        var distance = new Dictionary<int, float>();
        within = new List<int>();
        var queue = new PriorityQueue<int, (float, int)>();
        foreach (var source in sources) { distance[source] = 0f; queue.Enqueue(source, (0f, source)); }
        while (queue.TryDequeue(out var province, out var key))
        {
            if (key.Item1 > distance[province]) continue;
            within.Add(province);
            if (province < 0 || province >= map.Neighbors.Length) continue;
            var neighbours = map.Neighbors[province];
            if (neighbours is null) continue;
            foreach (var neighbour in neighbours)
            {
                var step = SupportCore.DistanceKm(c, province, neighbour);
                var toNeighbour = key.Item1 + step;
                if (toNeighbour > bound) continue;
                if (distance.TryGetValue(neighbour, out var known) && known <= toNeighbour) continue;
                distance[neighbour] = toNeighbour;
                queue.Enqueue(neighbour, (toNeighbour, neighbour));
            }
        }
        return distance;
    }

    private static IEnumerable<int> DistanceFieldStages(Campaign c, ProvinceTopology map, List<int> sources, float bound,
        RepositionScratch scratch)
    {
        var needed = map.ProvinceCount + 2;
        var within = scratch.Within;
        var initialized = scratch.Near.Length >= needed;
        if (!initialized)
        {
            scratch.Near = new float[needed + 64];
            Array.Fill(scratch.Near, float.PositiveInfinity);
            within.Clear();
        }
        if (NativeKernelsLoader.EnableDistanceFieldDefault &&
            NativeProvinceGraph.ForMap(map) is { } nativeGraph &&
            nativeGraph.TryFillDistanceField(sources, bound, scratch.Near, within))
            yield break;

        if (initialized)
        {
            for (var i = 0; i < within.Count; i++) scratch.Near[within[i]] = float.PositiveInfinity;
            within.Clear();
        }
        var distance = scratch.Near;
        var queue = scratch.Queue;
        queue.Clear();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if ((uint)source >= (uint)distance.Length) continue;
            distance[source] = 0f;
            queue.Enqueue(source, (0f, source));
        }
        var edgeKm = SupportCore.NeighborEdgeKm(map);
        var neighborsAll = map.Neighbors;
        var popped = 0;
        while (queue.TryDequeue(out var province, out var key))
        {
            if (key.Item1 > distance[province]) continue;
            within.Add(province);
            var neighbors = neighborsAll[province];
            var rowKm = edgeKm[province];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                var km = key.Item1 + rowKm[ni];
                if (km > bound || distance[next] <= km) continue;
                distance[next] = km;
                queue.Enqueue(next, (km, next));
            }
            if ((++popped & 255) == 255 && Campaign.StageOverdue()) yield return 0;
        }
        within.Sort();
    }

    private static bool Suits(Ground g, int province, float[] near, float standoff, bool mayTouch) =>
        g.Enterable(province) && !g.Battles.Contains(province) &&
        (uint)province < (uint)near.Length && near[province] <= standoff &&
        (mayTouch || !g.BordersEnemy(province));

    /// <summary>The province suiting the formation that the road reaches soonest, ties to the lower id.</summary>
    private static IEnumerable<int> NearestByRoadStages(Campaign c, Ground g, SupportTypeDefinition type, int origin,
        float[] near, float standoff, bool mayTouch, RepositionScratch scratch)
    {
        scratch.LastRoadChosen = -1;
        var needed = g.Map.ProvinceCount + 2;
        var touched = scratch.RoadTouched;
        if (scratch.RoadDays.Length < needed)
        {
            scratch.RoadDays = new float[needed + 64];
            Array.Fill(scratch.RoadDays, float.PositiveInfinity);
            touched.Clear();
        }
        else
        {
            for (var i = 0; i < touched.Count; i++) scratch.RoadDays[touched[i]] = float.PositiveInfinity;
            touched.Clear();
        }
        var days = scratch.RoadDays;
        if ((uint)origin >= (uint)days.Length) yield break;
        days[origin] = 0f;
        touched.Add(origin);
        var queue = scratch.Queue;
        queue.Clear();
        queue.Enqueue(origin, (0f, origin));
        var popped = 0;
        var neighborsAll = g.Map.Neighbors;
        while (queue.TryDequeue(out var province, out var key))
        {
            if (key.Item1 > days[province]) continue;
            if (province != origin && Suits(g, province, near, standoff, mayTouch))
            {
                scratch.LastRoadChosen = province;
                yield break;
            }
            // A month of driving is not a repositioning, and a continent is not a search space.
            if (++popped > 6000 || key.Item1 > 45f) break;
            var neighbors = neighborsAll[province];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                if (!g.Enterable(next)) continue;
                var pace = PaceOf(g, type, province, next, SupportMoveMode.Land, out _);
                var arrive = key.Item1 + MathF.Max(1f, 1f / pace);
                var known = days[next];
                if (known <= arrive) continue;
                if (float.IsPositiveInfinity(known)) touched.Add(next);
                days[next] = arrive;
                queue.Enqueue(next, (arrive, next));
            }
            if ((popped & 255) == 255 && Campaign.StageOverdue()) yield return 0;
        }
    }

    /// <summary>The suiting province nearest in a straight line within one ferry flight, ties to the lower id.</summary>
    private static int NearestByAir(Campaign c, Ground g, SupportTypeDefinition type, int origin, List<int> within,
        float[] near, float standoff, bool mayTouch)
    {
        var map = g.Map;
        var ferry = FerryKm(c, type);
        var originLat = map.CenterOf(origin).Latitude;
        var latBand = ferry / 111.0f;
        var best = -1;
        var bestKm = float.MaxValue;
        for (var i = 0; i < within.Count; i++)
        {
            var province = within[i];
            if (province == origin || !Suits(g, province, near, standoff, mayTouch)) continue;
            if (MathF.Abs(map.CenterOf(province).Latitude - originLat) > latBand) continue;
            var km = SupportCore.DistanceKm(c, origin, province);
            if (km > ferry || km >= bestKm) continue;
            best = province; bestKm = km;
        }
        return best;
    }

    /// <summary>
    /// SAM brigades go where the enemy's strike arm can hit and nobody covers yet - the most
    /// populous such region first - unless the brigade already stands under that threat.
    /// </summary>
    private static List<(SupportFormation Formation, SupportTypeDefinition Type)> CoverWithSams(Campaign c, Ground g,
        List<SupportFormation> own,
        List<(SupportFormation Formation, SupportTypeDefinition Type)> sams, Dictionary<ushort, List<int>> landByController,
        Dictionary<int, Ground> grounds, BattleSet battles, Dictionary<int, SupportFormation> byId, RepositionScratch scratch,
        SupportStepCache pool,
        List<(ushort OwnerMap, int Province, float Reach, float Lat)> allStrikers,
        Dictionary<int, List<ScenarioState>> statesByOwnerMap)
    {
        // The brigades no city needs, for the review to send toward the fighting instead.
        var spare = scratch.CoverSpare;
        spare.Clear();
        var map = g.Map;
        var strikers = scratch.CoverStrikers;
        strikers.Clear();
        for (var si = 0; si < allStrikers.Count; si++)
        {
            var item = allStrikers[si];
            if (g.IsHostile(item.OwnerMap)) strikers.Add((item.Province, item.Reach, item.Lat));
        }
        if (strikers.Count == 0) return sams;
        bool Threatened(int province, float plat)
        {
            for (var si = 0; si < strikers.Count; si++)
            {
                var (at, reach, slat) = strikers[si];
                if (MathF.Abs(plat - slat) * 111.0f <= reach && SupportCore.DistanceKm(c, at, province) <= reach)
                    return true;
            }
            return false;
        }

        // The regions in danger, most people first; each named by the province its bases take.
        var held = scratch.CoverHeld;
        pool.ReturnIntLists(held);
        var regions = scratch.CoverRegions;
        regions.Clear();
        var guarded = scratch.CoverGuarded;
        guarded.Clear();
        var anchors = scratch.CoverAnchors;
        anchors.Clear();
        try
        {
            if (g.Nation.MapIndex > 0 && landByController.TryGetValue((ushort)g.Nation.MapIndex, out var ownLand))
                for (var i = 0; i < ownLand.Count; i++)
                {
                    var p = ownLand[i];
                    var region = map.ProvinceToState[p];
                    if (!held.TryGetValue(region, out var list)) held[region] = list = pool.RentIntList();
                    list.Add(p);
                }
            if (g.Nation.MapIndex > 0 && statesByOwnerMap.TryGetValue(g.Nation.MapIndex, out var ownedStates))
                for (var i = 0; i < ownedStates.Count; i++)
                {
                    var s = ownedStates[i];
                    if (held.ContainsKey(s.MapIndex)) regions.Add(s);
                }
            regions.Sort(static (a, b) =>
            {
                var cmp = b.Population.CompareTo(a.Population);
                return cmp != 0 ? cmp : a.MapIndex.CompareTo(b.MapIndex);
            });
            if (regions.Count > 40) regions.RemoveRange(40, regions.Count - 40);
            for (var si = 0; si < sams.Count; si++)
            {
                var (f, type) = sams[si];
                var dest = Destination(f);
                var lat = dest > 0 && dest <= map.ProvinceCount ? map.CenterOf(dest).Latitude : 0f;
                guarded.Add((dest, SupportCore.ReachKm(c, type), lat));
            }
            for (var oi = 0; oi < own.Count; oi++)
            {
                var other = own[oi];
                if (other.Route.Count == 0) continue;
                if (SupportTypeCatalog.TryGet(other.TypeId, out var otherType) && otherType.Kind == SupportKind.AirDefence)
                {
                    var dest = Destination(other);
                    var lat = dest > 0 && dest <= map.ProvinceCount ? map.CenterOf(dest).Latitude : 0f;
                    guarded.Add((dest, SupportCore.ReachKm(c, otherType), lat));
                }
            }
            for (var si = 0; si < sams.Count; si++)
            {
                var (f, type) = sams[si];
                if (Threatened(f.BaseProvinceId, map.CenterOf(f.BaseProvinceId).Latitude)) continue;
                var sent = false;
                for (var ri = 0; ri < regions.Count; ri++)
                {
                    var region = regions[ri];
                    if (!anchors.TryGetValue(region.MapIndex, out var anchor))
                        anchors[region.MapIndex] = anchor = SupportCore.ChooseBase(map, held[region.MapIndex]);
                    if (anchor <= 0) continue;
                    var anchorLat = map.CenterOf(anchor).Latitude;
                    if (!Threatened(anchor, anchorLat) || !g.Enterable(anchor) || g.Battles.Contains(anchor)) continue;
                    var covered = false;
                    for (var gi = 0; gi < guarded.Count; gi++)
                    {
                        var (at, reach, glat) = guarded[gi];
                        if (at > 0 && MathF.Abs(anchorLat - glat) * 111.0f <= reach &&
                            SupportCore.DistanceKm(c, at, anchor) <= reach)
                        { covered = true; break; }
                    }
                    if (covered) continue;
                    if (OrderFast(c, g.Nation.Id, f.Id, anchor, grounds, battles, byId, scratch.PlanMemo))
                    {
                        guarded.Add((anchor, SupportCore.ReachKm(c, type), anchorLat));
                        AiCityCover++;
                        sent = true;
                    }
                    break;
                }
                if (!sent) spare.Add((f, type));
            }
            return spare;
        }
        finally
        {
            strikers.Clear();
            pool.ReturnIntLists(held);
            regions.Clear();
            guarded.Clear();
            anchors.Clear();
        }
    }

    // ------------------------------------------------------------------ the fingerprint

    /// <summary>The rules a peer must agree on, in a fixed order; compared against the shipped defaults.</summary>
}
