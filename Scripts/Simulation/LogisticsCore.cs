using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Province-graph logistics shared by player and AI. Home infrastructure creates hubs;
/// distance, local throughput and formation demand determine the supply that reaches an army.
/// </summary>
public static class LogisticsSystem
{
    public static void Tick(Campaign campaign)
    {
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        if (campaign.ElapsedDays > 1 && campaign.ElapsedDays % 3 != 0) return;

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var controlled = new Dictionary<ushort, List<int>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var map = controllers[province];
            if (map == 0) continue;
            if (!controlled.TryGetValue(map, out var list)) controlled[map] = list = new List<int>();
            list.Add(province);
        }

        foreach (var nation in campaign.Nations)
        {
            var armies = campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
            if (armies.Length == 0) continue;
            UpdateNation(campaign, nation, armies, controlled.GetValueOrDefault((ushort)nation.MapIndex));
        }
    }

    private static void UpdateNation(Campaign campaign, Nation nation, Army[] armies, List<int> territory)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || territory is null || territory.Count == 0)
        {
            foreach (var army in armies) SetStranded(army);
            return;
        }

        var stateById = campaign.StatesOf(nation)
            .ToDictionary(state => state.MapIndex);
        var hubByState = new Dictionary<int, int>();
        foreach (var province in territory)
        {
            if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
            var state = topology.ProvinceToState[province];
            if (!hubByState.ContainsKey(state)) hubByState[state] = province;
        }

        var distance = new int[topology.ProvinceCount + 1];
        var source = new int[topology.ProvinceCount + 1];
        Array.Fill(distance, -1);
        Array.Fill(source, -1);
        var queue = new Queue<int>();
        foreach (var (state, province) in hubByState)
        {
            distance[province] = 0;
            source[province] = state;
            queue.Enqueue(province);
        }
        while (queue.Count > 0)
        {
            var province = queue.Dequeue();
            if (distance[province] >= campaign.Defines.SupplyTraceRange) continue;
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (distance[neighbor] >= 0 || controllers[neighbor] != nation.MapIndex) continue;
                distance[neighbor] = distance[province] + 1;
                source[neighbor] = source[province];
                queue.Enqueue(neighbor);
            }
        }

        var demandBySource = new Dictionary<int, float>();
        foreach (var army in armies)
        {
            var sourceState = army.ProvinceId > 0 && army.ProvinceId < source.Length ? source[army.ProvinceId] : -1;
            if (sourceState >= 0)
                demandBySource[sourceState] = demandBySource.GetValueOrDefault(sourceState) +
                    army.Divisions * campaign.Defines.SupplyThroughputPerDivision *
                    UnitTypeCatalog.Get(army.TemplateId).SupplyUse;
        }

        foreach (var army in armies)
        {
            var province = army.ProvinceId;
            var reachable = province > 0 && province < distance.Length && distance[province] >= 0;
            if (!reachable) { SetStranded(army); continue; }
            var sourceState = source[province];
            stateById.TryGetValue(sourceState, out var state);
            var infrastructure = state?.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) ?? 0;
            var barracks = state?.Buildings.GetValueOrDefault(BuildingKind.Barracks) ?? 0;
            // A modern state moves its army on railways and roads, not on the three
            // divisions' worth of throughput a bare province used to allow. Infrastructure
            // is what a player builds to widen this, so it carries the most weight.
            var capacity = campaign.Defines.BaseStateSupplyThroughput +
                           infrastructure * campaign.Defines.SupplyPerInfrastructure +
                           barracks * campaign.Defines.SupplyPerBarracks;
            var demand = MathF.Max(0.01f, demandBySource.GetValueOrDefault(sourceState));
            var congestion = M.Clamp(capacity / demand, 0.08f, 1f);
            var range = Math.Max(1, campaign.Defines.SupplyTraceRange);
            var distanceFactor = M.Clamp(1f - distance[province] / (float)range * 0.62f, 0.22f, 1f);
            var lakeMultiplier = topology.HasLakeAccess(province) ? 1f + campaign.Defines.LakeSupplyBonus : 1f;
            var target = M.Clamp01(congestion * distanceFactor * lakeMultiplier);
            army.SupplyDistance = distance[province];
            army.SupplySourceStateMapIndex = sourceState;
            army.SupplyCongestion = congestion;
            army.CutOff = false;
            army.SupplyLevel = M.Lerp(army.SupplyLevel, target, 0.34f);
        }

        var barracksTotal = stateById.Values.Sum(state => state.Buildings.GetValueOrDefault(BuildingKind.Barracks));
        var spendingFactor = M.Clamp(nation.MilitarySpending /
            MathF.Max(0.01f, campaign.Defines.BaselineMilitarySpending), 0.25f, 2.5f);
        nation.ReinforcementCapacity = (MathF.Max(80f, MilitaryBootstrap.Personnel(nation.Tag, nation.Population) * 0.001f) +
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
