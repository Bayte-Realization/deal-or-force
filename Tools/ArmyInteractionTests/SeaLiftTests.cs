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
        var withTheShip = true;
        var days = 0;
        for (; days < 150 && division.ProvinceId != belfast; days++)
        {
            c.TickDay();
            if (division.EmbarkedFleetId != fleet.Id) continue;
            aboard = true;
            withTheShip &= division.ProvinceId == 0 && Math.Abs(division.Latitude - fleet.Latitude) < 0.001f &&
                           Math.Abs(division.Longitude - fleet.Longitude) < 0.001f;
        }
        Check(aboard, "the division went aboard the fleet by itself");
        Check(withTheShip, "and while aboard it was where the ship was, on no land province");
        Check(division.ProvinceId == belfast, $"and stands in Belfast {days} days later, with no other order given");
        Check(division.EmbarkedFleetId < 0 && !fleet.Carried.Contains(division.Id), "ashore, not still aboard");
        Check(c.SeaLifts.All(o => o.Id != order.Id), "the lift is over and leaves the list");
        Check(!SeaLift.Holds(c, fleet), "and the fleet is free again");

        // Aboard a ship that is sinking: the share of the hulls that goes down takes the same
        // share of the men; a formation with nothing left is lost at sea.
        var swimmer = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = gbr.Id, Name = "Drowning test",
            ProvinceId = belfast, Divisions = 1, Organisation = 1, Strength = 1 };
        c.Armies.Add(swimmer);
        NavalInvasion.Embark(c, fleet, swimmer);
        NavalInvasion.DrownWithHulls(c, fleet, 0.5f);
        Check(Math.Abs(swimmer.Strength - 0.5f) < 0.001f && fleet.Carried.Contains(swimmer.Id),
            "half the hulls sunk drown half the men aboard");
        NavalInvasion.DrownWithHulls(c, fleet, 1f);
        Check(!fleet.Carried.Contains(swimmer.Id) && c.Armies.All(a => a.Id != swimmer.Id),
            "and a formation with nothing left is lost at sea");
        // Sending forces home (Demobilisation): men back to the reserve, nothing counted as lost.
        var home = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = gbr.Id, Name = "Stand-down test",
            ProvinceId = london, Divisions = 1, Organisation = 1, Strength = 0.8f, Personnel = 15_000 };
        c.Armies.Add(home);
        var reserve = gbr.ManpowerReserve; var lostBefore = gbr.FormationsLost;
        var foreign = new DisbandArmiesCommand { IssuerId = gbr.Id + 1000, ArmyIds = new[] { home.Id } };
        foreign.Apply(c);
        Check(c.Armies.Contains(home), "nobody else can send a country's division home");
        var disband = new DisbandArmiesCommand { IssuerId = gbr.Id, ArmyIds = new[] { home.Id } };
        disband.Apply(c);
        Check(disband.StoodDown == 1 && !c.Armies.Contains(home), "a division is stood down by its own country's order");
        Check(Math.Abs(gbr.ManpowerReserve - reserve - 15_000 * 0.8f * c.Defines.ArmyStandDownReturn) < 1f && gbr.FormationsLost == lostBefore,
            "its men go back to the reserve and it is not counted as lost");
        var carrier = c.Fleets.First(f => f.NationId == gbr.Id && f.Hulls > 0);
        var passenger = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = gbr.Id, Name = "Passenger", ProvinceId = london,
            Divisions = 1, Organisation = 1, Strength = 1 };
        c.Armies.Add(passenger);
        NavalInvasion.Embark(c, carrier, passenger);
        var blocked = new DecommissionFleetCommand { IssuerId = gbr.Id, FleetId = carrier.Id };
        blocked.Apply(c);
        Check(blocked.Refusal.Length > 0 && c.Fleets.Contains(carrier), "a fleet with troops aboard is not decommissioned");
        new DisbandArmiesCommand { IssuerId = gbr.Id, ArmyIds = new[] { passenger.Id } }.Apply(c);
        Check(c.Armies.Contains(passenger), "nor is a division aboard a ship stood down");
        NavalInvasion.Disembark(c, carrier, london);
        var treasury = gbr.Treasury; var hulls = carrier.Hulls;
        var decommission = new DecommissionFleetCommand { IssuerId = gbr.Id, FleetId = carrier.Id };
        decommission.Apply(c);
        Check(decommission.Refusal.Length == 0 && !c.Fleets.Contains(carrier) &&
              Math.Abs(gbr.Treasury - treasury - hulls * c.Defines.HullCost * c.Defines.FleetDecommissionRefund) < 0.001f,
            "an empty fleet is decommissioned, a share of its price back to the treasury");
        Console.WriteLine("sea lift: all passed");
    }
}
