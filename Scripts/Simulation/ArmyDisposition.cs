using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// A formation whose supply hub is carrying more than it can (congestion under this), or
    /// whose supply has run down below the second figure, is moved off it to ground fed by
    /// another hub - up to this many provinces away, this many moves a week a country.
    /// </summary>
    public float DispositionCongestionLimit { get; set; } = 0.75f;
    public float DispositionSupplyLimit { get; set; } = 0.5f;
    public int DispositionSearchSteps { get; set; } = 6;
    public int DispositionMovesPerWeek { get; set; } = 6;
    /// <summary>At peace, how much of the free army garrisons the capital and the largest regions.</summary>
    public float DispositionGarrisonShare { get; set; } = 0.5f;
    public bool ArmyDispositionEnabled { get; set; } = true;
    /// <summary>How threatening a neighbour must be (ThreatPosture.ThreatOf) before a weaker country pays it for a pact.</summary>
    public float AiBuyPeaceThreat { get; set; } = 0.8f;
}

/// <summary>
/// Where a country keeps the army it is not fighting with.
///
/// Two failures this answers. A computer at peace left its divisions wherever the scenario
/// put them - often all of them in one province - and did nothing with them for a century.
/// And a computer at war let a stack sit on a hub that could feed a third of it until the
/// attrition ate it, because nothing that moved armies looked at their supply.
///
/// Weekly, for each country nobody plays (or whose player handed the army over): first any
/// free formation that is starving moves to the nearest ground another hub can still feed;
/// then, at peace, the free army is laid out - the capital, then the largest regions, one
/// formation each, taken from the largest heap. The borders it fears are ThreatPosture's;
/// the fronts are the staff's. This touches only formations neither has a use for.
/// </summary>
public static class ArmyDisposition
{
    public static int Relieved, Garrisoned, Homeward;

    private static readonly List<Army> Free = new();
    private static readonly Dictionary<int, float> Load = new();
    private static readonly Dictionary<int, float> Capacity = new();
    private static readonly Dictionary<int, List<Army>> ArmiesByNation = new();
    private static readonly Stack<List<Army>> ArmyListPool = new();
    private static Campaign _armiesFor;
    private static int _armiesDay = -1;
    private static int _armiesCount = -1;

    private static readonly HashSet<int> _disposeHeld = new();
    private static readonly Dictionary<int, List<Army>> _disposeHeapsByProv = new();
    private static readonly List<List<Army>> _disposeHeaps = new();
    private static readonly Stack<List<Army>> _disposeHeapPool = new();

    private static readonly Comparison<List<Army>> HeapInitialOrder = static (left, right) =>
    {
        var byCount = right.Count.CompareTo(left.Count);
        return byCount != 0 ? byCount : left[0].ProvinceId.CompareTo(right[0].ProvinceId);
    };

    private static readonly Comparison<List<Army>> HeapCountOrder =
        static (left, right) => right.Count.CompareTo(left.Count);

