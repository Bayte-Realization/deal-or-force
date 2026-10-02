using System;
using System.Linq;
using DealOrForce.Simulation;

static class BattleWithdrawalTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void RunFixture()
    {
        Run(CreateFixture());
    }

    internal static Campaign CreateFixture()
    {
        // A small, real topology isolates command rules from stale world-map exports.
        // The live game is checked separately against its actual map and input path.
        var campaign = Campaign.CreateBootstrap(20260906);
        var rus = campaign.NationByTag("RUS");
        var ukr = campaign.NationByTag("UKR");
        const int width = 64, height = 32;
        var regions = new ushort[width * height];
        var control = new ushort[width * height];
        var russianState = campaign.States.First(state => state.NationMapIndex == rus.MapIndex).MapIndex;
        var ukrainianState = campaign.States.First(state => state.NationMapIndex == ukr.MapIndex).MapIndex;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            regions[y * width + x] = (ushort)(x < width / 2 ? russianState : ukrainianState);
            control[y * width + x] = (ushort)(x < width / 2 ? rus.MapIndex : ukr.MapIndex);
        }
        var topology = ProvinceTopology.Build(regions, control, width, height, _ => false);
        var owners = (ushort[])topology.ProvinceToNation.Clone();
        campaign.AttachProvinceTopology(topology);
        // Scenario ownership overlays refer to the real-world province ids, not this fixture.
        Array.Copy(owners, campaign.ProvinceController, owners.Length);
        Array.Copy(owners, topology.ProvinceToNation, owners.Length);
        campaign.Wars.Clear();
        campaign.Wars.Add(new StrategicWar { Id = 999, AttackerId = rus.Id, DefenderId = ukr.Id });
        return campaign;
    }

    public static void Run(Campaign c)
    {
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        var map = c.ProvinceMap;
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        var start = BorderFronts.Candidates(c, rus.Id, ukr.Id).First(p =>
            map.Neighbors[p].Any(n => c.ProvinceController[n] == ukr.MapIndex &&
                map.Neighbors[n].Any(f => c.ProvinceController[f] == ukr.MapIndex && !map.IsWater(f))));
        var target = map.Neighbors[start].First(n => c.ProvinceController[n] == ukr.MapIndex &&
            map.Neighbors[n].Any(f => c.ProvinceController[f] == ukr.MapIndex && !map.IsWater(f)));
        var originalController = c.ProvinceController[target];
        Army a = null, a2 = null, d = null;
        LandBattle battle = null;
        void Reset(bool twoAttackers = false)
        {
            c.Armies.Clear(); c.Battles.Clear();
            c.ProvinceController[target] = originalController;
            a = new Army { Id = 9001, NationId = rus.Id, ProvinceId = start, BattleId = 999,
                Divisions = 1, Organisation = .6f, Strength = .7f, Equipment = .8f,
                OrderKind = ArmyOrder.AdvanceFront, PlanActive = true, AssignedWarId = war.Id,
                FrontLineId = 34, ArmyGroupId = 44 };
            d = new Army { Id = 9002, NationId = ukr.Id, ProvinceId = target, BattleId = 999, Divisions = 1,
                Organisation = .5f, Strength = .6f, Equipment = .7f, PlanActive = true, AssignedWarId = war.Id };
            c.Armies.Add(a); c.Armies.Add(d);
            battle = new LandBattle { Id = 999, WarId = war.Id, ProvinceId = target,
                AttackerId = rus.Id, DefenderId = ukr.Id, AttackerLosses = 123, DefenderLosses = 456 };
            battle.Attackers.Add(a.Id); battle.Defenders.Add(d.Id);
            if (twoAttackers)
            {
                a2 = new Army { Id = 9003, NationId = rus.Id, ProvinceId = start, BattleId = 999, Divisions = 1 };
                c.Armies.Add(a2); battle.Attackers.Add(a2.Id);
            }
            c.Battles.Add(battle);
        }
        WithdrawArmyFromBattleCommand Order(Army army, int issuer)
        {
            var order = new WithdrawArmyFromBattleCommand { IssuerId = issuer, ArmyId = army.Id };
            order.Apply(c);
            return order;
        }
        Reset(true);
        var previousPlan = new DrawnFrontLine { Id = a.FrontLineId, NationId = rus.Id };
        previousPlan.ArmyIds.Add(a.Id);
        c.FrontLines.Add(previousPlan);
        Check(!Order(a, ukr.Id).Withdrawn && a.InBattle && battle.Attackers.Count == 2,
            "foreign issuer cannot cancel our attack");
        Check(Order(a, rus.Id).Withdrawn && !a.InBattle && !battle.Ended && a2.InBattle,
            "partial withdrawal leaves other attackers fighting");
        Check(!previousPlan.ArmyIds.Contains(a.Id), "withdrawn division removed from old plan membership");
        Check(a.ProvinceId == start && a.Organisation == .6f && a.Strength == .7f && a.Equipment == .8f &&
            a.ArmyGroupId == 44 && !a.PlanActive && a.FrontLineId == -1 && a.AssignedWarId == -1,
            "stop attack preserves damage and commander group, cancels automatic restart");
        Check(battle.WithdrawnAttackers.Contains(a.Id) && battle.AttackerLosses == 123 && battle.DefenderLosses == 456,
            "withdrawal history and losses preserved");
        Check(!Order(a, rus.Id).Withdrawn && battle.WithdrawnAttackers.Count == 1,
            "repeated withdrawal is idempotent");
        Check(Order(a2, rus.Id).Withdrawn && battle.Ended && battle.WinnerId == ukr.Id && !d.InBattle &&
            c.ProvinceController[target] == ukr.MapIndex,
            "last attacker ends battle immediately without taking the province");

        Reset();
        Check(Order(d, ukr.Id).Withdrawn && d.ProvinceId != target &&
            c.ProvinceController[d.ProvinceId] == ukr.MapIndex && !d.InBattle,
            "defender retreats to adjacent friendly land");
        Check(d.ReadyDay >= c.ElapsedDays + c.Defines.RestAfterRetreatDays && d.Organisation == .5f && d.Strength == .6f,
            "retreat has recovery delay, not free healing");
        Check(battle.Ended && battle.WinnerId == rus.Id && a.ProvinceId == target &&
            c.ProvinceController[target] == rus.MapIndex,
            "last defender yields the province through normal capture rules");

        Reset();
        var saved = map.Neighbors[target].Select(p => (p, c.ProvinceController[p])).ToArray();
        foreach (var (p, _) in saved) c.ProvinceController[p] = (ushort)rus.MapIndex;
        Check(!Order(d, ukr.Id).Withdrawn && d.InBattle && d.ProvinceId == target && !battle.Ended &&
            d.PlanActive && battle.Defenders.Contains(d.Id), "encircled defender cannot erase an ongoing battle");
        foreach (var (p, owner) in saved) c.ProvinceController[p] = owner;

        Reset();
        a.SupportOnly = true;
        Check(Order(d, ukr.Id).Withdrawn && battle.Ended && a.ProvinceId == start &&
            c.ProvinceController[target] == ukr.MapIndex,
            "support-only attackers cannot occupy ground after withdrawal");

        Reset();
        var garrison = new Army { Id = 9004, NationId = ukr.Id, ProvinceId = target, Divisions = 1 };
        c.Armies.Add(garrison);
        Check(Order(d, ukr.Id).Withdrawn && !battle.Ended && garrison.InBattle &&
            battle.Defenders.Contains(garrison.Id), "remaining garrison continues defending");

        var encoded = NetworkCommandCodec.TryEncode(new WithdrawArmyFromBattleCommand { IssuerId = rus.Id, ArmyId = a.Id },
            out var kind, out var json);
        Check(encoded && NetworkCommandCodec.TryDecode(kind, json, out var decoded) &&
            decoded is WithdrawArmyFromBattleCommand remote && remote.IssuerId == rus.Id && remote.ArmyId == a.Id,
            "withdrawal round-trips through whitelisted network command codec");
    }
}
