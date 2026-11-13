using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Region adjacency and border grievances read ownership, not who stands on a province.
/// A capture in a long war must not walk the provinces again, and a second campaign must
/// not be handed the first campaign's tables.
/// </summary>
static class BorderAdjacencyTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("border adjacency: " + note); Console.WriteLine("PASS border adjacency: " + note); }

    public static void Run()
    {
        var first = World(61109, stateA: 1, stateB: 2, ownerA: 10, ownerB: 11);
        var second = World(61110, stateA: 3, stateB: 4, ownerA: 12, ownerB: 13);
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var nationA = first.Nations[0];
        var nationB = first.Nations[1];

        var edge = TerritorialCoherence.RegionBorders(first, 1);
        Check(edge.TryGetValue(2, out var edges) && edges == 1,
            "the first campaign records one edge from region 1 to region 2");
        Check(TerritorialCoherence.TouchesOwnedGround(first, nationB, 1),
            "region 1 touches the owner of region 2");
        var grievance = BorderShape.Grievance(first, nationA, nationB);
        Check(grievance > 0f, "two regions that wrap each other are a grievance");
        var neighbours = TerritorialCoherence.NeighbourTables(first);
        var provinces = BorderShape.ProvinceLists(first);
        var regionList = provinces[1];
        Check(regionList.SequenceEqual(new[] { 1 }), "region 1's province list is province 1");
        Check(TerritorialCoherence.NeighbourTables(second) is null && BorderShape.ProvinceLists(second) is null,
            "a second campaign does not observe the first campaign's tables");

        TerritorialCoherence.RegionBorders(second, 3);
        BorderShape.Grievance(second, second.Nations[0], second.Nations[1]);
        Check(TerritorialCoherence.RegionBorders(second, 3).TryGetValue(4, out var otherEdges) && otherEdges == 1,
            "the second campaign records only its own edge");
        Check(ReferenceEquals(neighbours, TerritorialCoherence.NeighbourTables(first)) &&
              neighbours[1].TryGetValue(2, out var still) && still == 1 &&
              ReferenceEquals(provinces, BorderShape.ProvinceLists(first)) &&
              ReferenceEquals(regionList, provinces[1]) && regionList.SequenceEqual(new[] { 1 }),
            "building the second campaign leaves the first campaign's tables in place");
        Check(!ReferenceEquals(neighbours, TerritorialCoherence.NeighbourTables(second)) &&
              !ReferenceEquals(provinces, BorderShape.ProvinceLists(second)) &&
              !ReferenceEquals(regionList, BorderShape.ProvinceLists(second)[3]),
            "the neighbour table and the province lists are separate objects");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "reading the borders does not change either fingerprint");

        var adjacency = TerritorialCoherence.AdjacencyBuilds;
        var shapes = BorderShape.ShapeBuilds;
        Check(first.SetProvinceController(2, 10), "the capture changes the controller");
        var occupiedPrint = CampaignFingerprint.Of(first);
        Check(TerritorialCoherence.RegionBorders(first, 1).TryGetValue(2, out still) && still == 1 &&
              TerritorialCoherence.TouchesOwnedGround(first, nationB, 1) &&
              BorderShape.Grievance(first, nationA, nationB) == grievance,
            "a capture leaves the edge and the grievance where they were");
        Check(TerritorialCoherence.AdjacencyBuilds == adjacency && BorderShape.ShapeBuilds == shapes,
            "a capture does not walk the provinces again");
        Check(ReferenceEquals(neighbours, TerritorialCoherence.NeighbourTables(first)) &&
              ReferenceEquals(regionList, BorderShape.ProvinceLists(first)[1]) &&
              CampaignFingerprint.Of(first) == occupiedPrint &&
              CampaignFingerprint.Of(second) == secondPrint,
            "asking again after the capture reuses the tables and writes nothing");

        first.States[1].NationMapIndex = 10;
        first.InvalidateStateIndex();
        Check(TerritorialCoherence.RegionBorders(first, 1).TryGetValue(2, out still) && still == 1 &&
              !TerritorialCoherence.TouchesOwnedGround(first, nationB, 1) &&
              BorderShape.Grievance(first, nationA, nationB) == 0f,
            "the refreshed tables follow the new owner");
        Check(TerritorialCoherence.AdjacencyBuilds == adjacency + 1 && BorderShape.ShapeBuilds == shapes + 1,
            "an ownership change walks the provinces again");
        Check(ReferenceEquals(neighbours, TerritorialCoherence.NeighbourTables(first)) &&
              ReferenceEquals(regionList, BorderShape.ProvinceLists(first)[1]) &&
              regionList.SequenceEqual(new[] { 1 }) &&
              TerritorialCoherence.NeighbourTables(second)[3].TryGetValue(4, out otherEdges) && otherEdges == 1 &&
              CampaignFingerprint.Of(second) == secondPrint,
            "the refresh reuses the first campaign's lists and leaves the second campaign alone");
    }

    static Campaign World(int seed, int stateA, int stateB, int ownerA, int ownerB)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = ownerA, Tag = "A" + ownerA, Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 2, MapIndex = ownerB, Tag = "B" + ownerB, Population = 1000 });
        campaign.States.Add(new ScenarioState { MapIndex = stateA, NationMapIndex = ownerA, Id = "s" + stateA, Name = "S" + stateA });
        campaign.States.Add(new ScenarioState { MapIndex = stateB, NationMapIndex = ownerB, Id = "s" + stateB, Name = "S" + stateB });
        var states = new ushort[] { 0, (ushort)stateA, (ushort)stateB };
        var owners = new ushort[] { 0, (ushort)ownerA, (ushort)ownerB };
        var neighbors = new int[states.Length][];
        neighbors[0] = Array.Empty<int>();
        neighbors[1] = new[] { 2 };
        neighbors[2] = new[] { 1 };
        var map = (ProvinceTopology)typeof(ProvinceTopology)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new ushort[1], states, (ushort[])owners.Clone(), new int[states.Length], neighbors, 1, 1 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])owners.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[owners.Length]);
        return campaign;
    }
}
