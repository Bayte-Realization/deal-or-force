using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Damage rows, the line, the reserve and the reinforcement queue are kept on that
/// campaign, keyed by battle id. A second world's battle must not be handed the first
/// world's lists, and filling the second must not clear the rows or the line the first
/// still holds. A direct order is not rewritten.
/// </summary>
static class BattleDamageIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("battle damage isolation: " + note); Console.WriteLine("PASS battle damage isolation: " + note); }

    public static void Run()
    {
        var first = World(61121, battleId: 4, otherBattle: 5, attackerNation: 0, defenderNation: 1, withDirect: true, strength: 1f);
        var second = World(61122, battleId: 8, otherBattle: -1, attackerNation: 2, defenderNation: 1, withDirect: false, strength: 0.5f);
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1] &&
              second.NationById(0) == second.Nations[0] && second.NationById(2) == second.Nations[2],
            "nation id is the list index");
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var direct = first.Armies.Single(army => army.DirectOrder);
        var firstBattle = first.Battles[0];
        var secondBattle = second.Battles[0];
        var other = first.Battles[1];
        var firstSide = Side(first, 0, direct: false);
        var secondSide = Side(second, 2, direct: false);

        Warfare.FillBattleBuckets(first, firstBattle);
        var line = Warfare.BattleAttackerLine(first, firstBattle.Id);
        var reserve = Warfare.BattleAttackerReserve(first, firstBattle.Id);
        var reinforcements = Warfare.BattleReinforcements(first, firstBattle.Id);
        Check(Ids(line).SequenceEqual(new[] { firstBattle.Attackers[0] }) && reserve.Count == 0 &&
              Ids(reinforcements).SequenceEqual(new[] { direct.Id }),
            "the first campaign keeps its attacker in its own line and the direct order waiting");
        Check(Warfare.BattleAttackerLine(second, secondBattle.Id).Count == 0 &&
              Warfare.BattleDamageRows(second, secondBattle.Id).Count == 0 &&
              !ReferenceEquals(line, Warfare.BattleAttackerLine(second, secondBattle.Id)),
            "a second campaign does not observe the first campaign's line or damage rows");

        Warfare.FillBattleDamageRows(first, firstBattle, firstSide, 0.2f, 1f);
        var rows = Warfare.BattleDamageRows(first, firstBattle.Id);
        var saved = Copy(rows);
        Check(saved.Length == 1 && saved[0].Personnel > 1f,
            "the first campaign records a damage row for its own formation");

        Warfare.FillBattleBuckets(second, secondBattle);
        Warfare.FillBattleDamageRows(second, secondBattle, secondSide, 0.2f, 1f);
        Warfare.FillBattleDamageRows(second, secondBattle, secondSide, 0.2f, 1f);
        Check(ReferenceEquals(line, Warfare.BattleAttackerLine(first, firstBattle.Id)) &&
              ReferenceEquals(reserve, Warfare.BattleAttackerReserve(first, firstBattle.Id)) &&
              ReferenceEquals(reinforcements, Warfare.BattleReinforcements(first, firstBattle.Id)) &&
              Ids(line).SequenceEqual(new[] { firstBattle.Attackers[0] }) &&
              Ids(reinforcements).SequenceEqual(new[] { direct.Id }),
            "the second campaign's battle leaves the first campaign's line in place");
        Check(ReferenceEquals(rows, Warfare.BattleDamageRows(first, firstBattle.Id)) && Same(saved, rows),
            "the second campaign's battle leaves the first campaign's damage rows in place");
        var secondRows = Warfare.BattleDamageRows(second, secondBattle.Id);
        Check(!ReferenceEquals(rows, secondRows) && !ReferenceEquals(line, Warfare.BattleAttackerLine(second, secondBattle.Id)) &&
              Ids(Warfare.BattleAttackerLine(second, secondBattle.Id)).SequenceEqual(new[] { secondBattle.Attackers[0] }) &&
              secondRows.Count == 1 && secondRows[0].Personnel > 1f && secondRows[0].Personnel != saved[0].Personnel,
            "the damage rows and the line are separate objects");

        var otherArmy = ArmyById(first, other.Attackers[0]);
        Warfare.FillBattleBuckets(first, other);
        Warfare.FillBattleDamageRows(first, other, new List<Army> { otherArmy }, 0.2f, 1f);
        var otherRows = Warfare.BattleDamageRows(first, other.Id);
        Check(ReferenceEquals(line, Warfare.BattleAttackerLine(first, firstBattle.Id)) &&
              ReferenceEquals(rows, Warfare.BattleDamageRows(first, firstBattle.Id)) &&
              Ids(line).SequenceEqual(new[] { firstBattle.Attackers[0] }) && Same(saved, rows) &&
              !ReferenceEquals(line, Warfare.BattleAttackerLine(first, other.Id)) &&
              !ReferenceEquals(rows, otherRows) &&
              Ids(Warfare.BattleAttackerLine(first, other.Id)).SequenceEqual(new[] { other.Attackers[0] }) &&
              otherRows.Count == 1 && otherRows[0].Personnel != saved[0].Personnel,
            "another battle of the same campaign clears only its own line and damage rows");
        Warfare.FillBattleBuckets(first, firstBattle);
        Warfare.FillBattleDamageRows(first, firstBattle, firstSide, 0.2f, 1f);
        Check(ReferenceEquals(line, Warfare.BattleAttackerLine(first, firstBattle.Id)) &&
              ReferenceEquals(rows, Warfare.BattleDamageRows(first, firstBattle.Id)) &&
              Same(saved, rows),
            "the same battle reuses its line and its damage rows");
        Check(direct.DirectOrder && direct.OrderKind == ArmyOrder.AdvanceFront && direct.OrderProvinceId == 1 &&
              direct.BattleId < 0 && direct.ProvinceId == 1 &&
              !firstBattle.Attackers.Contains(direct.Id) && !other.Attackers.Contains(direct.Id),
            "a direct order is not rewritten");
        Check(!first.Defines.FrontStaffMovesAdvances && !second.Defines.FrontStaffMovesAdvances,
            "front staff still does not move advances");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the fills do not change either fingerprint");
    }

    static (float Personnel, float Casualties)[] Copy(IReadOnlyList<(float Personnel, float Casualties)> rows)
    {
        var copy = new (float Personnel, float Casualties)[rows.Count];
        for (var i = 0; i < rows.Count; i++) copy[i] = rows[i];
        return copy;
    }

    static bool Same((float Personnel, float Casualties)[] saved, IReadOnlyList<(float Personnel, float Casualties)> rows)
    {
        if (saved.Length != rows.Count) return false;
        for (var i = 0; i < saved.Length; i++)
            if (saved[i].Personnel != rows[i].Personnel || saved[i].Casualties != rows[i].Casualties) return false;
        return true;
    }

    static int[] Ids(IReadOnlyList<Army> armies)
    {
        var ids = new int[armies.Count];
        for (var i = 0; i < armies.Count; i++) ids[i] = armies[i].Id;
        return ids;
    }

    static Army ArmyById(Campaign campaign, int id)
    {
        for (var i = 0; i < campaign.Armies.Count; i++)
            if (campaign.Armies[i].Id == id) return campaign.Armies[i];
        return null;
    }

    static List<Army> Side(Campaign campaign, int nationId, bool direct)
    {
        Army found = null;
        for (var i = 0; i < campaign.Armies.Count; i++)
        {
            var army = campaign.Armies[i];
            if (army.NationId == nationId && army.DirectOrder == direct) { found = army; break; }
        }
        return new List<Army> { found };
    }

    static Campaign World(int seed, int battleId, int otherBattle, int attackerNation, int defenderNation, bool withDirect, float strength)
    {
        var campaign = new Campaign(seed);
        var nations = Math.Max(attackerNation, defenderNation) + 1;
        for (var id = 0; id < nations; id++)
            campaign.Nations.Add(new Nation { Id = id, MapIndex = (ushort)(10 + id), Tag = "N" + id, Name = "Nation " + id, Population = 1000 });
        var war = new StrategicWar { Id = 1, AttackerId = attackerNation, DefenderId = defenderNation };
        campaign.Wars.Add(war);
        campaign.Battles.Add(Battle(battleId, province: 1, attackerNation, defenderNation));
        campaign.Armies.Add(Formation(battleId * 10 + 1, attackerNation, province: 1, strength, direct: false));
        campaign.Armies.Add(Formation(battleId * 10 + 2, defenderNation, province: 1, strength, direct: false));
        if (otherBattle >= 0)
        {
            campaign.Battles.Add(Battle(otherBattle, province: 2, defenderNation, attackerNation));
            campaign.Armies.Add(Formation(otherBattle * 10 + 1, defenderNation, province: 2, 0.25f, direct: false));
            campaign.Armies.Add(Formation(otherBattle * 10 + 2, attackerNation, province: 2, 0.25f, direct: false));
        }
        if (withDirect)
            campaign.Armies.Add(Formation(9, attackerNation, province: 1, strength, direct: true));
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

    static Army Formation(int id, int nation, int province, float strength, bool direct) =>
        new()
        {
            Id = id,
            NationId = nation,
            ProvinceId = province,
            OrderProvinceId = province,
            AssignedWarId = 1,
            BattleId = direct ? -1 : 1,
            OrderKind = ArmyOrder.AdvanceFront,
            DirectOrder = direct,
            Divisions = 1f,
            Personnel = 1000,
            Strength = strength
        };
}
