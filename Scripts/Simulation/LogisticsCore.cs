using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Province-graph logistics shared by player and AI. Home infrastructure creates hubs;
/// distance, local throughput and formation demand determine the supply that reaches an army.
///
/// This pass used to make three quarters of the whole simulation's garbage - sixteen
/// megabytes a campaign day, eleven gigabytes over two years - because it gave every one of
/// the two hundred and sixty-one nations two fresh arrays the length of the province map,
/// three hundred kilobytes a nation, and then filled both with -1 before using perhaps a
/// few hundred entries of either. At speed five that is a hundred megabytes a second handed
/// to the collector, which is what a player feels as the game stuttering and the memory
/// climbing.
///
/// Nothing about the algorithm needed that. The buffers are scratch: each nation overwrites
/// what the last one wrote. So they are allocated once and kept, and instead of clearing
/// them a generation stamp says which entries belong to the nation being worked on now -
/// which also removes the seventy-five thousand writes per nation that the clearing was.
/// Everything else here works the same way it did, to the last digit of the bench.
/// </summary>
public static class LogisticsSystem
{
    // Scratch, reused across nations and across days. The simulation is single-threaded by
    // construction - it has to be, to stay replayable - so one set is enough.
    private static int[] _distance = Array.Empty<int>();
    private static int[] _source = Array.Empty<int>();
    private static int[] _stamp = Array.Empty<int>();
    // Which provinces the network reaches from its roots, stamped like the trace below.
    private static int[] _reach = Array.Empty<int>();
    private static int _generation;