    private static int _garrisonSortCapitalStateMapIndex;
    private static readonly Comparison<ScenarioState> GarrisonOrder = static (left, right) =>
    {
        var leftCap = left.MapIndex == _garrisonSortCapitalStateMapIndex;
        var rightCap = right.MapIndex == _garrisonSortCapitalStateMapIndex;
        var byCap = rightCap.CompareTo(leftCap);
        if (byCap != 0) return byCap;
        var byPop = right.Population.CompareTo(left.Population);
        return byPop != 0 ? byPop : left.MapIndex.CompareTo(right.MapIndex);
    };

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same weekly reviews, with a frame boundary between countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || !campaign.Defines.ArmyDispositionEnabled) yield break;
        try
        {
            foreach (var nation in campaign.Nations)
            {
                if ((campaign.ElapsedDays + nation.Id * 3) % 7 != 0) continue;
                if (nation.Population <= 0 || nation.Landless) continue;
                if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command)) continue;
                foreach (var _ in ReviewStages(campaign, nation)) yield return 0;
                yield return 0;
            }
        }
        finally
        {
            ClearArmies();
        }
    }

    private static IReadOnlyList<Army> ArmiesOf(Campaign campaign, int nationId)
    {
        if (!ReferenceEquals(_armiesFor, campaign) || _armiesDay != campaign.ElapsedDays ||
            _armiesCount != campaign.Armies.Count)
        {
            ClearArmies();
            _armiesFor = campaign;
            _armiesDay = campaign.ElapsedDays;
            _armiesCount = campaign.Armies.Count;
            foreach (var army in campaign.Armies)
            {
                if (!ArmiesByNation.TryGetValue(army.NationId, out var list))
                    ArmiesByNation[army.NationId] = list = ArmyListPool.Count > 0 ? ArmyListPool.Pop() : new List<Army>();
                list.Add(army);
            }
        }
        return ArmiesByNation.TryGetValue(nationId, out var owned) ? owned : Array.Empty<Army>();
    }

    private static void ClearArmies()
    {
        foreach (var list in ArmiesByNation.Values)
        {
            list.Clear();
            ArmyListPool.Push(list);
        }
        ArmiesByNation.Clear();
        _armiesFor = null;
        _armiesDay = -1;
        _armiesCount = -1;
    }

    private static IEnumerable<int> ReviewStages(Campaign campaign, Nation nation)
    {
        var d = campaign.Defines;
        Free.Clear();
        var nationArmies = ArmiesOf(campaign, nation.Id);
        for (var i = 0; i < nationArmies.Count; i++)
        {
            var army = nationArmies[i];
            if (army.TemplateId == MilitiaCore.TemplateId) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.Route.Count > 0) continue;
            if (army.DirectOrder || army.AssignedWarId >= 0 || army.FrontLineId >= 0 ||
                army.PlanActive && army.OrderKind != ArmyOrder.Manual) continue;
            Free.Add(army);
        }
        if (Free.Count == 0) yield break;
        var atWar = AtWar(campaign, nation);
        var moves = 0;
        if (!atWar && ArmyHomecoming.NeedsReturns(campaign, nation, Free))
        {
            foreach (var delta in ArmyHomecoming.OrderReturnsStages(campaign, nation, Free, d.DispositionMovesPerWeek))
            {
                if (delta > 0) { moves += delta; Homeward += delta; }
                yield return 0;
            }
        }
        if (Free.Count == 0 || moves >= d.DispositionMovesPerWeek) yield break;
        Measure(campaign, nation, nationArmies);
        foreach (var added in RelieveStages(campaign, nation, d.DispositionMovesPerWeek - moves))
        {
            moves += added;
            yield return 0;
        }
        if (moves < d.DispositionMovesPerWeek && !atWar)
            foreach (var _ in GarrisonStages(campaign, nation, nationArmies, d.DispositionMovesPerWeek - moves))
                yield return 0;
    }

    /// <summary>Each hub's load and what it can carry, the way the logistics pass works them out.</summary>
    private static void Measure(Campaign campaign, Nation nation, IReadOnlyList<Army> nationArmies)
    {
        var d = campaign.Defines;
        Load.Clear();
        Capacity.Clear();
        foreach (var state in campaign.StatesOf(nation))
            Capacity[state.MapIndex] = d.BaseStateSupplyThroughput + LogisticsSystem.BuildingThroughput(d, state);
        for (var i = 0; i < nationArmies.Count; i++)
        {
            var army = nationArmies[i];
            if (army.SupplySourceStateMapIndex > 0)
                Load[army.SupplySourceStateMapIndex] = Load.GetValueOrDefault(army.SupplySourceStateMapIndex) + Demand(campaign, army);
        }
    }

    private static float Demand(Campaign campaign, Army army) =>
        army.Divisions * campaign.Defines.SupplyThroughputPerDivision * UnitTypeCatalog.Get(army.TemplateId).SupplyUse;

    private static readonly HashSet<int> RelieveSeen = new();
    private static readonly List<int> RelieveRingA = new();
    private static readonly List<int> RelieveRingB = new();
    private static readonly List<ScenarioState> GarrisonScratch = new();

    private static IEnumerable<int> RelieveStages(Campaign campaign, Nation nation, int budget)
    {
        var d = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var moves = 0;
        Free.Sort(static (left, right) =>
        {
            var byCongestion = left.SupplyCongestion.CompareTo(right.SupplyCongestion);
            return byCongestion != 0 ? byCongestion : left.Id.CompareTo(right.Id);
        });
        for (var fi = 0; fi < Free.Count; fi++)
        {
            if (moves >= budget) break;
            if (fi > 0 && fi % 4 == 0) yield return 0;
            var army = Free[fi];
            if (army.CutOff || army.ProvinceId <= 0 || army.ProvinceId >= map.ProvinceToState.Length) continue; // cut off is the staff's problem: it needs breaking out, not spreading
            if (army.SupplyCongestion >= d.DispositionCongestionLimit && army.SupplyLevel >= d.DispositionSupplyLimit) continue;
            var demand = Demand(campaign, army);
            var from = map.ProvinceToState[army.ProvinceId];
            // Outwards over our own ground: the nearest province whose hub has room for it.
            RelieveSeen.Clear();
            RelieveRingA.Clear();
            RelieveSeen.Add(army.ProvinceId);
            RelieveRingA.Add(army.ProvinceId);
            var ring = RelieveRingA;
            var nextRing = RelieveRingB;
            var target = 0;
            for (var step = 1; step <= d.DispositionSearchSteps && target == 0 && ring.Count > 0; step++)
            {
                nextRing.Clear();
                for (var ri = 0; ri < ring.Count; ri++)
                {
                    var neighbors = map.Neighbors[ring[ri]];
                    for (var ni = 0; ni < neighbors.Length; ni++)
                    {
                        var p = neighbors[ni];
                        if (p > 0 && p < controllers.Length && controllers[p] == nation.MapIndex &&
                            !map.IsWater(p) && RelieveSeen.Add(p))
                            nextRing.Add(p);
                    }
                }
                nextRing.Sort();
                for (var ni = 0; ni < nextRing.Count; ni++)
                {
                    var p = nextRing[ni];
                    var hub = map.ProvinceToState[p];
                    if (hub == from || !Capacity.TryGetValue(hub, out var room)) continue;
                    if (Load.GetValueOrDefault(hub) + demand > room) continue;
                    target = p;
                    break;
                }
                var tmp = ring;
                ring = nextRing;
                nextRing = tmp;
            }
            if (target == 0 || !Move(campaign, nation, army, target)) continue;
            Load[from] = MathF.Max(0f, Load.GetValueOrDefault(from) - demand);
            var to = map.ProvinceToState[target];
            Load[to] = Load.GetValueOrDefault(to) + demand;
            moves++; Relieved++;
            yield return 1;
        }
    }

    /// <summary>
    /// At peace: one formation in the capital, then one in each of the largest regions, taken
    /// from wherever the most are standing together - so the army is where the country is, not
    /// in one heap where the scenario left it.
    /// </summary>
    private static IEnumerable<int> GarrisonStages(Campaign campaign, Nation nation, IReadOnlyList<Army> nationArmies, int budget)
    {
        var d = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var wanted = Math.Max(1, (int)MathF.Round(Free.Count * d.DispositionGarrisonShare));
        GarrisonScratch.Clear();
        var states = campaign.StatesOf(nation);
        for (var i = 0; i < states.Length; i++)
            if (states[i].Population > 0) GarrisonScratch.Add(states[i]);
        if (GarrisonScratch.Count == 0) yield break;
        _garrisonSortCapitalStateMapIndex = nation.CapitalStateMapIndex;
        GarrisonScratch.Sort(GarrisonOrder);
        if (GarrisonScratch.Count > wanted) GarrisonScratch.RemoveRange(wanted, GarrisonScratch.Count - wanted);
        var regions = GarrisonScratch;

        // Who stands where, any formation of ours: a region with anybody in it is held.
        var held = _disposeHeld;
        held.Clear();
        for (var ai = 0; ai < nationArmies.Count; ai++)
        {
            var army = nationArmies[ai];
            if (army.NationId != nation.Id || army.ProvinceId <= 0) continue;
            var prov = army.Route.Count > 0 && army.OrderProvinceId > 0 ? army.OrderProvinceId : army.ProvinceId;
            if (prov > 0 && prov < map.ProvinceToState.Length)
                held.Add(map.ProvinceToState[prov]);
        }
        for (var hi = 0; hi < _disposeHeaps.Count; hi++) { _disposeHeaps[hi].Clear(); _disposeHeapPool.Push(_disposeHeaps[hi]); }
        _disposeHeaps.Clear();
        _disposeHeapsByProv.Clear();
        for (var fi = 0; fi < Free.Count; fi++)
        {
            var freeArmy = Free[fi];
            if (!_disposeHeapsByProv.TryGetValue(freeArmy.ProvinceId, out var bucket))
            {
                bucket = _disposeHeapPool.Count > 0 ? _disposeHeapPool.Pop() : new List<Army>();
                _disposeHeapsByProv[freeArmy.ProvinceId] = bucket;
            }
            bucket.Add(freeArmy);
        }
        foreach (var bucket in _disposeHeapsByProv.Values)
        {
            if (bucket.Count > 1) _disposeHeaps.Add(bucket);
            else { bucket.Clear(); _disposeHeapPool.Push(bucket); }
        }
        _disposeHeapsByProv.Clear();
        if (_disposeHeaps.Count == 0) yield break;
        _disposeHeaps.Sort(HeapInitialOrder);
        var heaps = _disposeHeaps;

        var moves = 0;
        for (var rgi = 0; rgi < regions.Count; rgi++)
        {
            var region = regions[rgi];
            if (moves >= budget) break;
            if (rgi > 0 && rgi % 4 == 0) yield return 0;
            if (held.Contains(region.MapIndex)) continue;
            // The province of the region we hold and can stand in: its capital's for the capital.
            var post = region.MapIndex == nation.CapitalStateMapIndex && nation.CapitalProvinceId > 0 &&
                       nation.CapitalProvinceId < controllers.Length &&
                       controllers[nation.CapitalProvinceId] == nation.MapIndex
                ? nation.CapitalProvinceId
                : StandingProvince(campaign, map, controllers, nation, region.MapIndex);
            if (post <= 0) continue;
            // A stack already standing on a foreign border stays. The extras that fill the
            // interior come from heaps behind the line, and only from the border if that is
            // the only pile the country has.
            List<Army> heap = null;
            List<Army> fallback = null;
            for (var hi = 0; hi < heaps.Count; hi++)
            {
                var group = heaps[hi];
                if (group.Count <= 1) continue;
                if (!BordersSomeone(map, controllers, nation, group[0].ProvinceId)) { heap = group; break; }
                fallback ??= group;
            }
            heap ??= fallback;
            if (heap is null) break;
            var centre = map.CenterOf(post);
            var army = heap[0];
            var bestDist = NavalOperations.Distance(army.Latitude, army.Longitude, centre.Latitude, centre.Longitude);
            for (var hgi = 1; hgi < heap.Count; hgi++)
            {
                var candidate = heap[hgi];
                var dist = NavalOperations.Distance(candidate.Latitude, candidate.Longitude, centre.Latitude, centre.Longitude);
                if (dist < bestDist || (dist == bestDist && candidate.Id < army.Id))
                {
                    army = candidate;
                    bestDist = dist;
                }
            }
            var moved = Move(campaign, nation, army, post);
            yield return 0;
            if (!moved) continue;
            heap.Remove(army);
            heaps.Sort(HeapCountOrder);
            held.Add(region.MapIndex);
            moves++; Garrisoned++;
        }
    }

    private static int StandingProvince(Campaign campaign, ProvinceTopology map, ushort[] controllers, Nation nation, int state)
    {
        var indexed = Corridors.FirstProvince(campaign, state);
        if (indexed > 0 && indexed < controllers.Length && controllers[indexed] == (ushort)nation.MapIndex && !map.IsWater(indexed))
            return indexed;
        for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
            if (map.ProvinceToState[province] == state && controllers[province] == (ushort)nation.MapIndex && !map.IsWater(province))
                return province;
        return 0;
    }

    private static bool BordersSomeone(ProvinceTopology map, ushort[] controllers, Nation nation, int province)
    {
        if (province <= 0 || province > map.ProvinceCount || province >= map.Neighbors.Length) return false;
        var neighbors = map.Neighbors[province];
        if (neighbors is null) return false;
        foreach (var next in neighbors)
        {
            if (next <= 0 || next > map.ProvinceCount || next >= controllers.Length || map.IsWater(next)) continue;
            var who = controllers[next];
            if (who != 0 && who != (ushort)nation.MapIndex) return true;
        }
        return false;
    }

    private static bool Move(Campaign campaign, Nation nation, Army army, int province)
    {
        var order = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = province };
        order.Apply(campaign);
        // The country's own disposition, not a player's order: the staff can still take it.
        army.DirectOrder = false;
        return army.OrderProvinceId == province;
    }

    private static bool AtWar(Campaign campaign, Nation nation)
    {
        foreach (var war in Warfare.LiveWars(campaign))
            if (war.Involves(nation.Id)) return true;
        return false;
    }
}
