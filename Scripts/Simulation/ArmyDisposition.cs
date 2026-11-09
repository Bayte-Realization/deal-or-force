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

    public static void Tick(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || !campaign.Defines.ArmyDispositionEnabled) return;
        foreach (var nation in campaign.Nations)
        {
            if ((campaign.ElapsedDays + nation.Id * 3) % 7 != 0) continue;
            if (nation.Population <= 0 || nation.Landless) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command)) continue;
            Review(campaign, nation);
        }
    }

    private static void Review(Campaign campaign, Nation nation)
    {
        var d = campaign.Defines;
        Free.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.TemplateId == MilitiaCore.TemplateId) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.Route.Count > 0) continue;
            if (army.DirectOrder || army.AssignedWarId >= 0 || army.FrontLineId >= 0 ||
                army.PlanActive && army.OrderKind != ArmyOrder.Manual) continue;
            Free.Add(army);
        }
        if (Free.Count == 0) return;
        var atWar = AtWar(campaign, nation);
        var moves = atWar ? 0 : ArmyHomecoming.OrderReturns(campaign, nation, Free, d.DispositionMovesPerWeek);
        Homeward += moves;
        if (Free.Count == 0 || moves >= d.DispositionMovesPerWeek) return;
        Measure(campaign, nation);
        moves += Relieve(campaign, nation, d.DispositionMovesPerWeek - moves);
        if (moves < d.DispositionMovesPerWeek && !atWar)
            Garrison(campaign, nation, d.DispositionMovesPerWeek - moves);
    }

    /// <summary>Each hub's load and what it can carry, the way the logistics pass works them out.</summary>
    private static void Measure(Campaign campaign, Nation nation)
    {
        var d = campaign.Defines;
        Load.Clear();
        Capacity.Clear();
        foreach (var state in campaign.StatesOf(nation))
            Capacity[state.MapIndex] = d.BaseStateSupplyThroughput + LogisticsSystem.BuildingThroughput(d, state);
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id && army.SupplySourceStateMapIndex > 0)
                Load[army.SupplySourceStateMapIndex] = Load.GetValueOrDefault(army.SupplySourceStateMapIndex) + Demand(campaign, army);
    }

    private static float Demand(Campaign campaign, Army army) =>
        army.Divisions * campaign.Defines.SupplyThroughputPerDivision * UnitTypeCatalog.Get(army.TemplateId).SupplyUse;

    private static int Relieve(Campaign campaign, Nation nation, int budget)
    {
        var d = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var moves = 0;
        foreach (var army in Free.OrderBy(a => a.SupplyCongestion).ThenBy(a => a.Id).ToList())
        {
            if (moves >= budget) break;
            if (army.CutOff || army.ProvinceId <= 0 || army.ProvinceId >= map.ProvinceToState.Length) continue; // cut off is the staff's problem: it needs breaking out, not spreading
            if (army.SupplyCongestion >= d.DispositionCongestionLimit && army.SupplyLevel >= d.DispositionSupplyLimit) continue;
            var demand = Demand(campaign, army);
            var from = map.ProvinceToState[army.ProvinceId];
            // Outwards over our own ground: the nearest province whose hub has room for it.
            var seen = new HashSet<int> { army.ProvinceId };
            var ring = new List<int> { army.ProvinceId };
            var target = 0;
            for (var step = 1; step <= d.DispositionSearchSteps && target == 0 && ring.Count > 0; step++)
            {
                ring = ring.SelectMany(p => map.Neighbors[p])
                    .Where(p => p > 0 && p < controllers.Length && controllers[p] == nation.MapIndex &&
                                !map.IsWater(p) && seen.Add(p)).OrderBy(p => p).ToList();
                foreach (var p in ring)
                {
                    var hub = map.ProvinceToState[p];
                    if (hub == from || !Capacity.TryGetValue(hub, out var room)) continue;
                    if (Load.GetValueOrDefault(hub) + demand > room) continue;
                    target = p;
                    break;
                }
            }
            if (target == 0 || !Move(campaign, nation, army, target)) continue;
            Load[from] = MathF.Max(0f, Load.GetValueOrDefault(from) - demand);
            var to = map.ProvinceToState[target];
            Load[to] = Load.GetValueOrDefault(to) + demand;
            moves++; Relieved++;
        }
        return moves;
    }

    /// <summary>
    /// At peace: one formation in the capital, then one in each of the largest regions, taken
    /// from wherever the most are standing together - so the army is where the country is, not
    /// in one heap where the scenario left it.
    /// </summary>
    private static void Garrison(Campaign campaign, Nation nation, int budget)
    {
        var d = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var wanted = Math.Max(1, (int)MathF.Round(Free.Count * d.DispositionGarrisonShare));
        var regions = campaign.StatesOf(nation).Where(s => s.Population > 0)
            .OrderByDescending(s => s.MapIndex == nation.CapitalStateMapIndex)
            .ThenByDescending(s => s.Population).ThenBy(s => s.MapIndex).Take(wanted).ToList();
        if (regions.Count == 0) return;

        // Who stands where, any formation of ours: a region with anybody in it is held.
        var held = new HashSet<int>();
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.ProvinceId <= 0) continue;
            var prov = army.Route.Count > 0 && army.OrderProvinceId > 0 ? army.OrderProvinceId : army.ProvinceId;
            if (prov > 0 && prov < map.ProvinceToState.Length)
                held.Add(map.ProvinceToState[prov]);
        }
        var heaps = Free.GroupBy(a => a.ProvinceId).Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.ToList()).ToList();
        if (heaps.Count == 0) return;

        var moves = 0;
        foreach (var region in regions)
        {
            if (moves >= budget) break;
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
            var heap = heaps.FirstOrDefault(group => group.Count > 1 && !BordersSomeone(map, controllers, nation, group[0].ProvinceId))
                ?? heaps.FirstOrDefault(group => group.Count > 1);
            if (heap is null) break;
            var centre = map.CenterOf(post);
            var army = heap.OrderBy(a => NavalOperations.Distance(a.Latitude, a.Longitude, centre.Latitude, centre.Longitude))
                .ThenBy(a => a.Id).First();
            if (!Move(campaign, nation, army, post)) continue;
            heap.Remove(army);
            heaps.Sort((left, right) => right.Count.CompareTo(left.Count));
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
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) return true;
        return false;
    }
}
