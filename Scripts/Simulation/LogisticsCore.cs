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
    private static int _generation;

    private static readonly Queue<int> Frontier = new();
    private static readonly Dictionary<int, int> HubByState = new();
    private static readonly Dictionary<int, float> DemandBySource = new();
    private static readonly Dictionary<int, ScenarioState> StateById = new();
    private static readonly Dictionary<ushort, List<int>> Controlled = new();
    private static readonly Dictionary<int, List<Army>> ArmiesByNation = new();

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

        HubByState.Clear();
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
            // A lake keeps no depot, however much of it a country owns.
            if (topology.IsWater(province)) continue;
            var state = topology.ProvinceToState[province];
            if (!HubByState.ContainsKey(state)) HubByState[state] = province;
        }

        // A new generation is this nation's claim on the scratch: an entry counts as
        // written only if it carries the number below. Nothing has to be cleared.
        var generation = ++_generation;
        var distance = _distance;
        var source = _source;
        var stamp = _stamp;

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
            var capacity = (campaign.Defines.BaseStateSupplyThroughput +
                           infrastructure * campaign.Defines.SupplyPerInfrastructure +
                           barracks * campaign.Defines.SupplyPerBarracks) *
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

    private static void SetStranded(Army army)
    {
        army.CutOff = true;
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
