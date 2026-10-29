using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class AiFrontRecoveryTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("AI front recovery: " + label);
        Console.WriteLine("PASS AI front recovery: " + label);
    }

    public static void Run()
    {
        ReserveCanRecoverAnEmptyFront();
        ReachableDonorIsNotStarvedByNearbyPockets();
        ReachableReserveIsNotStarvedByNearbyPockets();
        ReopenedCorridorIsReconsidered();
        EmergencyRecoveryRespectsOrders();
        AdvancesAreBorrowedOnlyForThreatenedHoles();
        RecoveryKeepsItsRepostingBudget();
        ReserveFillsBeforeAnAdvanceIsPulled();
        UnorderedDivisionIsCalledUp();
    }

    static void ReserveCanRecoverAnEmptyFront()
    {
        var c = Fixture();
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        var reserve = Unit(c, 0, 0, 12, ArmyOrder.ReserveFront);
        Unit(c, 1, 1, 5, ArmyOrder.Manual, 4f);
        Check(c.ProvinceMap.FindRoute(12, 4, 500, c.ProvinceController).Length > 0,
            "a real friendly route connects the available reserve to the empty post");
        var random = c.Random.State;
        FrontManager.Balance(c, war);
        Check(reserve.OrderKind == ArmyOrder.DefendFront && reserve.OrderProvinceId == 4 && reserve.Route.Count > 0,
            "the staff fills an empty front from a ready reserve even with no existing defenders");
        Check(c.Random.State == random, "emergency reposting consumes no random numbers");
        var issued = reserve.OrderIssuedDay;
        var reposts = FrontManager.Reposts;
        c.Defines.HoursPerTick = 24;
        for (var day = 0; day < 20 && reserve.Route.Count > 0; day++)
        {
            MilitaryOperations.Tick(c);
            FrontManager.Balance(c, war);
        }
        Check(reserve.ProvinceId == 4 && reserve.Route.Count == 0,
            "the reserve actually marches to its recovered post through MilitaryOperations");
        Check(reserve.OrderIssuedDay == issued && FrontManager.Reposts == reposts,
            "reviews leave the recovering column's order and progress alone");
    }

    static void ReachableDonorIsNotStarvedByNearbyPockets()
    {
        var c = Fixture();
        var war = c.Wars[0];
        foreach (var province in new[] { 2, 4, 7 }) war.AttackerFront.Add(province);
        war.DefenderFront.Add(5);
        for (var i = 0; i < 4; i++) Unit(c, i, 0, 2, ArmyOrder.DefendFront);
        var reachable = Unit(c, 4, 0, 7, ArmyOrder.DefendFront);
        Unit(c, 5, 0, 7, ArmyOrder.DefendFront);
        Unit(c, 6, 1, 5, ArmyOrder.Manual, 4f);
        Check(c.ProvinceMap.FindRoute(2, 4, 500, c.ProvinceController).Length == 0 &&
            c.ProvinceMap.FindRoute(7, 4, 500, c.ProvinceController).Length > 0,
            "nearby defenders are in a pocket while a farther donor has a real land route");
        var target = c.ProvinceMap.CenterOf(4);
        var close = c.ProvinceMap.CenterOf(2);
        var far = c.ProvinceMap.CenterOf(7);
        Check(NavalOperations.Distance(close.Latitude, close.Longitude, target.Latitude, target.Longitude) <
            NavalOperations.Distance(far.Latitude, far.Longitude, target.Latitude, target.Longitude),
            "the unreachable pocket genuinely ranks ahead by straight-line distance");
        var random = c.Random.State;
        FrontManager.Balance(c, war);
        Check(reachable.OrderProvinceId == 4 && reachable.Route.Count > 0,
            "four unreachable nearer donors do not starve the reachable donor at the first review");
        for (var look = 0; look < 3; look++) FrontManager.Balance(c, war);
        Check(reachable.OrderProvinceId == 4 && reachable.Route.Count > 0,
            "further reviews retain the valid recovery order");
        Check(c.Armies.Where(a => a.NationId == 0 && a.ProvinceId == 2).All(a => a.Route.Count == 0),
            "the staff never invents a route through neutral or enemy ground");
        Check(c.Random.State == random, "pocket connectivity checks consume no random numbers");
    }

    static void ReachableReserveIsNotStarvedByNearbyPockets()
    {
        var c = Fixture();
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        for (var i = 0; i < 3; i++) Unit(c, i, 0, 2, ArmyOrder.ReserveFront);
        var reachable = Unit(c, 3, 0, 7, ArmyOrder.ReserveFront);
        Unit(c, 4, 1, 5, ArmyOrder.Manual, 4f);
        FrontManager.Balance(c, war);
        Check(reachable.OrderKind == ArmyOrder.DefendFront && reachable.OrderProvinceId == 4 && reachable.Route.Count > 0,
            "three unreachable nearer reserves cannot hide the reachable fourth reserve");
        Check(c.Armies.Where(a => a.NationId == 0 && a.ProvinceId == 2)
            .All(a => a.OrderKind == ArmyOrder.ReserveFront && a.Route.Count == 0),
            "a refused reserve keeps its original duty and does not get a fake march");
    }

    static void ReopenedCorridorIsReconsidered()
    {
        var c = Fixture();
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        var reserve = Unit(c, 0, 0, 2, ArmyOrder.ReserveFront);
        var before = Snapshot(reserve);
        FrontManager.Balance(c, war);
        Check(Snapshot(reserve) == before, "an isolated reserve remains intact when no route exists");
        c.ProvinceController[3] = 500;
        FrontManager.Balance(c, war);
        Check(reserve.OrderKind == ArmyOrder.DefendFront && reserve.OrderProvinceId == 4 &&
            reserve.Route.SequenceEqual(new[] { 3, 4 }),
            "a newly controlled corridor is reconsidered at the next review, with no stale component cache");
    }

    static void EmergencyRecoveryRespectsOrders()
    {
        var guards = new (string Label, Action<Campaign, Army> Block)[]
        {
            ("a player's nondelegated reserve", (c, a) => c.Nations[0].IsHuman = true),
            ("a direct order", (c, a) => a.DirectOrder = true),
            ("a formation already fighting", (c, a) => a.BattleId = 10),
            ("an embarked formation", (c, a) => a.EmbarkedFleetId = 10),
            ("a militia formation", (c, a) => a.TemplateId = MilitiaCore.TemplateId),
            ("a formation on a drawn line", (c, a) => a.FrontLineId = 10),
            ("a formation serving another war", (c, a) => a.AssignedWarId = 100),
            ("a newly posted reserve", (c, a) => a.OrderIssuedDay = c.ElapsedDays),
            ("a reserve already marching", (c, a) => { a.Route.Add(13); a.PlannedRoute.Add(13); a.MoveProgress = .4f; }),
            ("a spearhead", (c, a) => a.OrderKind = ArmyOrder.Spearhead),
            ("a manual field order", (c, a) => a.OrderKind = ArmyOrder.Manual)
        };
        foreach (var (label, block) in guards)
        {
            var c = Fixture();
            var war = c.Wars[0];
            war.AttackerFront.Add(4);
            war.DefenderFront.Add(5);
            var reserve = Unit(c, 0, 0, 12, ArmyOrder.ReserveFront);
            Unit(c, 1, 1, 5, ArmyOrder.Manual, 4f);
            block(c, reserve);
            var before = Snapshot(reserve);
            FrontManager.Balance(c, war);
            Check(Snapshot(reserve) == before, "emergency recovery leaves " + label + " untouched");
        }

        var delegated = Fixture();
        delegated.Nations[0].IsHuman = true;
        Delegation.Set(delegated.Nations[0], Delegation.Command, true);
        delegated.Wars[0].AttackerFront.Add(4);
        var free = Unit(delegated, 0, 0, 12, ArmyOrder.ReserveFront);
        FrontManager.Balance(delegated, delegated.Wars[0]);
        Check(free.OrderKind == ArmyOrder.DefendFront && free.OrderProvinceId == 4,
            "a player who delegated field command can recover an empty war front");

        var drawn = Fixture();
        drawn.Nations[0].IsHuman = true;
        Delegation.Set(drawn.Nations[0], Delegation.Command, true);
        var line = new DrawnFrontLine { Id = 10, NationId = 0, Kind = FrontLineKind.Defence };
        line.Provinces.Add(4);
        drawn.FrontLines.Add(line);
        var unassigned = Unit(drawn, 0, 0, 12, ArmyOrder.ReserveFront);
        var unassignedBefore = Snapshot(unassigned);
        FrontManager.Tick(drawn);
        Check(Snapshot(unassigned) == unassignedBefore,
            "even delegated field command does not steal an unassigned reserve for a drawn line");
    }

    static void AdvancesAreBorrowedOnlyForThreatenedHoles()
    {
        foreach (var enemyDivisions in new[] { 0f, 1f, 4f })
        {
            var c = Fixture();
            var war = c.Wars[0];
            war.AttackerFront.Add(4);
            war.DefenderFront.Add(5);
            var advance = Unit(c, 0, 0, 12, ArmyOrder.AdvanceFront);
            if (enemyDivisions > 0) Unit(c, 1, 1, 5, ArmyOrder.Manual, enemyDivisions);
            var before = Snapshot(advance);
            FrontManager.Balance(c, war);
            if (enemyDivisions < c.Defines.FrontHoleAdvanceNeed - 1)
                Check(Snapshot(advance) == before,
                    "an empty but insufficiently threatened post does not disperse an advance (enemy divisions " + enemyDivisions + ")");
            else
                Check(advance.OrderKind == ArmyOrder.DefendFront && advance.OrderProvinceId == 4,
                    "an empty post above the unchanged threat threshold may borrow one advance");
        }
        var isolated = Fixture();
        isolated.Wars[0].AttackerFront.Add(4);
        isolated.Wars[0].DefenderFront.Add(5);
        var failed = Unit(isolated, 0, 0, 2, ArmyOrder.AdvanceFront);
        Unit(isolated, 1, 1, 5, ArmyOrder.Manual, 4f);
        var failedBefore = Snapshot(failed);
        FrontManager.Balance(isolated, isolated.Wars[0]);
        Check(Snapshot(failed) == failedBefore, "a failed emergency advance loan restores its original order exactly");
    }

    static void RecoveryKeepsItsRepostingBudget()
    {
        var c = Fixture();
        var war = c.Wars[0];
        foreach (var p in new[] { 4, 7, 12, 14 }) war.AttackerFront.Add(p);
        for (var i = 0; i < 8; i++) Unit(c, i, 0, 15, ArmyOrder.ReserveFront);
        var budget = c.Defines.FrontRepostsPerLook + (int)(war.AttackerFront.Count * c.Defines.FrontRepostsPerPost);
        var before = FrontManager.Reposts;
        FrontManager.Balance(c, war);
        Check(FrontManager.Reposts - before == budget && c.Armies.Count(a => a.OrderKind == ArmyOrder.DefendFront) == budget,
            "recovering several empty posts still obeys the existing per-review reposting budget");
    }

    static void ReserveFillsBeforeAnAdvanceIsPulled()
    {
        var c = Fixture();
        Check(c.Nations[0].Id == 0 && c.Nations[0] == c.Nations[0], "nation id 0 stands at index 0");
        Check(!c.Defines.FrontStaffMovesAdvances, "advances are not spread along the front");
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        var advance = Unit(c, 0, 0, 7, ArmyOrder.AdvanceFront);
        var reserve = Unit(c, 1, 0, 12, ArmyOrder.ReserveFront);
        Unit(c, 2, 1, 5, ArmyOrder.Manual, 4f);
        var before = Snapshot(advance);
        FrontManager.Balance(c, war);
        Check(reserve.OrderKind == ArmyOrder.DefendFront && reserve.OrderProvinceId == 4,
            "an empty post receives a free reserve before anyone is pulled off an attack");
        Check(Snapshot(advance) == before, "the advance keeps its order");
    }

    static void UnorderedDivisionIsCalledUp()
    {
        var c = Fixture();
        var war = c.Wars[0];
        war.AttackerFront.Add(4);
        war.DefenderFront.Add(5);
        var idle = Unit(c, 0, 0, 12, ArmyOrder.Manual);
        idle.AssignedWarId = war.Id;
        idle.DirectOrder = false;
        var held = Unit(c, 1, 0, 7, ArmyOrder.Manual);
        held.AssignedWarId = war.Id;
        held.DirectOrder = true;
        FrontManager.Tick(c);
        Check(idle.OrderKind is ArmyOrder.DefendFront or ArmyOrder.AdvanceFront,
            "an unordered division on a war is given a front order");
        Check(held.OrderKind == ArmyOrder.Manual && held.DirectOrder,
            "a direct player order is not overwritten");
    }

    static string Snapshot(Army army) => JsonSerializer.Serialize(army,
        new JsonSerializerOptions { IncludeFields = true });

    static Army Unit(Campaign c, int id, int nation, int province, ArmyOrder duty, float divisions = 1f)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderProvinceId = province,
            AssignedWarId = c.Wars[0].Id, OrderKind = duty, Divisions = divisions,
            Latitude = centre.Latitude, Longitude = centre.Longitude, OrderIssuedDay = -100,
            Strength = 1, Organisation = 1, SupplyLevel = 1, DirectOrder = false, FrontLineId = -1 };
        c.Armies.Add(army);
        return army;
    }

    static Campaign Fixture()
    {
        // Actual raster geometry: an isolated friendly pocket below neutral country;
        // another friendly sector reaches the post along the northern land corridor.
        var owner = new ushort[] { 0, 501, 500, 502, 500, 501, 501, 500, 502,
            502, 502, 502, 500, 500, 500, 500, 502 };
        const int width = 128, height = 64;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var province = (y < 32 ? 8 : 0) + x / 16 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + province);
            controllers[index] = owner[province];
            rgb[index * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 16).ToDictionary(p => p, p => p));
        var c = new Campaign(20261014) { ScenarioId = "ai_front_recovery_fixture" };
        for (var nation = 0; nation < 3; nation++)
            c.Nations.Add(new Nation { Id = nation, MapIndex = 500 + nation,
                Tag = "AI" + nation, Name = "Country " + nation, Population = 10_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 16; p++)
            c.States.Add(new ScenarioState { Id = "ai_front_" + p, Name = "Region " + p,
                MapIndex = 3000 + p, NationMapIndex = owner[p], NationId = "AI" + (owner[p] - 500),
                Population = 1_000_000 });
        c.AttachProvinceTopology(map);
        c.Armies.Clear();
        c.Wars.Add(new StrategicWar { Id = 882, AttackerId = 0, DefenderId = 1 });
        return c;
    }
}
