using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The morning way home is searched on the campaign's own buffers. A second campaign
/// does not clear them, and a cut-off formation still gets the same road.
/// </summary>
static class PocketScratchTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("pocket scratch: " + label);
        Console.WriteLine("PASS pocket scratch: " + label);
    }

    public static void Run()
    {
        var first = World(61113, enemyOn: new[] { 1, 2, 4, 5, 6 }, home: 3, stand: 1);
        var second = World(61114, enemyOn: new[] { 2, 3, 5, 6 }, home: 1, stand: 2);
        Check(first.Nations[0].Id == 0 && first.NationById(0) == first.Nations[0] &&
              second.NationById(1) == second.Nations[1],
            "nation id is the list index");
        Check(first.Defines.SupplyCutoffDays == 14 && second.Defines.SupplyCutoffDays == 14,
            "the cutoff span is unchanged");

        var nation = first.Nations[0];
        var once = PocketBreakout.WayHome(first, nation, 1, first.Wars[0]);
        var again = PocketBreakout.WayHome(first, nation, 1, first.Wars[0]);
        Check(once is { Count: >= 2 } && again is not null && once.SequenceEqual(again) && once[0] == 1 &&
              first.ProvinceController[once[^1]] == 10,
            "the same pocket finds the same way home: " + (once is null ? "none" : string.Join(",", once)));

        var came = PocketBreakout.SearchCameFrom(first);
        var depth = PocketBreakout.SearchDepth(first);
        var neighbours = PocketBreakout.SearchNeighbours(first);
        var frontier = PocketBreakout.SearchFrontier(first);
        Check(came is not null && depth is not null && neighbours is not null && frontier is not null &&
              came.TryGetValue(once[1], out var previous) && previous == once[0],
            "the first campaign keeps the step it just searched");

        var other = PocketBreakout.WayHome(second, second.Nations[0], 2, second.Wars[0]);
        Check(other is { Count: >= 2 } && other[0] == 2, "the second campaign finds its own way home");
        Check(!ReferenceEquals(came, PocketBreakout.SearchCameFrom(second)) &&
              !ReferenceEquals(depth, PocketBreakout.SearchDepth(second)) &&
              !ReferenceEquals(neighbours, PocketBreakout.SearchNeighbours(second)) &&
              !ReferenceEquals(frontier, PocketBreakout.SearchFrontier(second)) &&
              ReferenceEquals(came, PocketBreakout.SearchCameFrom(first)) &&
              came.TryGetValue(once[1], out var still) && still == once[0],
            "two campaigns do not share or clear the scratch");

        var army = first.Armies[0];
        var divisions = army.Divisions;
        var days = army.DaysCutOff;
        PocketBreakout.Tick(first);
        Check(first.Armies.Contains(army) && army.Divisions == divisions && army.DaysCutOff == days &&
              army.ReliefDistance == once.Count - 1 && first.Defines.SupplyCutoffDays == 14,
            "a cut-off army still finds that way home and is not disbanded");

        var after = PocketBreakout.SearchCameFrom(first);
        var step = after[once[1]];
        PocketBreakout.Tick(second);
        Check(ReferenceEquals(after, PocketBreakout.SearchCameFrom(first)) &&
              !ReferenceEquals(after, PocketBreakout.SearchCameFrom(second)) &&
              after.TryGetValue(once[1], out var kept) && kept == step &&
              second.Armies.Count == 1 && second.Defines.SupplyCutoffDays == 14,
            "the second morning does not clear the first campaign's way home");
    }

    static Campaign World(int seed, int[] enemyOn, int home, int stand)
    {
        const int width = 6, height = 1;
        var states = new ushort[width];
        var owners = new ushort[width];
        var rgb = new byte[width * 3];
        var enemy = new HashSet<int>(enemyOn);
        for (var x = 0; x < width; x++)
        {
            var province = x + 1;
            states[x] = (ushort)(1000 + province);
            owners[x] = (ushort)(province == home || !enemy.Contains(province) ? 10 : 11);
            rgb[x * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, width).ToDictionary(p => p, p => p));
        var controllers = (ushort[])map.ProvinceToNation.Clone();
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "HOM", Name = "Home", Population = 2_000_000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "CAP", Name = "Captor", Population = 2_000_000 });
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, controllers);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        campaign.Wars.Add(new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 });
        campaign.Armies.Add(new Army
        {
            Id = 1, NationId = 0, ProvinceId = stand, AssignedWarId = 1, CutOff = true,
            Divisions = 2f, Strength = 1f, OrderKind = ArmyOrder.DefendFront
        });
        return campaign;
    }
}
