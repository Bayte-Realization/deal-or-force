using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// One order across the sea (SeaLift): a British division in London sent to Belfast, which no
/// march reaches, walks to a port of its own country, boards a British fleet when it comes, is
/// carried over and put ashore, and arrives - with nobody pressing another button.
/// </summary>
static class SeaLiftTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(Campaign c)
    {
        var map = c.ProvinceMap;
        var gbr = c.NationByTag("GBR");
        var london = CityCatalog.ProvinceUnder(map, 51.507f, -0.128f);
        var belfast = CityCatalog.ProvinceUnder(map, 54.597f, -5.930f);
        Check(london > 0 && belfast > 0 && c.ProvinceController[london] == gbr.MapIndex && c.ProvinceController[belfast] == gbr.MapIndex,
            "London and Belfast are British ground on the real map");
        Check(c.Fleets.Any(f => f.NationId == gbr.Id && f.Hulls > 0), "Britain has a navy to carry them");

        var division = new Army
        {
            Id = c.Armies.Count == 0 ? 1 : c.Armies.Max(a => a.Id) + 1, NationId = gbr.Id, Name = "Sea lift test",
            ProvinceId = london, StateMapIndex = map.ProvinceToState[london], Divisions = 1, Organisation = 1, Strength = 1
        };
        (division.Latitude, division.Longitude) = map.CenterOf(london);
        c.Armies.Add(division);

        var walk = new MoveArmyCommand { IssuerId = gbr.Id, ArmyId = division.Id, TargetProvinceId = belfast };
        walk.Apply(c);
        Check(walk.Result == ArmyMoveResult.NoLandRoute, $"no march reaches Belfast from London ({walk.Result})");

        var refusal = new SeaLiftCommand { IssuerId = gbr.Id + 1000, ArmyIds = new[] { division.Id }, TargetProvinceId = belfast };
        refusal.Apply(c);
        Check(refusal.Order is null && c.SeaLifts.Count == 0, "nobody else can send a country's division by sea");

        var lift = new SeaLiftCommand { IssuerId = gbr.Id, ArmyIds = new[] { division.Id }, TargetProvinceId = belfast };
        lift.Apply(c);
        Check(lift.Order is not null, "the sea lift is ordered: " + lift.Refusal);
        var order = lift.Order;
        Check(c.ProvinceController[order.PortProvinceId] == gbr.MapIndex && SeaLanes.HarbourOf(map, order.PortProvinceId) >= 0,
            "it boards at a British port");
        Check(order.LandingProvinceId == belfast || SeaLanes.HarbourOf(map, order.LandingProvinceId) >= 0,
            "and lands on a coast (Belfast's own)");
        var fleet = c.Fleets.Single(f => f.Id == order.FleetId);
        Check(fleet.NationId == gbr.Id && NavalInvasion.Capacity(c, fleet) >= 1f, "a British fleet with room for it carries it");

        c.IsPaused = false;
        var aboard = false;
        var days = 0;
        for (; days < 150 && division.ProvinceId != belfast; days++)
        {
            c.TickDay();
            aboard |= division.EmbarkedFleetId == fleet.Id;
        }
        Check(aboard, "the division went aboard the fleet by itself");
        Check(division.ProvinceId == belfast, $"and stands in Belfast {days} days later, with no other order given");
        Check(division.EmbarkedFleetId < 0 && !fleet.Carried.Contains(division.Id), "ashore, not still aboard");
        Check(c.SeaLifts.All(o => o.Id != order.Id), "the lift is over and leaves the list");
        Check(!SeaLift.Holds(c, fleet), "and the fleet is free again");
        Console.WriteLine("sea lift: all passed");
    }
}