    private static readonly Queue<int> Frontier = new();
    private static readonly List<int> Roots = new();
    private static readonly Dictionary<int, int> HubByState = new();
    private static readonly Dictionary<int, float> DemandBySource = new();
    private static readonly Dictionary<int, ScenarioState> StateById = new();
    private static readonly Dictionary<ushort, List<int>> Controlled = new();
    private static readonly Dictionary<int, List<Army>> ArmiesByNation = new();
    private static readonly Dictionary<int, bool> IsIsolatedHub = new();

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(LogisticsSystem))) return;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        // Every third day, and once on it. Without the first half of this a campaign
        // stepping an hour at a time rebuilt the whole supply network twenty-four times on
        // the third day and not at all on the other two.
        if (!campaign.StepOpensNewDay) return;
        if (campaign.ElapsedDays > 1 && campaign.ElapsedDays % 3 != 0) return;

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;

        // Who holds what. The lists are kept between ticks and emptied rather than dropped,
        // so the province scan costs the scan and nothing else.
        foreach (var list in Controlled.Values) list.Clear();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var map = controllers[province];
            if (map == 0) continue;
            if (!Controlled.TryGetValue(map, out var list)) Controlled[map] = list = new List<int>();
            list.Add(province);
        }

        // One pass over the armies instead of one pass per nation. With two hundred and
        // sixty-one nations and fourteen hundred formations that is three hundred and
        // sixty-five thousand comparisons a tick turned into fourteen hundred.
        foreach (var list in ArmiesByNation.Values) list.Clear();
        foreach (var army in campaign.Armies)
        {
            if (!ArmiesByNation.TryGetValue(army.NationId, out var list))
                ArmiesByNation[army.NationId] = list = new List<Army>();
            list.Add(army);
        }

        var length = topology.ProvinceCount + 1;
        if (_distance.Length < length)
        {
            _distance = new int[length];
            _source = new int[length];
            _stamp = new int[length];
            _reach = new int[length];
            _generation = 0;
        }

        foreach (var nation in campaign.Nations)
        {
            if (!ArmiesByNation.TryGetValue(nation.Id, out var armies) || armies.Count == 0) continue;
            UpdateNation(campaign, nation, armies, Controlled.GetValueOrDefault((ushort)nation.MapIndex));
        }
    }

    private static void UpdateNation(Campaign campaign, Nation nation, List<Army> armies, List<int> territory)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || territory is null || territory.Count == 0)
        {
            foreach (var army in armies)
                if (!ProvisionVisitor(campaign, nation, army)) SetStranded(army);
            return;
        }

        StateById.Clear();
        foreach (var state in campaign.StatesOf(nation)) StateById[state.MapIndex] = state;

        // A new generation is this nation's claim on the scratch: an entry counts as
        // written only if it carries the number below. Nothing has to be cleared.
        var generation = ++_generation;
        var distance = _distance;
        var source = _source;
        var stamp = _stamp;

        // Which of the country's ground the network actually reaches. Only that ground can
        // hold a depot, which is a different question from which ground it owns - see
        // CollectRoots and TraceNetwork.
        var reach = _reach;
        CollectRoots(nation, territory, topology, controllers);
        TraceNetwork(nation, topology, controllers, generation);

        HubByState.Clear();
        IsIsolatedHub.Clear();
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
            // A lake keeps no depot, however much of it a country owns.
            if (topology.IsWater(province)) continue;
            var isConnected = reach[province] == generation;
            var state = topology.ProvinceToState[province];
            if (!isConnected)
            {
                // Isolated enclaves without capital land/port access have degraded depot capacity
                // and require an established local barracks/depot to serve as an emergency hub.
                if (!StateById.TryGetValue(state, out var st) || st.Buildings.GetValueOrDefault(BuildingKind.Barracks) <= 0)
                    continue;
                IsIsolatedHub[state] = true;
            }
            if (!HubByState.ContainsKey(state)) HubByState[state] = province;
        }

        Frontier.Clear();
        foreach (var (state, province) in HubByState)
        {
            stamp[province] = generation;
            distance[province] = 0;
            source[province] = state;
            Frontier.Enqueue(province);
        }
        var range = campaign.Defines.SupplyTraceRange;
        while (Frontier.Count > 0)
        {
            var province = Frontier.Dequeue();
            if (distance[province] >= range) continue;
            var next = distance[province] + 1;
            var from = source[province];
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (stamp[neighbor] == generation || controllers[neighbor] != nation.MapIndex) continue;
                stamp[neighbor] = generation;
                // Water is transparent to a supply line and impassable to a column. An
                // army cannot march across a lake; the lorries that feed it go round by
                // the roads on the shore, and charging that as extra hops of a trace
                // budget would be inventing a cost the map does not model anywhere else.
                // Before lakes were regions this ground was land and cost nothing extra;
                // it should not start costing something because it was given a name.
                distance[neighbor] = topology.IsWater(neighbor) ? distance[province] : next;
                source[neighbor] = from;
                Frontier.Enqueue(neighbor);
            }
        }

        SupplyBySea(campaign, nation, armies, controllers, generation);

        DemandBySource.Clear();
        foreach (var army in armies)
        {
            var at = army.ProvinceId;
            if (at <= 0 || at >= stamp.Length || stamp[at] != generation) continue;
            var sourceState = source[at];
            DemandBySource[sourceState] = DemandBySource.GetValueOrDefault(sourceState) +
                army.Divisions * campaign.Defines.SupplyThroughputPerDivision *
                UnitTypeCatalog.Get(army.TemplateId).SupplyUse;
        }

        foreach (var army in armies)
        {
            if (ProvisionVisitor(campaign, nation, army)) continue;
            // A formation at sea is supplied by the ship it is on, not by the province
            // network. Marking it cut off would starve an invasion before it landed.
            if (army.EmbarkedFleetId >= 0)
            {
                army.CutOff = false;
                army.DaysCutOff = 0;
                army.SupplyDistance = 0;
                army.SupplyCongestion = 1f;
                continue;
            }

            var province = army.ProvinceId;
            if (province <= 0 || province >= stamp.Length || stamp[province] != generation)
            {
                SetStranded(army);
                continue;
            }
            var sourceState = source[province];
            StateById.TryGetValue(sourceState, out var state);
            var infrastructure = state?.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) ?? 0;
            var barracks = state?.Buildings.GetValueOrDefault(BuildingKind.Barracks) ?? 0;
            // A modern state moves its army on railways and roads, not on the three
            // divisions' worth of throughput a bare province used to allow. Infrastructure
            // is what a player builds to widen this, so it carries the most weight.
            // Winter narrows the same road. It does not invent a second supply network.
            var centre = topology.CenterOf(province);
            var damageFactor = state is not null ? (1f - MathF.Min(0.85f, state.WarDamage * 0.9f)) : 1f;
            var enclaveFactor = IsIsolatedHub.GetValueOrDefault(sourceState) ? 0.35f : 1f;
            var capacity = (campaign.Defines.BaseStateSupplyThroughput +
                           infrastructure * campaign.Defines.SupplyPerInfrastructure +
                           barracks * campaign.Defines.SupplyPerBarracks) *
                          damageFactor * enclaveFactor *
                          WaterConditions.RoadThroughput(campaign, centre.Latitude);
            var demand = MathF.Max(0.01f, DemandBySource.GetValueOrDefault(sourceState));
            var congestion = M.Clamp(capacity / demand, 0.08f, 1f);
            var traceRange = Math.Max(1, campaign.Defines.SupplyTraceRange);
            var distanceFactor = M.Clamp(1f - distance[province] / (float)traceRange * 0.62f, 0.22f, 1f);
            var lakeMultiplier = WaterConditions.LakeSupplyMultiplier(campaign, province);

            // A fleet sitting off the coast takes what arrives by sea. This is the one
            // thing a navy does that a land player feels immediately: an army on a
            // blockaded shore stops being supplied, and stops being able to attack.
            var blockade = NavalOperations.BlockadePressure(campaign, nation, centre.Latitude, centre.Longitude);
            var blockadeFactor = 1f - blockade * campaign.Defines.FleetBlockadeSupplyPenalty;
            var target = M.Clamp01(congestion * distanceFactor * lakeMultiplier * blockadeFactor *
                                   topology.TerrainAt(province).Supply * NationalModifiers.Factor(nation, "supply"));
            army.SupplyDistance = distance[province];
            army.SupplySourceStateMapIndex = sourceState;
            army.SupplyCongestion = congestion;
            army.CutOff = false;
            army.DaysCutOff = 0;
            army.SupplyLevel = M.Lerp(army.SupplyLevel, target, 0.34f);
        }

        var barracksTotal = 0;
        foreach (var state in StateById.Values)
            barracksTotal += state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var spendingFactor = M.Clamp(nation.MilitarySpending /
            MathF.Max(0.01f, campaign.Defines.BaselineMilitarySpending), 0.25f, 2.5f);
        nation.ReinforcementCapacity = (MathF.Max(80f, MilitaryBootstrap.Personnel(nation.Tag, nation.Population) *
                                            campaign.Defines.ReinforcementCapacityShare) +
                                        barracksTotal * 120f) * spendingFactor *
                                       NationalModifiers.Factor(nation, "reinforcement_rate");
        if (campaign.ElapsedDays % 7 == 0) nation.ReinforcedLastWeek = 0f;
    }

    /// <summary>
    /// Supply by sea, for ground of one's own that no road reaches.
    ///
    /// The trace above walks the land, so every island that is not a region of its own was
    /// cut off from the moment the campaign began - and the deployment put divisions on
    /// them: six of Japan's seventeen, on Rishiri, Shikoku's islets and the Amakusa chain,
    /// starved to six per cent and were buried inside six weeks of peace. The same trace
    /// starved every beachhead an invasion took, because a beachhead is exactly ground no
    /// road reaches.
    ///
    /// So a formation on a coastal province its country holds, that the land trace did not
    /// reach, is supplied through the nearest port the trace did reach on the same sea,
    /// SeaSupplyHops further away than that port is. A blockade still bites: the supply
    /// that arrives is charged for any enemy fleet sitting off it, below, like any coast's.
    /// </summary>
    private static void SupplyBySea(Campaign campaign, Nation nation, List<Army> armies, ushort[] controllers,
        int generation)
    {
        var topology = campaign.ProvinceMap;
        var stamp = _stamp;
        List<int> ports = null;
        foreach (var army in armies)
        {
            if (army.EmbarkedFleetId >= 0) continue;
            var province = army.ProvinceId;
            if (province <= 0 || province >= stamp.Length || stamp[province] == generation) continue;
            if (controllers[province] != nation.MapIndex) continue;
            if (!SeaLanes.TryHarbourPoint(topology, province, out var latitude, out var longitude)) continue;
            var basin = SeaLanes.HarbourBasin(topology, province);

            // The ports the land trace reached, found once for the country and only if some
            // formation needs one.
            if (ports is null)
            {
                ports = new List<int>();
                foreach (var coast in SeaLanes.CoastalProvinces(topology))
                    if (coast < stamp.Length && stamp[coast] == generation && controllers[coast] == nation.MapIndex)
                        ports.Add(coast);
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            foreach (var port in ports)
            {
                if (SeaLanes.HarbourBasin(topology, port) != basin) continue;
                if (!SeaLanes.TryHarbourPoint(topology, port, out var portLatitude, out var portLongitude)) continue;
                var distance = NavalOperations.Distance(latitude, longitude, portLatitude, portLongitude);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = port;
            }
            if (best < 0) continue;
            stamp[province] = generation;
            _distance[province] = _distance[best] + Math.Max(0, campaign.Defines.SeaSupplyHops);
            _source[province] = _source[best];
        }
    }

    /// <summary>
    /// Where a country's supply network begins: the capital, or, when the capital has fallen,
    /// the depots the country still holds, or, in the last case, the ground itself.
    ///
    /// A province used to be a source of supply the moment the country owned it, at distance
    /// zero. Two things followed, and a player feels both. A division encircled on a scrap of
    /// national soil was fed from that scrap for as long as it stood there, so closing a pocket
    /// bought nothing until the ground itself fell; and cutting the corridor between a capital
    /// and its front cost the enemy nothing at all, because every province behind the cut was
    /// still a source of its own. Supply begins at a root and travels over ground the country
    /// controls, which is what makes a corridor worth taking.
    /// </summary>
    private static void CollectRoots(Nation nation, List<int> territory, ProvinceTopology topology,
        ushort[] controllers)
    {
        Roots.Clear();
        var capital = nation.CapitalProvinceId;
        if (capital > 0 && capital < controllers.Length && controllers[capital] == nation.MapIndex &&
            !topology.IsWater(capital))
        {
            Roots.Add(capital);
            return;
        }
        var capitalState = nation.CapitalStateMapIndex;
        if (capitalState > 0)
        {
            foreach (var province in territory)
            {
                if (topology.ProvinceToState[province] != capitalState) continue;
                if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
                if (topology.IsWater(province)) continue;
                Roots.Add(province);
                return;
            }
        }
        // The capital is gone. A state keeps its stores in a barracks, so the depots that are
        // still standing are the roots - and if not one of them is, the ground the country
        // stands on is: a state pushed off its own roads should be in trouble, not out of
        // supply the same afternoon.
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
            if (topology.IsWater(province)) continue;
            if (!StateById.TryGetValue(topology.ProvinceToState[province], out var state)) continue;
            if (state.Buildings.GetValueOrDefault(BuildingKind.Barracks) <= 0) continue;
            Roots.Add(province);
        }
        if (Roots.Count > 0) return;
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
            if (topology.IsWater(province)) continue;
            Roots.Add(province);
        }
    }

    /// <summary>
    /// The ground the network reaches from its roots: over land the country controls, and then,
    /// if it holds a port, along its own coast.
    ///
    /// No hop budget here. The budget belongs to distance - a trace twenty provinces long is
    /// still a trace - and spending it here would cut every country wider than twenty provinces
    /// in half on the first day of the campaign.
    ///
    /// A sea lane is a road, and for the same reason: without it an island of one's own is cut
    /// off from its capital the moment the campaign begins. That is the starvation SupplyBySea
    /// repairs, one formation at a time, for ground no road reaches. The basin two shores
    /// share is not asked here because the thing that would have to ask it is a navy, and the
    /// navy is already modelled: a fleet off the coast charges that coast a blockade penalty when
    /// its supply arrives. Asking the basin would instead have starved every overseas garrison
    /// in the game, which is the worse guess of the two.
    /// </summary>
    private static void TraceNetwork(Nation nation, ProvinceTopology topology, ushort[] controllers,
        int generation)
    {
        var reach = _reach;
        Frontier.Clear();
        foreach (var root in Roots)
        {
            if (reach[root] == generation) continue;
            reach[root] = generation;
            Frontier.Enqueue(root);
        }
        while (Frontier.Count > 0)
        {
            var province = Frontier.Dequeue();
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (reach[neighbor] == generation || controllers[neighbor] != nation.MapIndex) continue;
                reach[neighbor] = generation;
                Frontier.Enqueue(neighbor);
            }
        }

        var coasts = SeaLanes.CoastalProvinces(topology);
        if (coasts.Count == 0) return;
        var openBasins = new HashSet<int>();
        foreach (var coast in coasts)
        {
            if (reach[coast] == generation && controllers[coast] == nation.MapIndex)
            {
                var basin = SeaLanes.HarbourBasin(topology, coast);
                if (basin >= 0) openBasins.Add(basin);
            }
        }
        if (openBasins.Count == 0) return;
        foreach (var coast in coasts)
        {
            if (controllers[coast] == nation.MapIndex && openBasins.Contains(SeaLanes.HarbourBasin(topology, coast)))
                reach[coast] = generation;
        }
    }

    private static void SetStranded(Army army)
    {
        army.CutOff = true;
        army.DaysCutOff++;
        army.SupplyDistance = -1;
        army.SupplySourceStateMapIndex = -1;
        army.SupplyCongestion = 0f;
        army.SupplyLevel = M.Lerp(army.SupplyLevel, 0.06f, 0.38f);
    }

    private static bool ProvisionVisitor(Campaign c, Nation nation, Army army)
    {
        var at = army.ProvinceId;
        if (army.EmbarkedFleetId >= 0 || at <= 0 || at >= c.ProvinceController.Length) return false;
        var controller = c.ProvinceController[at];
        if (controller == nation.MapIndex || controller == 0 ||
            !c.NationsByMapIndex().TryGetValue(controller, out var host) || MilitaryAccess.Hostile(c, nation.Id, host.Id)) return false;
        // The host provisions an authorised visitor or an interned column awaiting return;
        // this is not a friendly land trace into an enemy pocket.
        MilitaryAccess.Provision(army);
        return true;
    }
}
