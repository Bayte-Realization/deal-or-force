using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The same advance order over the same holders is the same corridor, and it is not searched again.
/// A capture on that corridor searches again. A hand order is left as the player gave it.
/// </summary>
static class AdvanceRouteCacheTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("advance route cache: " + label);
        Console.WriteLine("PASS advance route cache: " + label);
    }

    public static void Run()
    {
        var first = World(61111, out var controllers);
        var second = World(61112, out _);
        Check(first.Nations[0].Id == 0 && first.NationById(0) == first.Nations[0] &&
              first.NationById(1) == first.Nations[1],
            "nation id is the list index");
        Check(first.Defines.SupplyCutoffDays == 14 && second.Defines.SupplyCutoffDays == 14,
            "the cutoff span is unchanged");

        var army = first.Armies[0];
        var war = first.Wars[0];
        Warfare.ResetAdvanceRouteSearches();
        Check(Warfare.PrepareAdvanceCorridor(first, war, army), "the first search finds a corridor");
        var path = army.PlannedRoute.ToArray();
        Check(path.Length >= 2 && Warfare.AdvanceRouteSearches == 1,
            "the first corridor is more than one step and was searched once: " + string.Join(",", path));

        army.PlannedRoute.Clear();
        Check(Warfare.PrepareAdvanceCorridor(first, war, army) && army.PlannedRoute.SequenceEqual(path) &&
              Warfare.AdvanceRouteSearches == 1,
            "the same order and the same holders return the same corridor and do not walk again");
        Check(Warfare.AdvanceRouteReuses == 1 && Warfare.AdvanceRouteSearches == 1,
            "a second ask with the same order and holders reuses and does not walk");

        var other = second.Armies[0];
        Check(Warfare.PrepareAdvanceCorridor(second, second.Wars[0], other), "the other campaign searches its own corridor");
        Check(Warfare.AdvanceRouteSearches == 2, "the other campaign's search is its own walk");
        army.PlannedRoute.Clear();
        Check(Warfare.PrepareAdvanceCorridor(first, war, army) && army.PlannedRoute.SequenceEqual(path) &&
              Warfare.AdvanceRouteSearches == 2,
            "the other campaign does not drop the first campaign's corridor");

        var hand = first.Armies[1];
        var handRoute = hand.PlannedRoute.ToArray();
        var handOrder = hand.OrderProvinceId;
        var walks = Warfare.AdvanceRouteSearches;
        Check(Warfare.PrepareAdvanceCorridor(first, war, hand) && hand.DirectOrder &&
              hand.OrderKind == ArmyOrder.AdvanceFront && hand.OrderProvinceId == handOrder &&
              hand.PlannedRoute.SequenceEqual(handRoute) && Warfare.AdvanceRouteSearches == walks,
            "a direct player order is not rewritten");

        var captured = path[1];
        Check(captured != path[0], "the capture is further along the corridor than the next step");
        controllers[captured] = 12;
        Check(Warfare.PrepareAdvanceCorridor(first, war, army) && Warfare.AdvanceRouteSearches == walks + 1 &&
              !army.PlannedRoute.SequenceEqual(path),
            "a controller change on the corridor walks again: " + string.Join(",", army.PlannedRoute));
        Check(first.Defines.SupplyCutoffDays == 14, "the search does not change the cutoff span");
    }

    static Campaign World(int seed, out ushort[] controllers)
    {
        const int width = 6, height = 1;
        var states = new ushort[width];
        var owners = new ushort[width];
        var rgb = new byte[width * 3];
        for (var x = 0; x < width; x++)
        {
            var province = x + 1;
            states[x] = (ushort)(1000 + province);
            owners[x] = (ushort)(province <= 2 || province == 6 ? 10 : 11);
            rgb[x * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, width).ToDictionary(p => p, p => p));
        controllers = (ushort[])map.ProvinceToNation.Clone();
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ATK", Name = "Attacker", Population = 2_000_000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "DEF", Name = "Defender", Population = 2_000_000 });
        campaign.Nations.Add(new Nation { Id = 2, MapIndex = 12, Tag = "NEU", Name = "Neutral", Population = 1_000_000 });
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, controllers);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        campaign.Wars.Add(new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 });
        campaign.Armies.Add(new Army
        {
            Id = 1, NationId = 0, ProvinceId = 1, OrderProvinceId = 4, AssignedWarId = 1,
            OrderKind = ArmyOrder.AdvanceFront, PlanActive = true, DirectOrder = false, Divisions = 1f
        });
        campaign.Armies.Add(new Army
        {
            Id = 2, NationId = 0, ProvinceId = 1, OrderProvinceId = 4, AssignedWarId = 1,
            OrderKind = ArmyOrder.AdvanceFront, PlanActive = true, DirectOrder = true, Divisions = 1f
        });
        campaign.Armies[1].PlannedRoute.Add(2);
        return campaign;
    }
}
