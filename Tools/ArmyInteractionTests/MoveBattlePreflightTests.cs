using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Invalid movement must not withdraw a formation before discovering the refusal.</summary>
public static class MoveBattlePreflightTests
{
    private static readonly JsonSerializerOptions Json = new()
    { IncludeFields = true, IgnoreReadOnlyProperties = true };

    public static void Run()
    {
        InvalidTargetsKeepTheEntireBattle();
        FriendlyRetreatAndFurtherMarchStillWork();
        ChangingAnAttackTargetStillWorks();
        EncircledDefenderCanQueueAValidBreakout();
        AppendedOrdersDoNotDisengage();
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL movement battle preflight: " + label);
        Console.WriteLine("PASS movement battle preflight: " + label);
    }

    private static string Snapshot(Campaign c) => JsonSerializer.Serialize(new
    {
        c.Armies, c.Battles, c.FrontLines, c.ProvinceController, c.Nations,
        c.InterfaceDirty, Random = c.Random.State
    }, Json);

    private static void InvalidTargetsKeepTheEntireBattle()
    {
        foreach (var attacking in new[] { true, false })
        foreach (var kind in new[] { "neutral", "disconnected friendly", "disconnected enemy", "invalid id" })
        {
            var c = Fixture();
            var army = c.Armies.Single(a => a.NationId == (attacking ? 0 : 1));
            var target = kind == "neutral" ? 5 : kind == "invalid id" ? 0 : 6;
            if (kind.StartsWith("disconnected", StringComparison.Ordinal))
                c.ProvinceController[target] = (ushort)(kind.EndsWith("friendly", StringComparison.Ordinal)
                    ? 500 + army.NationId : 500 + (1 - army.NationId));
            var before = Snapshot(c);
            var order = new MoveArmyCommand { IssuerId = army.NationId, ArmyId = army.Id, TargetProvinceId = target };
            order.Apply(c);
            var expected = kind == "neutral" ? ArmyMoveResult.TargetNotControlled
                : kind == "invalid id" ? ArmyMoveResult.InvalidTarget : ArmyMoveResult.NoLandRoute;
            Check(order.Result == expected, $"{(attacking ? "attacker" : "defender")} reports {kind} destination");
            Check(Snapshot(c) == before,
                $"{(attacking ? "attacker" : "defender")} refusal preserves units, orders, rosters, losses, control and RNG ({kind})");
        }
    }

    private static void FriendlyRetreatAndFurtherMarchStillWork()
    {
        var c = Fixture();
        var defender = c.Armies.Single(a => a.NationId == 1);
        var retreat = new MoveArmyCommand { IssuerId = 1, ArmyId = defender.Id, TargetProvinceId = 3 };
        retreat.Apply(c);
        Check(retreat.Result == ArmyMoveResult.WithdrawnAndMoving && !defender.InBattle && defender.ProvinceId == 3,
            "a valid adjacent friendly withdrawal remains immediate");
        Check(c.Battles.Single().Ended && c.ProvinceController[2] == 500 &&
            defender.ReadyDay >= c.ElapsedDays + c.Defines.RestAfterRetreatDays,
            "last defender still yields ground and keeps the recovery interval");

        c = Fixture(); defender = c.Armies.Single(a => a.NationId == 1);
        var farther = new MoveArmyCommand { IssuerId = 1, ArmyId = defender.Id, TargetProvinceId = 4 };
        farther.Apply(c);
        Check(farther.Result == ArmyMoveResult.WithdrawnAndMoving && defender.ProvinceId == 3 &&
            defender.Route.SequenceEqual(new[] { 4 }) && defender.PlannedRoute.SequenceEqual(new[] { 4 }) &&
            defender.DirectOrder && defender.OrderProvinceId == 4,
            "a further friendly destination is routed from the actual retreat province");
    }

    private static void ChangingAnAttackTargetStillWorks()
    {
        var c = Fixture();
        var attacker = c.Armies.Single(a => a.NationId == 0);
        c.ProvinceController[7] = 501;
        var attack = new MoveArmyCommand { IssuerId = 0, ArmyId = attacker.Id, TargetProvinceId = 7 };
        attack.Apply(c);
        Check(attack.Result == ArmyMoveResult.WithdrawnAndMoving && !attacker.InBattle &&
            attacker.DirectOrder && attacker.OrderKind == ArmyOrder.Spearhead &&
            attacker.PlannedRoute.SequenceEqual(new[] { 2, 3, 7 }),
            "an attacker can stop its current assault and order a different reachable enemy objective");

        c = Fixture();
        var defender = c.Armies.Single(a => a.NationId == 1);
        var counter = new MoveArmyCommand { IssuerId = 1, ArmyId = defender.Id, TargetProvinceId = 7 };
        counter.Apply(c);
        Check(counter.Result == ArmyMoveResult.WithdrawnAndMoving && !defender.InBattle &&
            defender.ProvinceId == 3 && defender.DirectOrder && defender.OrderKind == ArmyOrder.Spearhead &&
            defender.PlannedRoute.SequenceEqual(new[] { 7 }),
            "a defender can retreat first and keep a valid enemy retarget order");
        Check(AssaultReadiness.Assess(c, defender).Reason == AssaultReadiness.Reason.Recovering,
            "retargeting under a direct order does not bypass retreat recovery");
    }

