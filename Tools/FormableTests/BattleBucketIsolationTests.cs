using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Battle buckets are kept per campaign. A second world in the same process must not be
/// handed the first world's sides, line, or reinforcements, and looking at the second
/// must not clear the buckets the first still holds.
/// </summary>
static class BattleBucketIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("battle bucket isolation: " + note); Console.WriteLine("PASS battle bucket isolation: " + note); }

    public static void Run()
    {
        var first = World(province: 1, attacker: 1, defender: 2, spare: 9);
        var second = World(province: 3, attacker: 3, defender: 4, spare: 8);
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var direct = first.Armies.Single(army => army.DirectOrder);
        var firstBattle = first.Battles[0];
        var secondBattle = second.Battles[0];

        Warfare.FillBattleBuckets(first, firstBattle);
        Check(Ids(Warfare.BattleAttackers(first)).SequenceEqual(new[] { 1 }) &&
              Ids(Warfare.BattleDefenders(first)).SequenceEqual(new[] { 2 }),
            "the first campaign places its own armies on the two sides");
        Check(Ids(Warfare.BattleAttackerLine(first)).SequenceEqual(new[] { 1 }) &&
              Ids(Warfare.BattleDefenderLine(first)).SequenceEqual(new[] { 2 }) &&
              Warfare.BattleAttackerReserve(first).Count == 0 && Warfare.BattleDefenderReserve(first).Count == 0,
            "the first campaign keeps those armies in its own line");
        Check(Ids(Warfare.BattlePresent(first)).SequenceEqual(new[] { 2 }) &&
              Ids(Warfare.BattleReinforcements(first)).SequenceEqual(new[] { 9 }) &&
              Ids(Warfare.BattleLineCandidates(first)).SequenceEqual(new[] { 2 }),
            "the first campaign records the defender present and the direct order waiting");
        Check(direct.DirectOrder && direct.OrderProvinceId == 1 && direct.ProvinceId == 1 && direct.BattleId < 0 &&
              !firstBattle.Attackers.Contains(direct.Id) && firstBattle.LastAttackerLine.Count == 0,
            "a direct order is not written into the battle");
        Check(Warfare.BattleAttackers(second).Count == 0 && Warfare.BattleDefenders(second).Count == 0 &&
              Warfare.BattlePresent(second).Count == 0 && Warfare.BattleReinforcements(second).Count == 0 &&
              Warfare.BattleAttackerLine(second).Count == 0 && Warfare.BattleDefenderLine(second).Count == 0 &&
              Warfare.BattleLineCandidates(second).Count == 0,
            "a second campaign does not observe the first campaign's buckets");

        var attackers = Warfare.BattleAttackers(first);
        var defenders = Warfare.BattleDefenders(first);
        var present = Warfare.BattlePresent(first);
        var reinforcements = Warfare.BattleReinforcements(first);
        var attackerLine = Warfare.BattleAttackerLine(first);
        var defenderLine = Warfare.BattleDefenderLine(first);
        var attackerReserve = Warfare.BattleAttackerReserve(first);
        var defenderReserve = Warfare.BattleDefenderReserve(first);
        var candidates = Warfare.BattleLineCandidates(first);
        Warfare.FillBattleBuckets(second, secondBattle);
        Check(Ids(Warfare.BattleAttackers(second)).SequenceEqual(new[] { 3 }) &&
              Ids(Warfare.BattleDefenders(second)).SequenceEqual(new[] { 4 }) &&
              Ids(Warfare.BattleAttackerLine(second)).SequenceEqual(new[] { 3 }) &&
              Ids(Warfare.BattleDefenderLine(second)).SequenceEqual(new[] { 4 }) &&
              Ids(Warfare.BattlePresent(second)).SequenceEqual(new[] { 4 }) &&
              Ids(Warfare.BattleReinforcements(second)).SequenceEqual(new[] { 8 }),
            "the second campaign records only its own sides, line, presence and reinforcement");
        Check(ReferenceEquals(attackers, Warfare.BattleAttackers(first)) &&
              Ids(attackers).SequenceEqual(new[] { 1 }) && Ids(defenders).SequenceEqual(new[] { 2 }) &&
              Ids(present).SequenceEqual(new[] { 2 }) && Ids(reinforcements).SequenceEqual(new[] { 9 }) &&
              Ids(attackerLine).SequenceEqual(new[] { 1 }) && Ids(defenderLine).SequenceEqual(new[] { 2 }) &&
              Ids(candidates).SequenceEqual(new[] { 2 }),
            "looking at the second campaign leaves the first campaign's buckets in place");
        Check(!ReferenceEquals(attackers, Warfare.BattleAttackers(second)) &&
              !ReferenceEquals(defenders, Warfare.BattleDefenders(second)) &&
              !ReferenceEquals(present, Warfare.BattlePresent(second)) &&
              !ReferenceEquals(reinforcements, Warfare.BattleReinforcements(second)) &&
              !ReferenceEquals(attackerLine, Warfare.BattleAttackerLine(second)) &&
              !ReferenceEquals(defenderLine, Warfare.BattleDefenderLine(second)) &&
              !ReferenceEquals(attackerReserve, Warfare.BattleAttackerReserve(second)) &&
              !ReferenceEquals(defenderReserve, Warfare.BattleDefenderReserve(second)) &&
              !ReferenceEquals(candidates, Warfare.BattleLineCandidates(second)),
            "the buckets are separate objects");
        Warfare.FillBattleBuckets(first, firstBattle);
        Check(ReferenceEquals(attackers, Warfare.BattleAttackers(first)) &&
              ReferenceEquals(defenders, Warfare.BattleDefenders(first)) &&
              ReferenceEquals(attackerLine, Warfare.BattleAttackerLine(first)) &&
              ReferenceEquals(reinforcements, Warfare.BattleReinforcements(first)) &&
              Ids(attackers).SequenceEqual(new[] { 1 }) && Ids(defenders).SequenceEqual(new[] { 2 }),
            "the same campaign reuses its buckets");
        Check(direct.OrderProvinceId == 1 && direct.BattleId < 0 &&
              CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the looks do not change either fingerprint");
    }

    static int[] Ids(System.Collections.Generic.IReadOnlyList<Army> armies) =>
        armies.Select(army => army.Id).ToArray();

    static Campaign World(int province, int attacker, int defender, int spare)
    {
        var states = new ushort[4];
        var owners = new ushort[4];
        var controllers = new ushort[4];
        for (var i = 1; i < states.Length; i++)
        {
            states[i] = 1;
            controllers[i] = 10;
        }
        var campaign = new Campaign(61109);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ATK", Population = 1000, IsHuman = true });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "DEF", Population = 1000 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, Map(states, owners));
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, controllers);
        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 };
        campaign.Wars.Add(war);
        var battle = new LandBattle
        {
            Id = 7,
            WarId = war.Id,
            ProvinceId = province,
            AttackerId = 0,
            DefenderId = 1
        };
        battle.Attackers.Add(attacker);
        battle.Defenders.Add(defender);
        campaign.Battles.Add(battle);
        campaign.Armies.Add(Formation(attacker, 0, province, war.Id, battle.Id, direct: false, order: ArmyOrder.AdvanceFront));
        campaign.Armies.Add(Formation(defender, 1, province, war.Id, battle.Id, direct: false, order: ArmyOrder.DefendFront));
        campaign.Armies.Add(Formation(spare, 0, province, war.Id, battleId: -1, direct: true, order: ArmyOrder.AdvanceFront));
        return campaign;
    }

    static Army Formation(int id, int nation, int province, int war, int battleId, bool direct, ArmyOrder order) =>
        new()
        {
            Id = id,
            NationId = nation,
            ProvinceId = province,
            OrderProvinceId = province,
            AssignedWarId = war,
            BattleId = battleId,
            OrderKind = order,
            DirectOrder = direct,
            Divisions = 1f,
            Personnel = 1000
        };

    static ProvinceTopology Map(ushort[] state, ushort[] owner)
    {
        var neighbors = new int[state.Length][];
        for (var i = 0; i < neighbors.Length; i++) neighbors[i] = Array.Empty<int>();
        var constructor = typeof(ProvinceTopology).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (ProvinceTopology)constructor.Invoke(new object[]
        {
            new ushort[1], state, owner, new int[state.Length], neighbors, 1, 1
        });
    }
}
