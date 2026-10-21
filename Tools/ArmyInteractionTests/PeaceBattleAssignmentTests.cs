using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PeaceBattleAssignmentTests
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static string Snapshot(object value) => JsonSerializer.Serialize(value, Json);
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL peace battle assignment: " + label);
        Console.WriteLine("PASS: peace battle assignment " + label);
    }

    static Army Unit(Campaign c, int id, int nation, int province, int warId)
    {
        var army = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], AssignedWarId = warId,
            OrderKind = ArmyOrder.DefendFront, OrderProvinceId = province,
            Divisions = 2, Personnel = 17000, MaxPersonnel = 30000, Recovering = 79,
            Strength = .57f, Organisation = .43f, Equipment = .36f, Cohesion = .61f,
            Experience = .52f, SupplyLevel = .82f, MoveProgress = .25f };
        (army.Latitude, army.Longitude) = c.ProvinceMap.CenterOf(province);
        army.Route.Add(province); army.PlannedRoute.Add(province);
        c.Armies.Add(army); return army;
    }

    static LandBattle Battle(Campaign c, int id, int warId, int province, Army attacker, Army defender)
    {
        var battle = new LandBattle { Id = id, WarId = warId, ProvinceId = province,
            AttackerId = attacker.NationId, DefenderId = defender.NationId,
            StartedDay = c.ElapsedDays - 3, AttackerLosses = 321, DefenderLosses = 654 };
        battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id);
        battle.Rounds.Add(new LandBattle.Round { Day = c.ElapsedDays - 1,
            AttackerLosses = 21, DefenderLosses = 34, Note = "Recorded before settlement" });
        attacker.BattleId = defender.BattleId = id; c.Battles.Add(battle); return battle;
    }

    static Campaign Fixture()
    {
        var c = PartialFrontWithdrawalTests.Fixture();
        c.ScenarioId = "peace_battle_assignment_fixture";
        var war = c.Wars[0]; war.StartedDay = c.ElapsedDays - 1000; war.WarScore = 100;
        // A second engagement includes a supporting participant without a staff assignment.
        // Peace must release real battle participants, rather than only AssignedWarId matches.
        Battle(c, 992, war.Id, 7, Unit(c, 3, 0, 6, war.Id), Unit(c, 4, 1, 7, -1));
        var other = new StrategicWar { Id = 883, AttackerId = 0, DefenderId = 2 };
        c.Wars.Add(other);
        Battle(c, 993, other.Id, 1, Unit(c, 5, 0, 5, other.Id), Unit(c, 6, 2, 1, other.Id));
        return c;
    }

    static string Condition(Army army) => Snapshot(new { army.Id, army.NationId, army.ProvinceId,
        army.StateMapIndex, army.Latitude, army.Longitude, army.Divisions, army.Personnel,
        army.MaxPersonnel, army.Recovering, army.Strength, army.Organisation, army.Equipment,
        army.Cohesion, army.Experience, army.SupplyLevel });

    static string History(LandBattle battle) => Snapshot(new { battle.Id, battle.WarId,
        battle.ProvinceId, battle.AttackerId, battle.DefenderId, battle.StartedDay,
        battle.AttackerLosses, battle.DefenderLosses, battle.Attackers, battle.Defenders,
        battle.WithdrawnAttackers, battle.WithdrawnDefenders, battle.Rounds });

    static int PeaceOutcomes(int warId) => Warfare.OutcomeTallyByWar.TryGetValue(warId, out var outcomes)
        ? outcomes.GetValueOrDefault("Peace agreement") : 0;

    static void ResolvePeace(bool written, int proposer)
    {
        var c = Fixture(); var war = c.Wars[0];
        var name = (written ? "conference" : "standing terms") + ", proposer=" + proposer;
        var battles = c.Battles.Where(b => b.WarId == war.Id).ToArray();
        var participants = c.Armies.Where(a => battles.Any(b => b.Id == a.BattleId)).ToArray();
        var conditions = participants.ToDictionary(a => a.Id, Condition);
        var history = battles.ToDictionary(b => b.Id, History);
        var other = c.Battles.Single(b => b.WarId != war.Id);
        var otherArmies = c.Armies.Where(a => a.BattleId == other.Id).ToArray();
        var unrelated = Snapshot(new { Battle = other, Armies = otherArmies });
        var repairs = Warfare.RepairedBattleAssignments;
        var outcomes = PeaceOutcomes(war.Id);

        var beforeRefusal = Snapshot(new { c.Armies, c.Battles });
        if (written)
            Check(!PeaceConference.Resolve(c, war, new PeaceDemand { WarId = war.Id, ProposerId = 2, Reparations = true }),
                name + ": a non-belligerent's demand is refused");
        else
            PeaceNegotiation.Resolve(c, war, 2, PeaceTerms.Capitulate);
        Check(!war.Ended && Snapshot(new { c.Armies, c.Battles }) == beforeRefusal,
            name + ": refused peace leaves both engagements and all participants fighting");

        if (written)
        {
            war.WarScore = proposer == 0 ? 100 : -100;
            var demand = new PeaceDemand { WarId = war.Id, ProposerId = proposer, Reparations = true };
            Check(PeaceConference.BlockReason(c, war, demand).Length == 0,
                name + ": the actual conference demand is signable");
            Check(PeaceConference.Resolve(c, war, demand), name + ": the actual conference resolves");
        }
        else
        {
            Check(PeaceNegotiation.BlockReason(c, war, proposer, PeaceTerms.Capitulate).Length == 0,
                name + ": the actual standing term is signable");
            PeaceNegotiation.Resolve(c, war, proposer, PeaceTerms.Capitulate);
        }

        // These assertions run immediately after the real settlement, with no warfare tick
        // or reconciliation pass that could mask a missing peace-path release.
        Check(war.Ended && battles.All(b => b.Ended && b.WinnerId == -1 && b.Outcome == "Peace agreement") &&
            participants.All(a => c.Armies.Contains(a) && !a.InBattle && a.BattleId == -1),
            name + ": signing immediately releases attackers, defenders and unassigned support in every battle");
        Check(participants.All(a => Condition(a) == conditions[a.Id]) && battles.All(b => History(b) == history[b.Id]),
            name + ": signing preserves people, damage, position and recorded battle history");
        Check(!c.Wars[1].Ended && Snapshot(new { Battle = other, Armies = otherArmies }) == unrelated,
            name + ": another live war involving the same country keeps its battle and assignments");
        Check(Warfare.RepairedBattleAssignments == repairs && PeaceOutcomes(war.Id) == outcomes + battles.Length,
            name + ": each battle closes through the normal outcome path without delayed repair");

        var settled = Snapshot(new { c.Armies, c.Battles });
        if (written)
            Check(!PeaceConference.Resolve(c, war, new PeaceDemand { WarId = war.Id, ProposerId = proposer, Reparations = true }),
                name + ": an already ended war cannot sign twice");
        else
            PeaceNegotiation.Resolve(c, war, proposer, PeaceTerms.Capitulate);
        Check(Snapshot(new { c.Armies, c.Battles }) == settled && PeaceOutcomes(war.Id) == outcomes + battles.Length,
            name + ": repeated resolution neither changes formations nor counts the ending twice");
    }

    static void DestroyBattleTerrain()
    {
        var c = Fixture(); var battle = c.Battles[0]; var province = battle.ProvinceId;
        var attacker = c.Armies.Single(a => a.Id == battle.Attackers[0]);
        var defender = c.Armies.Single(a => a.Id == battle.Defenders[0]);
        var condition = Condition(attacker);
        var unrelatedBattles = c.Battles.Where(b => b.Id != battle.Id).ToArray();
        var unrelatedArmies = c.Armies.Where(a => a.BattleId != battle.Id).ToArray();
        var unrelated = Snapshot(new { Battles = unrelatedBattles, Armies = unrelatedArmies });
        var repairs = Warfare.RepairedBattleAssignments;

        Check(attacker.ProvinceId != province && defender.ProvinceId == province,
            "terrain fixture distinguishes an off-province attacker from the formation on destroyed ground");
        Check(c.DestroyProvinceTerrain(province), "the actual terrain destruction succeeds");
        Check(c.Armies.Contains(attacker) && !attacker.InBattle && Condition(attacker) == condition &&
            !c.Armies.Contains(defender),
            "destroying battle ground immediately frees surviving attackers while removing troops actually on it");
        Check(battle.Ended && battle.WinnerId == -1 && battle.Outcome == "Terrain destroyed" &&
            battle.AttackerLosses == 123 && battle.DefenderLosses == 456 && c.Battles.Contains(battle),
            "terrain termination retains a non-victory report and its recorded losses");
        Check(Snapshot(new { Battles = unrelatedBattles, Armies = unrelatedArmies }) == unrelated &&
            Warfare.RepairedBattleAssignments == repairs,
            "terrain destruction leaves other engagements intact and needs no assignment repair");
        var settled = Snapshot(new { c.Armies, c.Battles });
        Check(!c.DestroyProvinceTerrain(province) && Snapshot(new { c.Armies, c.Battles }) == settled,
            "destroying the same terrain twice does not repeat battle termination");
    }

    public static void Run()
    {
        foreach (var written in new[] { false, true })
            foreach (var proposer in new[] { 0, 1 }) ResolvePeace(written, proposer);
        DestroyBattleTerrain();
    }
}
