using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The navy (item 68): courses that keep to the sea, orders that refuse what cannot be
/// reached, ports a fleet actually lies in, landings only on coasts, a gangway that does not
/// teleport anybody home, ships that sink, yards that build, fights that last as many days
/// at any clock, and all of it surviving a save.
/// </summary>
static class NavalTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260928);
        c.AttachProvinceTopology(map);
        var usa = c.NationByTag("USA");
        var gbr = c.NationByTag("GBR");
        var fra = c.NationByTag("FRA");
        c.ConfigureSinglePlayer(gbr.Id);
        var atlantic = c.Fleets.First(fleet => fleet.NationId == usa.Id);
        var home = c.Fleets.First(fleet => fleet.NationId == gbr.Id);

        // --- A: where a fleet lives ----------------------------------------------------------
        Check(c.Fleets.All(fleet => SeaLanes.HarbourOf(map, fleet.HomeProvinceId) >= 0),
            "every fleet of 2024 has a home port with a harbour");
        Check(c.Fleets.All(fleet => fleet.InPort), "and every one of them opens the campaign lying in it");

        // --- B: a course round the land, and one that cannot be sailed -------------------------
        var west = new SetFleetOrderCommand
        {
            IssuerId = usa.Id, FleetId = atlantic.Id, Mission = FleetMission.Patrol,
            HasDestination = true, Latitude = 32.7f, Longitude = -117.25f
        };
        west.Apply(c);
        Check(west.Refusal.Length == 0 && atlantic.HasOrder && atlantic.Waypoints.Count >= 4,
            $"Norfolk to San Diego is a course of {atlantic.Waypoints.Count / 2} legs, not one line over Texas");
        var length = NavalOperations.CourseLength(atlantic);
        Check(length > NavalOperations.Distance(atlantic.Latitude, atlantic.Longitude, 32.7f, -117.25f) * 1.5f && length < 110f,
            $"and it is {length:0} degrees of sea: through Panama, not round the Horn");
        var caspian = new SetFleetOrderCommand
        {
            IssuerId = gbr.Id, FleetId = home.Id, Mission = FleetMission.Patrol,
            HasDestination = true, Latitude = 42f, Longitude = 50.5f
        };
        var wasMission = home.Mission;
        caspian.Apply(c);
        Check(caspian.Refusal.Length > 0 && !home.HasOrder && home.Mission == wasMission,
            $"a fleet ordered to the Caspian is refused and left as it was ({caspian.Refusal})");

        // --- C: a port is where a fleet repairs ----------------------------------------------
        home.Strength = 0.5f;
        home.Supply = 0.4f;
        for (var step = 0; step < 24; step++) NavalOperations.Tick(c);
        var repaired = home.Strength;
        Check(repaired > 0.5f && home.Supply > 0.4f, $"in port the Home Fleet repairs ({repaired:P1}) and refills");
        var (portLatitude, portLongitude) = (home.Latitude, home.Longitude);
        home.Latitude = 45f;
        home.Longitude = -30f;
        home.BerthProvinceId = -1;
        home.Strength = 0.5f;
        var supply = home.Supply;
        for (var step = 0; step < 24; step++) NavalOperations.Tick(c);
        Check(home.Strength <= 0.5f + 0.0001f && home.Supply < supply,
            "anchored in mid-Atlantic it repairs nothing and burns its stores");

        // --- D: a gangway, not a teleport -------------------------------------------------------
        home.Latitude = portLatitude;
        home.Longitude = portLongitude;
        home.Strength = 1f;
        home.BerthProvinceId = NavalOperations.FindBerth(c, home);
        var division = c.Armies.First(army => army.NationId == gbr.Id && army.EmbarkedFleetId < 0 && !army.InBattle);
        var inland = Enumerable.Range(1, map.ProvinceCount).First(province =>
            c.ProvinceController[province] == (ushort)gbr.MapIndex && SeaLanes.HarbourOf(map, province) < 0);
        division.ProvinceId = inland;
        Check(!NavalInvasion.CanEmbark(c, home, division, out var notACoast),
            $"a division standing inland cannot walk up the gangway ({notACoast})");
        division.ProvinceId = home.HomeProvinceId;
        new EmbarkArmiesCommand { IssuerId = gbr.Id, FleetId = home.Id, ArmyIds = new[] { division.Id } }.Apply(c);
        Check(home.Carried.Contains(division.Id), "a division at the quay goes aboard");
        for (var step = 0; step < 48; step++) NavalOperations.Tick(c);
        Check(home.Carried.Contains(division.Id), "and stays aboard in port instead of being put straight back ashore");

        home.Latitude = 45f;
        home.Longitude = -30f;
        home.BerthProvinceId = -1;
        Check(NavalInvasion.PutAshore(c, home, out var noShore) == 0 && home.Carried.Contains(division.Id),
            $"'put ashore' in mid-Atlantic lands nobody in Plymouth ({noShore})");
        home.Latitude = portLatitude;
        home.Longitude = portLongitude;
        Check(NavalInvasion.PutAshore(c, home, out _) == 1 && division.ProvinceId > 0 &&
              NavalOperations.HolderOf(c, division.ProvinceId)?.Id == gbr.Id,
            "off its own coast it lands them on its own coast");

        // --- E: a landing is made on a coast ------------------------------------------------------
        new EmbarkArmiesCommand { IssuerId = gbr.Id, FleetId = home.Id, ArmyIds = new[] { division.Id } }.Apply(c);
        var french = Enumerable.Range(1, map.ProvinceCount).First(province =>
            c.ProvinceController[province] == (ushort)fra.MapIndex && SeaLanes.HarbourOf(map, province) < 0);
        var refused = new LaunchInvasionCommand { IssuerId = gbr.Id, FleetId = home.Id, ProvinceId = french };
        refused.Apply(c);
        Check(refused.Refusal.Length > 0 && home.Mission != FleetMission.Invasion,
            $"a landing in the middle of France is refused ({refused.Refusal})");
        NavalInvasion.PutAshore(c, home, out _);

        // --- F: a fight lasts as many days at any clock ---------------------------------------------
        var war = new StrategicWar
        {
            Id = 95_000, Name = "Test naval war", AttackerId = gbr.Id, DefenderId = fra.Id,
            StartedDay = c.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
        };
        c.Wars.Add(war);
        var enemy = c.Fleets.First(fleet => fleet.NationId == fra.Id);
        // Two equal fleets, so the pressure each is under stays the same through the day and a
        // day in one step and a day in twenty-four have to agree. (Between unequal ones the
        // daily step overstates the stronger side's advantage, which is the step's fault, not
        // the fight's.)
        var enemyHulls = enemy.Hulls;
        var homeHulls = home.Hulls;
        float OneDay(int hours)
        {
            c.Defines.HoursPerTick = hours;
            foreach (var fleet in new[] { home, enemy })
            {
                fleet.Latitude = 48f;
                fleet.Longitude = -8f;
                fleet.Mission = FleetMission.Patrol;
                fleet.HasOrder = false;
                fleet.Organisation = fleet.Strength = fleet.Supply = fleet.Readiness = 1f;
                fleet.Experience = 0f;
                fleet.Hulls = 60;
                fleet.ReadyDay = 0;
            }
            for (var step = 0; step < 24 / hours; step++) NavalOperations.Tick(c);
            return 1f - home.Organisation;
        }
        var daily = OneDay(24);
        var hourly = OneDay(1);
        c.Defines.HoursPerTick = 1;
        enemy.Hulls = enemyHulls;
        home.Hulls = homeHulls;
        // The daily step recovers a day's cohesion on a fleet already at full and loses it to the
        // cap; the hourly one keeps most of it. That, and nothing else, is the gap allowed -
        // before each step took its share, the hourly fight cost twenty-four days in one.
        Check(daily > 0f && MathF.Abs(hourly - daily) <= c.Defines.FleetOrganisationAtSeaPerDay + 0.005f,
            $"a day of action costs {daily:P1} of cohesion at a daily clock and {hourly:P1} at an hourly one");

        // --- G: the one that breaks loses ships -------------------------------------------------------
        home.Organisation = 0.05f;
        home.Strength = 0.6f;
        var hulls = home.Hulls;
        NavalOperations.Tick(c);
        Check(home.Hulls < hulls && home.HullsLost == hulls - home.Hulls && home.HasOrder && home.Mission == FleetMission.Anchored,
            $"a fleet that breaks at sixty per cent strength loses {hulls - home.Hulls} hulls and turns for home");
        c.Wars.Remove(war);

        // --- H: the yards ------------------------------------------------------------------------------
        var order = new OrderHullsCommand { IssuerId = gbr.Id, FleetId = home.Id, Count = 5 };
        order.Apply(c);
        Check(order.Refusal.Length == 0 && home.HullsOrdered == 5, "a player lays down five hulls");
        var treasury = gbr.Treasury = MathF.Max(gbr.Treasury, 10_000f);
        var before = home.Hulls;
        for (var day = 0; day < 400 && home.Hulls == before; day++) Shipbuilding.Tick(c);
        Check(home.Hulls == before + 1 && gbr.Treasury < treasury,
            $"the first joins the fleet, and the treasury paid {Fmt.Money(treasury - gbr.Treasury)} for the work so far");
        new OrderHullsCommand { IssuerId = gbr.Id, FleetId = home.Id, Count = -home.HullsOrdered }.Apply(c);
        Check(home.HullsOrdered == 0, "and what is left can be cancelled");
        var foreign = new OrderHullsCommand { IssuerId = fra.Id, FleetId = home.Id, Count = 5 };
        foreign.Apply(c);
        Check(foreign.Refusal.Length > 0 && home.HullsOrdered == 0, "nobody lays hulls down for somebody else's fleet");

        // --- H2: a navy handed to the admiralty ------------------------------------------------------
        var ireland = c.NationByTag("IRL");
        if (ireland is not null)
        {
            var feud = new StrategicWar
            {
                Id = 95_001, Name = "Test admiralty war", AttackerId = gbr.Id, DefenderId = ireland.Id,
                StartedDay = c.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
            };
            c.Wars.Add(feud);
            var interval = c.Defines.AiFleetDecisionInterval;
            c.Defines.AiFleetDecisionInterval = 1;
            home.Latitude = portLatitude;
            home.Longitude = portLongitude;
            home.Mission = FleetMission.Anchored;
            home.HasOrder = false;
            home.Supply = home.Organisation = home.Strength = 1f;
            home.BerthProvinceId = NavalOperations.FindBerth(c, home);
            AiAdmiralty.Tick(c);
            Check(!home.HasOrder && home.Mission == FleetMission.Anchored, "a player's navy is left alone by the admiralty");
            new SetDelegationCommand { IssuerId = gbr.Id, Portfolio = Delegation.Navy, Delegated = true }.Apply(c);
            AiAdmiralty.Tick(c);
            Check(home.HasOrder && home.Mission is FleetMission.Blockade or FleetMission.Raid,
                $"handed to the admiralty, the Home Fleet sails against Ireland on {home.Mission}");
            new SetDelegationCommand { IssuerId = gbr.Id, Portfolio = Delegation.Navy, Delegated = false }.Apply(c);
            c.Defines.AiFleetDecisionInterval = interval;
            c.Wars.Remove(feud);
        }

        // --- I: through a save ----------------------------------------------------------------------------
        new SetFleetOrderCommand
        {
            IssuerId = gbr.Id, FleetId = home.Id, Mission = FleetMission.Patrol,
            HasDestination = true, Latitude = 36f, Longitude = -20f
        }.Apply(c);
        home.HullsOrdered = 3;
        var bytes = SaveSystem.ExportNetworkSnapshot(c, "");
        var back = SaveSystem.ImportNetworkSnapshot(bytes, "").Campaign;
        var restored = back?.Fleets.Find(fleet => fleet.Id == home.Id);
        Check(restored is not null && restored.Waypoints.SequenceEqual(home.Waypoints) && restored.Routed &&
              restored.HullsOrdered == 3 && restored.HullsLost == home.HullsLost && restored.Establishment == home.Establishment,
            "a course, a yard order and a fleet's losses all survive a save");
    }
}
