using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A player's report: "I captured a province. I right-click it - my army is standing in it,
/// the province is the enemy's and occupied by me - so as not to attack, and my army leaves
/// the province." The map handler sent every fighting formation standing in the clicked
/// province a battle withdrawal; for a defender that is a retreat to friendly ground, so the
/// click meant to hold the province gave it up. Now only an attack is broken off that way
/// (the attacker stands in the province it attacks from and stays there); a defence holds.
///
/// Click() below is the order the map handler (ArmyOperationsUI.OnProvinceCommanded) issues
/// for a plain right-click, so the test drives the same two commands the game does.
/// </summary>
static class OwnProvinceClickTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("own-province click: " + label); Console.WriteLine("PASS own-province click: " + label); }

    const int Player = 0, Enemy = 1;
    const ushort PlayerMap = 501, EnemyMap = 502;

    /// <summary>Five provinces in a row: 1-3 the player's, 4-5 the enemy's, 4 occupied by the player.</summary>
    static Campaign Fixture()
    {
        var c = new Campaign(20261114) { ScenarioId = "own_province_click_fixture" };
        c.AddNation(new Nation { Tag = "RUS", MapIndex = PlayerMap, Population = 3_000_000, Gdp = 100,
            Treasury = 100, OutputScale = 1, Stability = .8f, WarSupport = 1, IsHuman = true });
        c.AddNation(new Nation { Tag = "UKR", MapIndex = EnemyMap, Population = 2_000_000, Gdp = 60,
            Treasury = 60, OutputScale = 1, Stability = .8f, WarSupport = 1, IsHuman = true });
        for (var i = 0; i < 5; i++)
            c.States.Add(new ScenarioState { Id = "click_fixture_" + i, MapIndex = 1001 + i,
                Name = "Click fixture " + i, NationId = i < 3 ? "RUS" : "UKR",
                NationMapIndex = i < 3 ? PlayerMap : EnemyMap, Population = 1_000_000, Stability = .8f });
        var cells = Enumerable.Range(0, 80).Select(i => (ushort)(1001 + i % 20 / 4)).ToArray();
        var owners = Enumerable.Range(0, 80).Select(i => i % 20 < 12 ? PlayerMap : EnemyMap).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 20, 4, _ => false));
        for (var p = 1; p <= 5; p++)
            c.ProvinceMap.Neighbors[p] = p == 1 ? new[] { 2 } : p == 5 ? new[] { 4 } : new[] { p - 1, p + 1 };
        c.Wars.Add(new StrategicWar { Id = 0, AttackerId = Player, DefenderId = Enemy, StartedDay = 0,
            AiNegotiationUnlockDay = 99999, ClaimedStateMapIndex = 1004, FrontDirty = false, LastFrontDay = 0 });
        CampaignDiplomacy.Get(c, Player, Enemy).Stance = DiplomaticStance.AtWar;
        c.Nations[Player].CapitalProvinceId = 1; c.Nations[Enemy].CapitalProvinceId = 5;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 12L * 24);
        SovereignFinance.NormalizeAccounts(c);
        c.ConfigureSinglePlayer(Player);
        // Both seats human, as WarStalemateTests does: no computer planner moves anybody here.
        c.Nations[Player].IsHuman = true; c.Nations[Enemy].IsHuman = true;
        c.IsPaused = true;
        c.SetProvinceController(4, PlayerMap);
        return c;
    }

    static Army Unit(Campaign c, int id, int nation, int province)
    {
        var a = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1, Organisation = 1, Strength = 1, SupplyLevel = 1, Planning = 1,
            Personnel = 15000, MaxPersonnel = 15000, Equipment = 1, Cohesion = 1, Name = "Formation " + id };
        (a.Latitude, a.Longitude) = c.ProvinceMap.CenterOf(province);
        c.Armies.Add(a); return a;
    }

    static LandBattle Fight(Campaign c, int province, Army attacker, Army defender)
    {
        var battle = new LandBattle { Id = c.NextBattleId++, WarId = 0, ProvinceId = province,
            AttackerId = attacker.NationId, DefenderId = defender.NationId, StartedDay = c.ElapsedDays };
        battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id);
        attacker.BattleId = defender.BattleId = battle.Id;
        attacker.AssignedWarId = defender.AssignedWarId = 0;
        c.Battles.Add(battle);
        return battle;
    }

    /// <summary>A plain right-click on a province with one formation selected, as the map handler issues it.</summary>
    static ArmyMoveResult Click(Campaign c, Army army, int province)
    {
        if (BattleMovementOrders.OwnProvinceClickBreaksOff(c, army, province))
        {
            var withdrawal = new WithdrawArmyFromBattleCommand { IssuerId = army.NationId, ArmyId = army.Id };
            c.Commands.Enqueue(withdrawal);
            c.Commands.ApplyAll(c);
            return ArmyMoveResult.None;
        }
        var move = new MoveArmyCommand { IssuerId = army.NationId, ArmyId = army.Id, TargetProvinceId = province };
        c.Commands.Enqueue(move);
        c.Commands.ApplyAll(c);
        return move.Result;
    }

    public static void Run()
    {
        DefenderHolds();
        RetreatIsStillAvailableOnPurpose();
        AttackerBreaksOffInPlace();
        MarchCancelledAndHeld();
    }

    /// <summary>The reported case: the enemy counter-attacks the occupied province and the player clicks it.</summary>
    static void DefenderHolds()
    {
        var c = Fixture();
        var own = Unit(c, 1, Player, 4);
        var enemy = Unit(c, 2, Enemy, 5);
        var battle = Fight(c, 4, enemy, own);
        own.PostBattleWaypoints.Add(3);
        Check(c.ProvinceController[4] == PlayerMap && c.ProvinceMap.ProvinceToNation[4] == EnemyMap,
            "the fixture's province 4 is the enemy's, occupied by the player");
        var result = Click(c, own, 4);
        Check(own.ProvinceId == 4 && own.InBattle && !battle.Ended && battle.Defenders.Contains(own.Id),
            $"the army defending the occupied province stays in it and keeps fighting (province {own.ProvinceId}, battle {own.BattleId})");
        Check(c.ProvinceController[4] == PlayerMap, "the occupied province is not handed back to the enemy");
        Check(own.PostBattleWaypoints.Count == 0 && result == ArmyMoveResult.OrderCancelled,
            $"the click cancels the queued post-battle march instead ({result})");
        Check(Click(c, own, 4) == ArmyMoveResult.AlreadyAtDestination && own.ProvinceId == 4 && own.InBattle,
            "a second click with nothing queued is still a hold");
        Check(!BattleMovementOrders.OwnProvinceClickBreaksOff(c, own, 4),
            "a defender's click on its own province is not a withdrawal");
    }

    /// <summary>Why it left: the withdrawal the click used to send is a retreat for a defender. Still there for H.</summary>
    static void RetreatIsStillAvailableOnPurpose()
    {
        var c = Fixture();
        var own = Unit(c, 1, Player, 4);
        var enemy = Unit(c, 2, Enemy, 5);
        Fight(c, 4, enemy, own);
        var withdrawal = new WithdrawArmyFromBattleCommand { IssuerId = Player, ArmyId = own.Id };
        c.Commands.Enqueue(withdrawal);
        c.Commands.ApplyAll(c);
        Check(withdrawal.Withdrawn && !own.InBattle && own.ProvinceId == 3,
            $"an explicit withdrawal (H, Leave battle) of a defender still falls back out of the province (to {own.ProvinceId})");
    }

    /// <summary>The army in the occupied province is attacking the next one; a click on its own province stops the attack.</summary>
    static void AttackerBreaksOffInPlace()
    {
        var c = Fixture();
        var own = Unit(c, 1, Player, 4);
        var enemy = Unit(c, 2, Enemy, 5);
        var battle = Fight(c, 5, own, enemy);
        Check(BattleMovementOrders.OwnProvinceClickBreaksOff(c, own, 4),
            "an attacker's click on the province it attacks from breaks the attack off");
        Check(!BattleMovementOrders.OwnProvinceClickBreaksOff(c, own, 5),
            "a click on the attacked province is an ordinary order, not a withdrawal");
        Click(c, own, 4);
        Check(!own.InBattle && own.ProvinceId == 4 && c.ProvinceController[4] == PlayerMap,
            $"the attack is stopped and the army stays in the occupied province (province {own.ProvinceId})");
        Check(battle.Ended || !battle.Attackers.Contains(own.Id), "the formation is no longer an attacker in that battle");
    }

    /// <summary>Not fighting, on its way to attack: the click cancels the march and the army stays, days later too.</summary>
    static void MarchCancelledAndHeld()
    {
        var c = Fixture();
        var own = Unit(c, 1, Player, 4);
        var order = Click(c, own, 5);
        Check(order is ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued && own.OrderProvinceId == 5,
            $"the fixture's army is ordered on to the next enemy province ({order})");
        var result = Click(c, own, 4);
        Check(result == ArmyMoveResult.OrderCancelled && own.Route.Count == 0 && own.PlannedRoute.Count == 0 &&
            own.OrderProvinceId < 0 && own.ProvinceId == 4,
            $"a click on the province it stands in cancels the march where it stands ({result})");
        c.IsPaused = false;
        for (var day = 0; day < 10; day++) c.TickDay();
        Check(own.ProvinceId == 4 && own.Route.Count == 0 && c.ProvinceController[4] == PlayerMap,
            $"ten days later the army still holds the occupied province (province {own.ProvinceId})");
    }
}
