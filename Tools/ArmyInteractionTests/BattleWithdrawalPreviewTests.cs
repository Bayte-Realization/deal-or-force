using System;
using System.Linq;
using DealOrForce.Simulation;

static class BattleWithdrawalPreviewTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL withdrawal preview: " + label);
        Console.WriteLine("PASS withdrawal preview: " + label);
    }

    public static void Run()
    {
        // Keep this quote-only fixture independent of bootstrap's process-wide content
        // catalogs. It runs first in the full stand, before the other isolated fixtures.
        var c = PartialFrontWithdrawalTests.Fixture(divisions: 5);
        var attackerNation = c.NationById(0); var defenderNation = c.NationById(1);
        var map = c.ProvinceMap;
        var source = BorderFronts.Candidates(c, attackerNation.Id, defenderNation.Id).First(p =>
            map.Neighbors[p].Any(n => c.ProvinceController[n] == defenderNation.MapIndex &&
                map.Neighbors[n].Any(h => c.ProvinceController[h] == defenderNation.MapIndex && !map.IsWater(h))));
        var target = map.Neighbors[source].First(p => c.ProvinceController[p] == defenderNation.MapIndex &&
            map.Neighbors[p].Any(h => c.ProvinceController[h] == defenderNation.MapIndex && !map.IsWater(h)));
        var owners = (ushort[])c.ProvinceController.Clone();
        Army attacker = null, defender = null;
        LandBattle battle = null;
        void Reset()
        {
            Array.Copy(owners, c.ProvinceController, owners.Length);
            c.Armies.Clear(); c.Battles.Clear(); c.Wars[0].Ended = false;
            attacker = new Army { Id = 9001, NationId = attackerNation.Id, ProvinceId = source,
                BattleId = 999, Divisions = 5, Personnel = 50000, MaxPersonnel = 60000, Strength = .7f, Equipment = .8f };
            defender = new Army { Id = 9002, NationId = defenderNation.Id, ProvinceId = target,
                BattleId = 999, Divisions = 5, Personnel = 50000, MaxPersonnel = 60000, Strength = .6f, Equipment = .7f };
            c.Armies.Add(attacker); c.Armies.Add(defender);
            battle = new LandBattle { Id = 999, WarId = c.Wars[0].Id, ProvinceId = target,
                AttackerId = attackerNation.Id, DefenderId = defenderNation.Id };
            battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id); c.Battles.Add(battle);
        }
        BattleWithdrawalQuote Read(Army army, float share = 1, int destination = -1)
        {
            var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
            var quote = BattleWithdrawalPreview.Read(c, army.NationId, army.Id, share, destination);
            Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random, "quote is read-only");
            return quote;
        }
        foreach (var attacking in new[] { false, true })
        foreach (var share in new[] { .5f, 1f, .9995f })
        {
            Reset(); var army = attacking ? attacker : defender;
            var quote = Read(army, share);
            Check(quote.Available && quote.Attacking == attacking && quote.Leaving + quote.Remaining == 5 &&
                (attacking ? quote.Destination == -1 : map.Neighbors[target].Contains(quote.Destination)),
                "quote describes legal side, destination and full/partial split");
            var command = new WithdrawArmyFromBattleCommand { IssuerId = army.NationId, ArmyId = army.Id, Share = share };
            command.Apply(c);
            var leaving = c.Armies.Find(a => a.Id == command.WithdrawnArmyId);
            Check(command.Withdrawn && leaving is not null && leaving.Divisions == quote.Leaving &&
                leaving.ProvinceId == (attacking ? source : quote.Destination) &&
                (quote.Remaining == 0 ? !army.InBattle : army.InBattle && army.Divisions == quote.Remaining),
                "actual command exactly matches quoted withdrawal");
            Check(leaving.Strength == (attacking ? .7f : .6f) && leaving.Equipment == (attacking ? .8f : .7f),
                "quoted exit does not heal the formation");
        }
        Reset();
        foreach (var neighbor in map.Neighbors[target]) c.ProvinceController[neighbor] = (ushort)attackerNation.MapIndex;
        var blocked = Read(defender);
        Check(!blocked.Available && blocked.Refusal == "No friendly line of retreat", "closed pocket explains refusal");
        var home = map.Neighbors[target].First(p => p != source && !map.IsWater(p));
        c.SetProvinceController(home, (ushort)defenderNation.MapIndex);
        Check(Read(defender, destination: home) is { Available: true } open && open.Destination == home,
            "paused corridor change is reflected immediately");
        var foreign = BattleWithdrawalPreview.Read(c, attackerNation.Id, defender.Id);
        Check(!foreign.Available && foreign.Refusal == "Order not authorised" && foreign.Destination == -1,
            "foreign issuer cannot inspect a retreat route");
        foreach (var invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 1.01f })
            Check(!Read(defender, invalid).Available, "invalid share is refused");
        defender.Divisions = 1;
        Check(!Read(defender, .5f).Available, "single division cannot be split");
        var hadBinding = c.Defines.FeatureCommands.TryGetValue(nameof(WithdrawArmyFromBattleCommand), out var previousBinding);
        try
        {
            c.Defines.FeatureCommands[nameof(WithdrawArmyFromBattleCommand)] = "withdrawal_preview_fixture";
            c.SetFeature("withdrawal_preview_fixture", false);
            Check(Read(attacker).Refusal == "This command is disabled in this campaign.", "disabled command is explained without logging");
        }
        finally
        {
            // Restore the command binding even if an assertion fails.
            if (hadBinding) c.Defines.FeatureCommands[nameof(WithdrawArmyFromBattleCommand)] = previousBinding;
            else c.Defines.FeatureCommands.Remove(nameof(WithdrawArmyFromBattleCommand));
        }
        c.Wars[0].Ended = true;
        Check(Read(attacker).Refusal == "War ended", "stale battle cannot offer a withdrawal after war ended");
        c.Wars[0].Ended = false;
        battle.Attackers.Clear();
        Check(Read(attacker).Refusal == "Not in battle", "stale BattleId cannot grant roster membership");
    }
}

