using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The countries in a battle are kept on that campaign, keyed by battle id. A second
/// world's battle must not be handed the first world's sets, and filling the second
/// must not clear the sets the first still holds. A direct order is not rewritten.
/// </summary>
static class BattleSideIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("battle side isolation: " + note); Console.WriteLine("PASS battle side isolation: " + note); }

    public static void Run()
    {
        var first = World(61121, battleId: 4, otherBattle: 5, attackerNation: 0, defenderNation: 1, withDirect: true);
        var second = World(61122, battleId: 8, otherBattle: -1, attackerNation: 2, defenderNation: 1, withDirect: false);
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1] &&
              second.NationById(0) == second.Nations[0] && second.NationById(2) == second.Nations[2],
            "nation id is the list index");
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var direct = first.Armies.Single(army => army.DirectOrder);
        var firstBattle = first.Battles[0];
        var secondBattle = second.Battles[0];
        var other = first.Battles[1];

        Warfare.FillBattleNationSets(first, firstBattle, Side(first, 0), Side(first, 1));
        var attackers = Warfare.BattleAttackerNations(first, firstBattle.Id);
        var defenders = Warfare.BattleDefenderNations(first, firstBattle.Id);
        Check(attackers.SetEquals(new[] { 0 }) && defenders.SetEquals(new[] { 1 }),
            "the first campaign records its own two countries");
        Check(Warfare.BattleAttackerNations(second, secondBattle.Id).Count == 0 &&
              Warfare.BattleDefenderNations(second, secondBattle.Id).Count == 0 &&
              !ReferenceEquals(attackers, Warfare.BattleAttackerNations(second, secondBattle.Id)),
            "a second campaign does not observe the first campaign's side sets");

        Warfare.FillBattleNationSets(second, secondBattle, Side(second, 2), Side(second, 1));
        Warfare.FillBattleNationSets(second, secondBattle, Side(second, 2), Side(second, 1));
        Check(ReferenceEquals(attackers, Warfare.BattleAttackerNations(first, firstBattle.Id)) &&
              ReferenceEquals(defenders, Warfare.BattleDefenderNations(first, firstBattle.Id)) &&
              attackers.SetEquals(new[] { 0 }) && defenders.SetEquals(new[] { 1 }),
            "the second campaign's battle leaves the first campaign's side sets in place");
        Check(!ReferenceEquals(attackers, Warfare.BattleAttackerNations(second, secondBattle.Id)) &&
              !ReferenceEquals(defenders, Warfare.BattleDefenderNations(second, secondBattle.Id)) &&
              Warfare.BattleAttackerNations(second, secondBattle.Id).SetEquals(new[] { 2 }) &&
              Warfare.BattleDefenderNations(second, secondBattle.Id).SetEquals(new[] { 1 }),
            "the side sets are separate objects");

        Warfare.FillBattleNationSets(first, other, Side(first, 1), Side(first, 0));
        Check(ReferenceEquals(attackers, Warfare.BattleAttackerNations(first, firstBattle.Id)) &&
              attackers.SetEquals(new[] { 0 }) && defenders.SetEquals(new[] { 1 }) &&
              !ReferenceEquals(attackers, Warfare.BattleAttackerNations(first, other.Id)) &&
              Warfare.BattleAttackerNations(first, other.Id).SetEquals(new[] { 1 }) &&
              Warfare.BattleDefenderNations(first, other.Id).SetEquals(new[] { 0 }),
            "another battle of the same campaign clears only its own sets");
        Warfare.FillBattleNationSets(first, firstBattle, Side(first, 0), Side(first, 1));
        Check(ReferenceEquals(attackers, Warfare.BattleAttackerNations(first, firstBattle.Id)) &&
              ReferenceEquals(defenders, Warfare.BattleDefenderNations(first, firstBattle.Id)) &&
              attackers.SetEquals(new[] { 0 }) && defenders.SetEquals(new[] { 1 }),
            "the same battle reuses its sets");
        Check(direct.DirectOrder && direct.OrderKind == ArmyOrder.AdvanceFront && direct.OrderProvinceId == 1 &&
              direct.BattleId < 0 && direct.ProvinceId == 1 &&
              !firstBattle.Attackers.Contains(direct.Id) && !other.Attackers.Contains(direct.Id),
            "a direct order is not rewritten");
        Check(!first.Defines.FrontStaffMovesAdvances && !second.Defines.FrontStaffMovesAdvances,
            "front staff still does not move advances");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the fills do not change either fingerprint");
    }

    static List<Army> Side(Campaign campaign, int nationId)
    {
        var found = campaign.Armies.First(army => army.NationId == nationId && !army.DirectOrder);
        return new List<Army> { found };
    }

    static Campaign World(int seed, int battleId, int otherBattle, int attackerNation, int defenderNation, bool withDirect)
    {
        var campaign = new Campaign(seed);
        var nations = Math.Max(attackerNation, defenderNation) + 1;
        for (var id = 0; id < nations; id++)
            campaign.Nations.Add(new Nation { Id = id, MapIndex = (ushort)(10 + id), Tag = "N" + id, Name = "Nation " + id, Population = 1000 });
        campaign.Battles.Add(Battle(battleId, province: 1, attacker: attackerNation, defender: defenderNation));
        if (otherBattle >= 0)
            campaign.Battles.Add(Battle(otherBattle, province: 2, attacker: defenderNation, defender: attackerNation));
        campaign.Armies.Add(Formation(battleId * 10 + 1, attackerNation, direct: false));
        campaign.Armies.Add(Formation(battleId * 10 + 2, defenderNation, direct: false));
        if (withDirect)
            campaign.Armies.Add(Formation(9, attackerNation, direct: true));
        return campaign;
    }

    static LandBattle Battle(int id, int province, int attacker, int defender)
    {
        var battle = new LandBattle
        {
            Id = id,
            WarId = 1,
            ProvinceId = province,
            AttackerId = attacker,
            DefenderId = defender
        };
        battle.Attackers.Add(id * 10 + 1);
        battle.Defenders.Add(id * 10 + 2);
        return battle;
    }

    static Army Formation(int id, int nation, bool direct) =>
        new()
        {
            Id = id,
            NationId = nation,
            ProvinceId = 1,
            OrderProvinceId = 1,
            AssignedWarId = 1,
            BattleId = direct ? -1 : 1,
            OrderKind = ArmyOrder.AdvanceFront,
            DirectOrder = direct,
            Divisions = 1f,
            Personnel = 1000
        };
}
