using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// What is in a battle and what is merely in the neighbourhood.
///
/// Two rules hold this together. A battle is fed by the province it is fought over and by
/// nothing else, so a defender cannot pour in two fresh formations a day from the provinces
/// around him. And a side fights with the frontage it is given, so nineteen divisions in a
/// province put a line of four in front of an attacker rather than nineteen.
/// </summary>
static class BattleReinforcementTests
{
    private delegate void ReinforceCall(Campaign campaign, LandBattle battle);
    private delegate void FrontageCall(IReadOnlyCollection<Army> side, float room, List<Army> line, List<Army> reserve);

    private static readonly ReinforceCall Reinforce = typeof(Warfare)
        .GetMethod("ReinforceBattle", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<ReinforceCall>();
    private static readonly FrontageCall SplitFrontage = typeof(Warfare)
        .GetMethod("Frontage", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<FrontageCall>();

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void RunFixture() => Run(BattleWithdrawalTests.CreateFixture());

    public static void Run(Campaign c)
    {
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        var map = c.ProvinceMap;
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));

        // A defended border province with a second enemy province beside it, so "the province
        // next door" is a real place with a real id rather than province zero.
        var start = BorderFronts.Candidates(c, rus.Id, ukr.Id).First(p =>
            map.Neighbors[p].Any(n => c.ProvinceController[n] == ukr.MapIndex &&
                map.Neighbors[n].Any(f => f != p && c.ProvinceController[f] == ukr.MapIndex && !map.IsWater(f))));
        var target = map.Neighbors[start].First(n => c.ProvinceController[n] == ukr.MapIndex &&
            map.Neighbors[n].Any(f => f != start && c.ProvinceController[f] == ukr.MapIndex && !map.IsWater(f)));
        var flank = map.Neighbors[target].First(f => f != start && c.ProvinceController[f] == ukr.MapIndex && !map.IsWater(f));

        c.Armies.Clear();
        c.Battles.Clear();
        Army Foot(int id, int nation, int province, ArmyOrder order, bool fighting) => new Army
        {
            Id = id, NationId = nation, ProvinceId = province, BattleId = fighting ? 9001 : -1,
            Divisions = 1, Organisation = .92f, Strength = .92f, Equipment = .92f,
            SupplyLevel = .92f, Cohesion = .9f, Planning = 1f,
            OrderKind = order, PlanActive = order == ArmyOrder.AdvanceFront, AssignedWarId = war.Id
        };

        var attackerInLine = Foot(9101, rus.Id, target, ArmyOrder.AdvanceFront, true);
        var attackerNextDoor = Foot(9102, rus.Id, start, ArmyOrder.AdvanceFront, false);
        var attackerArriving = Foot(9103, rus.Id, target, ArmyOrder.AdvanceFront, false);
        var defenderInLine = Foot(9201, ukr.Id, target, ArmyOrder.DefendFront, true);
        var defenderArriving = Foot(9202, ukr.Id, target, ArmyOrder.DefendFront, false);
        var defenderNextDoor = Foot(9203, ukr.Id, flank, ArmyOrder.DefendFront, false);
        foreach (var army in new[] { attackerInLine, attackerNextDoor, attackerArriving,
                     defenderInLine, defenderArriving, defenderNextDoor }) c.Armies.Add(army);

        var battle = new LandBattle
        {
            Id = 9001, WarId = war.Id, ProvinceId = target,
            AttackerId = rus.Id, DefenderId = ukr.Id
        };
        battle.Attackers.Add(attackerInLine.Id);
        battle.Defenders.Add(defenderInLine.Id);
        c.Battles.Add(battle);

        Reinforce(c, battle);

        Check(battle.Defenders.Contains(defenderArriving.Id) && defenderArriving.InBattle,
            "a defender who reached the province joins the defence");
        Check(battle.Attackers.Contains(attackerArriving.Id) && attackerArriving.InBattle,
            "an attacker who reached the province joins the assault");
        Check(!battle.Defenders.Contains(defenderNextDoor.Id) && !defenderNextDoor.InBattle,
            "a defender standing in the province next door does not join");
        Check(!battle.Attackers.Contains(attackerNextDoor.Id) && !attackerNextDoor.InBattle,
            "an attacker standing in the province next door does not join");
        Check(battle.DefenderReinforcements == 1 && battle.AttackerReinforcements == 1,
            "only formations in the province count as reinforcements");

        // Frontage. Nineteen divisions in a province put a line in front of the attacker, not
        // nineteen divisions' worth of fire.
        var side = new List<Army>();
        for (var i = 0; i < 19; i++)
            side.Add(new Army
            {
                Id = 9300 + i, NationId = ukr.Id, ProvinceId = target, Divisions = 1,
                Organisation = .9f, Strength = .2f + i * .04f, Equipment = .9f, SupplyLevel = .9f
            });
        var width = UnitTypeCatalog.Get(side[0].TemplateId).CombatWidth;
        var line = new List<Army>();
        var reserve = new List<Army>();

        SplitFrontage(side, width * 4f, line, reserve);
        Check(line.Count == 4 && reserve.Count == 15,
            "a front four formations wide takes four of nineteen and waits with fifteen");
        Check(line.All(army => !reserve.Contains(army)) && line.Count + reserve.Count == side.Count,
            "every formation is either in the line or in reserve");
        Check(line.Contains(side.OrderByDescending(army => army.Strength).First()),
            "the strongest formation is the one in the line");

        SplitFrontage(side, width * 0.5f, line, reserve);
        Check(line.Count == 1 && reserve.Count == 18,
            "a front too narrow for anything still puts one formation in the line");

        SplitFrontage(side, width * 100f, line, reserve);
        Check(line.Count == 19 && reserve.Count == 0, "a front wide enough takes the whole stack");
    }
}