    private static void EncircledDefenderCanQueueAValidBreakout()
    {
        var c = Fixture();
        var defender = c.Armies.Single(a => a.NationId == 1);
        foreach (var neighbor in c.ProvinceMap.Neighbors[defender.ProvinceId]) c.ProvinceController[neighbor] = 500;
        var battle = c.Battles.Single();
        var order = new MoveArmyCommand { IssuerId = 1, ArmyId = defender.Id, TargetProvinceId = 7 };
        order.Apply(c);
        Check(order.Result == ArmyMoveResult.QueuedAfterBattle && defender.InBattle && defender.ProvinceId == 2 &&
            battle.Defenders.Contains(defender.Id) && !battle.Ended,
            "an encircled defender cannot cancel combat but may queue a legal breakout objective");
        Check(defender.DirectOrder && defender.PlannedRoute.SequenceEqual(new[] { 3, 7 }) &&
            battle.AttackerLosses == 123 && battle.DefenderLosses == 456,
            "queued breakout keeps the existing battle losses and its explicit corridor");
    }

    private static void AppendedOrdersDoNotDisengage()
    {
        var c = Fixture();
        var attacker = c.Armies.Single(a => a.NationId == 0);
        attacker.PlannedRoute.Clear(); attacker.PlannedRoute.Add(2);
        c.ProvinceController[7] = 501;
        var command = new MoveArmyCommand { IssuerId = 0, ArmyId = attacker.Id, TargetProvinceId = 7, AppendWaypoint = true };
        command.Apply(c);
        Check(command.Result == ArmyMoveResult.QueuedAfterBattle && attacker.InBattle &&
            attacker.PlannedRoute.SequenceEqual(new[] { 2, 3, 7 }) && !c.Battles.Single().Ended,
            "Shift-queued waypoints extend the attack without withdrawing the formation");
    }

    private static Campaign Fixture()
    {
        const int width = 96, height = 96;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        var ownership = new ushort[] { 0, 500, 501, 501, 501, 502, 501, 500, 501, 503 };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = y / 32 * 3 + x / 32 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + p);
            controllers[index] = ownership[p];
            rgb[index * 3 + 2] = (byte)p;
        }
        var topology = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 9).ToDictionary(p => p, p => p));
        // Explicit symmetric scenario graph keeps this route contract independent of raster
        // seam shortcuts: 6 is reached only through neutral 5; 7 is a reachable retarget.
        var links = new[] { Array.Empty<int>(), new[] { 2 }, new[] { 1, 3, 8, 9 },
            new[] { 2, 4, 5, 7 }, new[] { 3 }, new[] { 3, 6 }, new[] { 5 },
            new[] { 3 }, new[] { 2 }, new[] { 2 } };
        for (var p = 1; p <= 9; p++) topology.Neighbors[p] = links[p];
        var c = new Campaign(20261020) { ScenarioId = "move_battle_preflight" };
        c.Defines.HoursPerTick = 24;
        for (var id = 0; id < 4; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id, Tag = "MP" + id,
                Name = "Preflight country " + id, Population = 1_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 9; p++)
            c.States.Add(new ScenarioState { Id = "preflight_" + p, Name = "Region " + p,
                MapIndex = 3000 + p, NationMapIndex = ownership[p], NationId = "MP" + (ownership[p] - 500), Population = 100_000 });
        c.AttachProvinceTopology(topology);
        c.Armies.Clear();
        c.Wars.Add(new StrategicWar { Id = 883, AttackerId = 0, DefenderId = 1 });
        var battle = new LandBattle { Id = 777, WarId = 883, ProvinceId = 2, AttackerId = 0, DefenderId = 1,
            AttackerLosses = 123, DefenderLosses = 456 };
        for (var id = 0; id < 2; id++)
        {
            var province = id + 1;
            var centre = topology.CenterOf(province);
            var army = new Army { Id = 9000 + id, NationId = id, ProvinceId = province,
                StateMapIndex = topology.ProvinceToState[province], BattleId = battle.Id,
                AssignedWarId = 883, FrontLineId = 40 + id, ArmyGroupId = 60 + id,
                OrderKind = id == 0 ? ArmyOrder.AdvanceFront : ArmyOrder.DefendFront,
                OrderProvinceId = 2, ObjectiveStateMapIndex = 3002, OrderIssuedDay = 7,
                OrderRouteSteps = 3, OrderStepsCompleted = 1, PlanActive = true, Planning = .4f,
                Divisions = 2, Personnel = 24_000, MaxPersonnel = 30_000, Recovering = 550,
                Organisation = .7f, Strength = .8f, Equipment = .6f, Entrenchment = .4f,
                Latitude = centre.Latitude, Longitude = centre.Longitude,
                LastMoveResult = ArmyMoveResult.AttackIssued, LastMoveResultDay = 17 };
            army.PlannedRoute.Add(2);
            c.Armies.Add(army);
            (id == 0 ? battle.Attackers : battle.Defenders).Add(army.Id);
            var line = new DrawnFrontLine { Id = army.FrontLineId, NationId = id };
            line.ArmyIds.Add(army.Id); line.Posts[army.Id] = province;
            c.FrontLines.Add(line);
        }
        c.Battles.Add(battle);
        return c;
    }
}
