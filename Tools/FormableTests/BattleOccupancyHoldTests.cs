using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

/// <summary>
/// A capture during the day's battles patches province occupancy and does not rebuild
/// the whole army index until those battles have been read. The occupancy a later
/// battle reads matches the army list. A second campaign's damage rows stay put.
/// A direct order is not rewritten.
/// </summary>
static class BattleOccupancyHoldTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("battle occupancy hold: " + note); Console.WriteLine("PASS battle occupancy hold: " + note); }

    public static void Run()
    {
        var first = World(61121, withDirect: true);
        var second = World(61122, withDirect: false);
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1] &&
              second.NationById(0) == second.Nations[0] && second.NationById(2) == second.Nations[2],
            "nation id is the list index");

        var firstBattle = first.Battles[0];
        var secondBattle = second.Battles[0];
        var direct = Army(first, 9);
        Army(first, 11).Strength = 1f;
        Army(second, 81).Strength = 0.25f;
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        Warfare.FillBattleDamageRows(first, firstBattle, new List<Army> { Army(first, 11) }, 0.2f, 1f);
        var rows = Warfare.BattleDamageRows(first, firstBattle.Id);
        var saved = rows[0];
        Check(Warfare.BattleDamageRows(second, secondBattle.Id).Count == 0 &&
              !ReferenceEquals(rows, Warfare.BattleDamageRows(second, secondBattle.Id)),
            "a second campaign does not observe the first campaign's damage rows");
        Warfare.FillBattleDamageRows(second, secondBattle, new List<Army> { Army(second, 81) }, 0.2f, 1f);
        Warfare.FillBattleDamageRows(second, secondBattle, new List<Army> { Army(second, 81) }, 0.2f, 1f);
        var secondRows = Warfare.BattleDamageRows(second, secondBattle.Id);
        Check(ReferenceEquals(rows, Warfare.BattleDamageRows(first, firstBattle.Id)) &&
              rows.Count == 1 && rows[0].Personnel == saved.Personnel && rows[0].Casualties == saved.Casualties,
            "the second campaign's battle leaves the first campaign's damage rows in place");
        Check(!ReferenceEquals(rows, secondRows) && secondRows.Count == 1 && secondRows[0].Personnel != saved.Personnel,
            "the damage rows are separate objects");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the fills do not change either fingerprint");

        Warfare.RebuildArmyIndex(first);
        Warfare.RebuildArmyIndex(second);
        var rebuilds = Warfare.ArmyIndexRebuilds(first);
        var secondRebuilds = Warfare.ArmyIndexRebuilds(second);
        var untouched = Warfare.IndexedProvinceArmies(first, 3);
        var secondSlot = Warfare.IndexedProvinceArmies(second, 5);
        Check(Ids(untouched).Length == 1 && Ids(untouched)[0] == 9 && Ids(secondSlot).Length == 1 && Ids(secondSlot)[0] == 84,
            "the rebuilt index places the direct order and the second campaign where they stand");

        Warfare.HoldBattleOccupancy(first);
        var mover = Army(first, 11);
        var removed = Army(first, 12);
        mover.ProvinceId = 2;
        mover.AssignedWarId = -1;
        Warfare.NoteFormationsMoved(first);
        var afterMove = CampaignFingerprint.Of(first);
        var occupied = Warfare.BattleOccupancy(first, 2);
        var again = Warfare.BattleOccupancy(first, 2);
        Check(Warfare.ArmyIndexRebuilds(first) == rebuilds,
            "a capture does not rebuild the index while the day's battles are still being read");
        Check(Same(occupied, Serial(first, 2)) && Same(again, occupied),
            "the second battle's occupancy matches the serial answer");
        Check(CampaignFingerprint.Of(first) == afterMove,
            "reading occupancy does not change the fingerprint");
        Check(ReferenceEquals(untouched, Warfare.IndexedProvinceArmies(first, 3)) &&
              Ids(Warfare.IndexedProvinceArmies(first, 3)).Length == 1 &&
              Ids(Warfare.IndexedProvinceArmies(first, 3))[0] == 9,
            "an untouched province keeps its list");
        Check(Contains(Warfare.IndexedWarArmies(first, 1), mover.Id),
            "the war bucket is not rebuilt until the day's battles have been read");

        first.Armies.Remove(removed);
        Warfare.NoteFormationsMoved(first);
        var afterRemove = CampaignFingerprint.Of(first);
        var leftBehind = Warfare.BattleOccupancy(first, 1);
        Check(Warfare.ArmyIndexRebuilds(first) == rebuilds && Same(leftBehind, Serial(first, 1)) && leftBehind.Length == 0,
            "a formation removed from the world leaves that province without a full rebuild");
        Check(CampaignFingerprint.Of(first) == afterRemove,
            "the occupancy read after the removal does not change the fingerprint");

        Warfare.ReleaseBattleOccupancy(first);
        Check(Warfare.ArmyIndexRebuilds(first) == rebuilds + 1,
            "releasing the day's battles rebuilds the index once");
        Check(Same(Warfare.BattleOccupancy(first, 2), Serial(first, 2)) &&
              !Contains(Warfare.IndexedWarArmies(first, 1), mover.Id),
            "after the rebuild the occupancy still matches the serial answer");
        Check(ReferenceEquals(secondSlot, Warfare.IndexedProvinceArmies(second, 5)) &&
              Ids(secondSlot).Length == 1 && Ids(secondSlot)[0] == 84 &&
              Warfare.ArmyIndexRebuilds(second) == secondRebuilds,
            "the first campaign's rebuild does not clear the second campaign's province list");
        Check(ReferenceEquals(rows, Warfare.BattleDamageRows(first, firstBattle.Id)) &&
              rows[0].Personnel == saved.Personnel &&
              ReferenceEquals(secondRows, Warfare.BattleDamageRows(second, secondBattle.Id)) &&
              secondRows[0].Personnel != saved.Personnel,
            "two campaigns still do not clear each other's damage rows");
        Check(direct.DirectOrder && direct.OrderKind == ArmyOrder.AdvanceFront && direct.OrderProvinceId == 3 &&
              direct.ProvinceId == 3 && direct.BattleId < 0 && direct.AssignedWarId == 1,
            "a direct order is not rewritten");
        Check(!first.Defines.FrontStaffMovesAdvances && !second.Defines.FrontStaffMovesAdvances,
            "front staff still does not move advances");
        Check(CampaignFingerprint.Of(second) == secondPrint,
            "the second campaign's fingerprint is unchanged");
    }

    static bool Contains(IReadOnlyList<Army> armies, int id)
    {
        if (armies is null) return false;
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].Id == id) return true;
        return false;
    }

    static int[] Ids(IReadOnlyList<Army> armies)
    {
        if (armies is null) return Array.Empty<int>();
        var ids = new int[armies.Count];
        for (var i = 0; i < armies.Count; i++) ids[i] = armies[i].Id;
        return ids;
    }

    static int[] Serial(Campaign campaign, int province)
    {
        var ids = new List<int>();
        for (var i = 0; i < campaign.Armies.Count; i++)
            if (campaign.Armies[i].ProvinceId == province) ids.Add(campaign.Armies[i].Id);
        return ids.ToArray();
    }

    static bool Same(int[] left, int[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    static Army Army(Campaign campaign, int id)
    {
        for (var i = 0; i < campaign.Armies.Count; i++)
            if (campaign.Armies[i].Id == id) return campaign.Armies[i];
        return null;
    }

    static Campaign World(int seed, bool withDirect)
    {
        var campaign = new Campaign(seed);
        var nations = withDirect ? 2 : 3;
        for (var id = 0; id < nations; id++)
            campaign.Nations.Add(new Nation { Id = id, MapIndex = (ushort)(10 + id), Tag = "N" + id, Name = "Nation " + id, Population = 1000 });
        campaign.Wars.Add(new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 });
        if (withDirect)
        {
            campaign.Battles.Add(Battle(4, 1, 0, 1, 11, 12));
            campaign.Armies.Add(Formation(11, 0, 1, direct: false));
            campaign.Armies.Add(Formation(12, 1, 1, direct: false));
            campaign.Armies.Add(Formation(13, 1, 2, direct: false));
            campaign.Armies.Add(Formation(9, 0, 3, direct: true));
        }
        else
        {
            campaign.Battles.Add(Battle(8, 4, 2, 1, 81, 82));
            campaign.Armies.Add(Formation(81, 2, 4, direct: false));
            campaign.Armies.Add(Formation(82, 1, 4, direct: false));
            campaign.Armies.Add(Formation(84, 2, 5, direct: false));
        }
        return campaign;
    }

    static LandBattle Battle(int id, int province, int attacker, int defender, int attackerArmy, int defenderArmy)
    {
        var battle = new LandBattle
        {
            Id = id,
            WarId = 1,
            ProvinceId = province,
            AttackerId = attacker,
            DefenderId = defender
        };
        battle.Attackers.Add(attackerArmy);
        battle.Defenders.Add(defenderArmy);
        return battle;
    }

    static Army Formation(int id, int nation, int province, bool direct) =>
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
            Strength = direct ? 1f : 0.5f,
            Organisation = 1f,
            SupplyLevel = 1f
        };
}
