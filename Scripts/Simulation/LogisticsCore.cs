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
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
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
            foreach (var army in armies) SetStranded(army);
            return;
        }

        StateById.Clear();
        foreach (var state in campaign.StatesOf(nation)) StateById[state.MapIndex] = state;

        HubByState.Clear();
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
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
                distance[neighbor] = next;
                source[neighbor] = from;
                Frontier.Enqueue(neighbor);
            }
        }

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
            // A formation at sea is supplied by the ship it is on, not by the province
            // network. Marking it cut off would starve an invasion before it landed.
            if (army.EmbarkedFleetId >= 0)
            {
                army.CutOff = false;
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
            var capacity = campaign.Defines.BaseStateSupplyThroughput +
                           infrastructure * campaign.Defines.SupplyPerInfrastructure +
                           barracks * campaign.Defines.SupplyPerBarracks;
            var demand = MathF.Max(0.01f, DemandBySource.GetValueOrDefault(sourceState));
            var congestion = M.Clamp(capacity / demand, 0.08f, 1f);
            var traceRange = Math.Max(1, campaign.Defines.SupplyTraceRange);
            var distanceFactor = M.Clamp(1f - distance[province] / (float)traceRange * 0.62f, 0.22f, 1f);
            var lakeMultiplier = topology.HasLakeAccess(province) ? 1f + campaign.Defines.LakeSupplyBonus : 1f;

            // A fleet sitting off the coast takes what arrives by sea. This is the one
            // thing a navy does that a land player feels immediately: an army on a
            // blockaded shore stops being supplied, and stops being able to attack.
            var centre = topology.CenterOf(province);
            var blockade = NavalOperations.BlockadePressure(campaign, nation, centre.Latitude, centre.Longitude);
            var blockadeFactor = 1f - blockade * campaign.Defines.FleetBlockadeSupplyPenalty;
            var target = M.Clamp01(congestion * distanceFactor * lakeMultiplier * blockadeFactor);
            army.SupplyDistance = distance[province];
            army.SupplySourceStateMapIndex = sourceState;
            army.SupplyCongestion = congestion;
            army.CutOff = false;
            army.SupplyLevel = M.Lerp(army.SupplyLevel, target, 0.34f);
        }

        var barracksTotal = 0;
        foreach (var state in StateById.Values)
            barracksTotal += state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var spendingFactor = M.Clamp(nation.MilitarySpending /
            MathF.Max(0.01f, campaign.Defines.BaselineMilitarySpending), 0.25f, 2.5f);
        nation.ReinforcementCapacity = (MathF.Max(80f, MilitaryBootstrap.Personnel(nation.Tag, nation.Population) *
                                            campaign.Defines.ReinforcementCapacityShare) +
                                        barracksTotal * 120f) * spendingFactor;
        if (campaign.ElapsedDays % 7 == 0) nation.ReinforcedLastWeek = 0f;
    }

    private static void SetStranded(Army army)
    {
        army.CutOff = true;
        army.SupplyDistance = -1;
        army.SupplySourceStateMapIndex = -1;
        army.SupplyCongestion = 0f;
        army.SupplyLevel = M.Lerp(army.SupplyLevel, 0.06f, 0.38f);
    }
}
