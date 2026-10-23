using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Coalition members fight, cover, march and retreat as real belligerents.</summary>
static class CoalitionCombatTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL coalition combat: " + label);
        Console.WriteLine("PASS coalition combat: " + label);
    }

    public static void Run()
    {
        AlliedGarrisonIsNotEmptyGround();
        AllMembersFightAndReinforce();
        LandingMeetsAnAlliedGarrison();
        AlliedRetreatIsNotSurrender();
        CoalitionCorridorKeepsNeutralBorders();
        FriendlyStagingUsesTheLiveCoalition();
        FailedRouteIsReviewedWhenAnAllyJoins();
        AlliedCoverStillHoldsAnEmptyGap();
        LastOccupierDoesNotDonateLandToAWithdrawnLeader();
        WithdrawalDoesNotEraseAlliedParticipants();
        CounteroffensiveChargesTheCorrectWarSide();
        BilateralRouteOrderIsUnchanged();
        ReplayedOrderAndSavedRosterAreStable();
        CoalitionMembershipIsAuthoritative();
    }

    static void AlliedGarrisonIsNotEmptyGround()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        Warfare.Tick(c);
        Check(c.Battles.Count == 1 && defender.InBattle && attacker.InBattle,
            "an enemy ally's garrison starts a battle instead of an unopposed capture");
        Check(c.ProvinceController[3] == 503 && c.Battles[0].DefenderId == 3,
            "the battle identifies the real defending country and does not hand over its ground");
        Check(c.Battles[0].Rounds.Count == 1 && defender.Strength < 1,
            "the allied garrison actually fires and takes losses, not only joins a decorative roster");
    }

    static void AllMembersFightAndReinforce()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        // Coalition help has to be in the province being fought over: a formation in the
        // province next door joins on the day it marches in, not from where it stands.
        // The route keeps the marching ally out of ResolveAdvances' initial join.
        var alliedAttack = Unit(c, 2, 2, 3, ArmyOrder.Spearhead);
        // A marching reserve reaches the reinforcement path, not ResolveAdvances' initial join.
        alliedAttack.Route.Add(1);
        var alliedDefence = Unit(c, 3, 1, 3, ArmyOrder.ReserveFront);
        var unrelated = Unit(c, 4, 4, 3, ArmyOrder.DefendFront);
        var nextDoor = Unit(c, 5, 1, 4, ArmyOrder.ReserveFront);
        var b = Battle(c, attacker, defender);
        Warfare.Tick(c);
        Check(b.Attackers.Contains(alliedAttack.Id) && b.Defenders.Contains(alliedDefence.Id),
            "both coalitions can reinforce a battle started by a different member");
        Check(b.AttackerReinforcements == 1 && b.DefenderReinforcements == 1,
            "reinforcement counters describe the actual coalition arrivals");
        Check(!b.Defenders.Contains(nextDoor.Id) && !nextDoor.InBattle,
            "an allied reserve in the province next door does not join from where it stands");
        Check(alliedAttack.Strength < 1 && alliedDefence.Strength < 1 && unrelated.Strength == 1 && !unrelated.InBattle,
            "both allies share the combat losses while a neutral co-located unit is excluded");
        Check(c.Wars[0].AttackerLosses > 0 && c.Wars[0].DefenderLosses > 0,
            "casualties of coalition units are charged to the correct war sides");
    }

    static void LandingMeetsAnAlliedGarrison()
    {
        var c = Fixture();
        var landed = Unit(c, 0, 2, 3, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        Warfare.LandInvasion(c, c.Wars[0], 3, new[] { landed });
        Check(c.Battles.Count == 1 && c.Battles[0].Defenders.Contains(defender.Id),
            "an amphibious landing meets all hostile garrisons rather than only the war leader");
        Check(c.ProvinceController[3] == 503 && c.Battles[0].DefenderId == 3,
            "a contested allied beach remains defended until its battle is won");
    }

    static void AlliedRetreatIsNotSurrender()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        defender.Organisation = .001f;
        var b = Battle(c, attacker, defender);
        Warfare.Tick(c);
        Check(b.Ended && b.WinnerId == attacker.NationId && c.ProvinceController[3] == 500,
            "defeating a coalition defender resolves and occupies the battle normally");
        Check(c.Armies.Contains(defender) && defender.ProvinceId == 4 && defender.ReadyDay > c.ElapsedDays,
            "a defeated ally retreats through its coalition's ground rather than surrendering beside it");
        Check(c.Wars[0].DefenderCaptured == 0,
            "an open allied escape route does not create fictitious prisoners");

        c = Fixture(); attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        b = Battle(c, attacker, defender);
        var command = new WithdrawArmyFromBattleCommand { IssuerId = 3, ArmyId = defender.Id };
        command.Apply(c);
        Check(command.Withdrawn && defender.ProvinceId == 4 && !defender.InBattle,
            "an explicit withdrawal uses the same legal allied retreat route");

        c = Fixture(); attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront); Battle(c, attacker, defender);
        c.ProvinceController[2] = c.ProvinceController[4] = 503;
        c.ProvinceMap.Neighbors[3] = new[] { 4, 2 };
        new WithdrawArmyFromBattleCommand { IssuerId = 3, ArmyId = defender.Id }.Apply(c);
        Check(defender.ProvinceId == 4,
            "explicit withdrawal skips a hostile-occupied province despite its friendly controller and unsorted neighbours");
    }

    static void CoalitionCorridorKeepsNeutralBorders()
    {
        var c = Fixture();
        var army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var command = new MoveArmyCommand { IssuerId = 0, ArmyId = army.Id, TargetProvinceId = 4 };
        command.Apply(c);
        Check(army.DirectOrder && army.PlannedRoute.SequenceEqual(new[] { 3, 4 }),
            "a direct order reaches the enemy leader through the enemy ally's province");
        c.SetProvinceController(3, 504);
        command.Apply(c);
        Check(army.LastMoveResult == ArmyMoveResult.NoLandRoute,
            "coalition routing does not cut through a neutral country");
    }

    static void FriendlyStagingUsesTheLiveCoalition()
    {
        var c = Fixture();
        c.ProvinceController[3] = 502;
        var army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var command = new MoveArmyCommand { IssuerId = 0, ArmyId = army.Id, TargetProvinceId = 4 };
        command.Apply(c);
        Check(army.Route.SequenceEqual(new[] { 3 }) && army.PlannedRoute.SequenceEqual(new[] { 3, 4 }),
            "an offensive corridor separates allied staging from hostile combat steps");
        army.MoveProgress = 1;
        MilitaryOperations.Tick(c);
        Check(army.ProvinceId == 3 && c.ProvinceController[3] == 502 && army.AssignedWarId == 883 && army.DirectOrder,
            "the ordinary marcher crosses allied staging without capturing it or cancelling the attack");

        c = Fixture(); c.ProvinceController[3] = 502;
        army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        new MoveArmyCommand { IssuerId = 0, ArmyId = army.Id, TargetProvinceId = 4 }.Apply(c);
        c.Wars[0].AttackerAllies.Remove(2);
        army.MoveProgress = 1;
        MilitaryOperations.Tick(c);
        Check(army.ProvinceId == 2 && army.Route.Count == 0 && army.LastMoveResult == ArmyMoveResult.RouteInterrupted,
            "a former ally's now-neutral border blocks the march immediately rather than turning into an attack");
    }

    static void FailedRouteIsReviewedWhenAnAllyJoins()
    {
        var c = Fixture();
        var war = c.Wars[0];
        war.DefenderAllies.Remove(3);
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        var revision = c.MapRevision;
        Warfare.Tick(c);
        Check(attacker.PlannedRoute.Count == 0 && c.Battles.Count == 0,
            "an unreachable neutral objective produces no assault");
        war.DefenderAllies.Add(3);
        Warfare.Tick(c);
        Check(c.MapRevision == revision && c.Battles.Count == 1 && defender.InBattle,
            "failed-route memoisation observes coalition membership changes even with no map capture");
    }

    static void AlliedCoverStillHoldsAnEmptyGap()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var covering = Unit(c, 1, 3, 4, ArmyOrder.DefendFront);
        covering.Divisions = 100;
        Warfare.Tick(c);
        Check(attacker.MoveProgress == 0 && c.ProvinceController[3] == 503,
            "an allied covering force protects an adjacent gap just as the leader's force does");
        covering.Divisions = 0;
        Warfare.Tick(c);
        Check(attacker.MoveProgress > 0,
            "removing real allied cover opens the gap without any extra waiting flag");
    }

    static void LastOccupierDoesNotDonateLandToAWithdrawnLeader()
    {
        var c = Fixture();
        var original = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var ally = Unit(c, 1, 2, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 2, 3, 3, ArmyOrder.DefendFront);
        var b = Battle(c, original, defender);
        b.Attackers.Add(ally.Id); ally.BattleId = b.Id;
        new WithdrawArmyFromBattleCommand { IssuerId = 0, ArmyId = original.Id }.Apply(c);
        defender.Organisation = .001f;
        Warfare.Tick(c);
        Check(b.Ended && c.ProvinceController[3] == 502 && ally.ProvinceId == 3 && original.ProvinceId == 2,
            "the surviving allied occupier takes hostile ground, not a leader that withdrew its entire force");
    }

    static void WithdrawalDoesNotEraseAlliedParticipants()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
        var ally = Unit(c, 2, 1, 3, ArmyOrder.DefendFront);
        var b = Battle(c, attacker, defender);
        b.Defenders.Add(ally.Id); ally.BattleId = b.Id;
        new WithdrawArmyFromBattleCommand { IssuerId = 3, ArmyId = defender.Id }.Apply(c);
        Check(!b.Ended && ally.InBattle && c.ProvinceController[3] == 503,
            "withdrawing one country's garrison leaves its ally defending the province");
        var random = c.Random.State;
        Check(WarCoalition.SameSide(c.Wars[0], 0, 2) && !WarCoalition.SameSide(c.Wars[0], 0, 4) &&
            !WarCoalition.FriendlyController(c, c.Wars[0], 0, 504) && c.Random.State == random,
            "shared coalition decisions exclude neutral countries and consume no random draws");
    }

    static void CounteroffensiveChargesTheCorrectWarSide()
    {
        var c = Fixture();
        var counterattacker = Unit(c, 0, 3, 3, ArmyOrder.AdvanceFront);
        var coattacker = Unit(c, 1, 1, 3, ArmyOrder.Spearhead);
        var defender = Unit(c, 2, 0, 2, ArmyOrder.DefendFront);
        var codefender = Unit(c, 3, 2, 2, ArmyOrder.ReserveFront);
        var b = Battle(c, counterattacker, defender); b.ProvinceId = 2;
        b.Attackers.Add(coattacker.Id); coattacker.BattleId = b.Id;
        b.Defenders.Add(codefender.Id); codefender.BattleId = b.Id;
        Warfare.Tick(c);
        Check(coattacker.Strength < 1 && codefender.Strength < 1 && b.Rounds.Count == 1,
            "the war's defending coalition can counterattack with both members actually fighting");
        Check(c.Wars[0].DefenderLosses == b.AttackerLosses && c.Wars[0].AttackerLosses == b.DefenderLosses,
            "counteroffensive casualties follow war allegiance, not the battle's attacker/defender labels");
    }

    static void BilateralRouteOrderIsUnchanged()
    {
        var c = Fixture();
        var war = c.Wars[0]; war.AttackerAllies.Clear(); war.DefenderAllies.Clear();
        var random = c.Random.State;
        var oracle = new Random(184321);
        var controls = c.ProvinceController;
        var equal = true;
        for (var sample = 0; sample < 64; sample++)
        {
            for (var p = 1; p <= 6; p++) controls[p] = (ushort)(500 + oracle.Next(3));
            for (var from = 1; from <= 6; from++)
            for (var to = 1; to <= 6; to++)
                equal &= c.ProvinceMap.FindOperationalRoute(from, to, 500, 501, controls)
                    .SequenceEqual(WarCoalition.OperationalRoute(c, war, 0, from, to, controls));
        }
        Check(equal && c.Random.State == random,
            "2,304 bilateral route queries preserve the legacy route sequence and consume no simulation RNG");
        Check(c.ProvinceMap.FindOperationalRoute(1, 2, (IReadOnlySet<ushort>)null, controls).Length == 0,
            "a missing coalition access set cannot authorize an uncontrolled corridor");
    }

    static void ReplayedOrderAndSavedRosterAreStable()
    {
        var first = Fixture(); var second = Fixture();
        foreach (var c in new[] { first, second })
        {
            Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
            Unit(c, 1, 3, 3, ArmyOrder.DefendFront);
            Unit(c, 2, 2, 2, ArmyOrder.Spearhead);
            Unit(c, 3, 1, 4, ArmyOrder.ReserveFront);
        }
        var order = new MoveArmyCommand { IssuerId = 0, ArmyId = 0, TargetProvinceId = 4 };
        var encoded = NetworkCommandCodec.TryEncode(order, out var kind, out var payload);
        var decoded = NetworkCommandCodec.TryDecode(kind, payload, out var remote);
        Check(encoded && decoded, "a coalition corridor order uses the existing network command codec");
        order.Apply(first); remote.Apply(second);
        Warfare.Tick(first); Warfare.Tick(second);
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(second) && first.Random.State == second.Random.State,
            "the same replayed coalition order produces the same authoritative world and RNG state");
        first.ConfigureSinglePlayer(0);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(first, "coalition-tests"), "coalition-tests");
        if (!saved.Success) throw new Exception("Coalition snapshot fixture failed: " + saved.Warning);
        Check(saved.Success && saved.Campaign.Wars.Single().AttackerAllies.SetEquals(new[] { 2 }) &&
            saved.Campaign.Wars.Single().DefenderAllies.SetEquals(new[] { 3 }),
            "coalition membership round-trips without introducing a new save format");
        var battle = first.Battles.Single(); var restored = saved.Campaign.Battles.Single();
        Check(restored.Attackers.SequenceEqual(battle.Attackers) && restored.Defenders.SequenceEqual(battle.Defenders) &&
            restored.AttackerLosses == battle.AttackerLosses && restored.DefenderLosses == battle.DefenderLosses,
            "mixed-country battle rosters and combat losses survive the actual campaign snapshot");
    }

    static void CoalitionMembershipIsAuthoritative()
    {
        var c = Fixture(); var war = c.Wars[0];
        var before = CampaignFingerprint.Of(c);
        war.AttackerAllies.Remove(2); war.AttackerAllies.Add(4);
        Check(CampaignFingerprint.Of(c) != before,
            "swapping one coalition member is visible to peer checks before any army moves");
        war.AttackerAllies.Clear(); war.AttackerAllies.Add(2); war.AttackerAllies.Add(4);
        var ordered = CampaignFingerprint.Of(c);
        war.AttackerAllies.Clear(); war.AttackerAllies.Add(4); war.AttackerAllies.Add(2);
        Check(CampaignFingerprint.Of(c) == ordered,
            "coalition fingerprints do not depend on hash-set insertion order");
        war.AttackerAllies.Clear(); war.AttackerAllies.Add(3);
        war.DefenderAllies.Clear(); war.DefenderAllies.Add(2);
        Check(CampaignFingerprint.Of(c) != before,
            "identical countries on opposite sides are not treated as the same coalition state");
    }

    static LandBattle Battle(Campaign c, Army attacker, Army defender)
    {
        var b = new LandBattle { Id = 884, WarId = 883, ProvinceId = 3,
            AttackerId = attacker.NationId, DefenderId = defender.NationId, StartedDay = c.ElapsedDays };
        b.Attackers.Add(attacker.Id); b.Defenders.Add(defender.Id);
        attacker.BattleId = defender.BattleId = b.Id;
        c.Battles.Add(b);
        return b;
    }

    static Army Unit(Campaign c, int id, int nation, int province, ArmyOrder order)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var a = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderKind = order,
            OrderProvinceId = nation is 0 or 2 ? 3 : -1, AssignedWarId = 883,
            Latitude = centre.Latitude, Longitude = centre.Longitude,
            Divisions = 1, Organisation = 1, SupplyLevel = 1, Strength = 1,
            Planning = 1, PlanActive = true, OrderIssuedDay = -100 };
        c.Armies.Add(a);
        return a;
    }

    static Campaign Fixture()
    {
        const int width = 192, height = 64;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        var owners = new ushort[] { 0, 500, 500, 503, 501, 502, 504 };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = x / 32 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + p);
            controllers[index] = owners[p];
            rgb[index * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 6).ToDictionary(p => p, p => p));
        var c = new Campaign(20261015) { ScenarioId = "coalition_combat_fixture" };
        c.Defines.HoursPerTick = 24;
        c.Defines.FrontStaffCallsUpIdle = false;
        for (var id = 0; id < 5; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id, Tag = "CC" + id,
                Name = "Country " + id, Population = 10_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 6; p++)
            c.States.Add(new ScenarioState { Id = "coalition_region_" + p, Name = "Region " + p,
                MapIndex = 3000 + p, NationMapIndex = owners[p],
                NationId = "CC" + (owners[p] - 500), Population = 1_000_000 });
        c.AttachProvinceTopology(map);
        c.Armies.Clear();
        var war = new StrategicWar { Id = 883, AttackerId = 0, DefenderId = 1,
            LastFrontDay = c.ElapsedDays, FrontDirty = false };
        war.AttackerAllies.Add(2); war.DefenderAllies.Add(3);
        c.Wars.Add(war);
        return c;
    }
}
