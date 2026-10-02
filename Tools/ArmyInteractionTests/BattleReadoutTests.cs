using System;
using System.Text.Json;
using DealOrForce.Simulation;

static class BattleReadoutTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    public static void Run()
    {
        var battle = new LandBattle { AttackerId = 1, DefenderId = 2 };
        Check(!BattleReadout.Assess(battle, .1f).Known, "no invented forecast before first round");
        var round = new LandBattle.Round { AttackerOrganisation = .8f, DefenderOrganisation = .8f,
            AttackerDamage = .1f, DefenderDamage = .1f };
        battle.Rounds.Add(round);
        Check(Near(BattleReadout.Assess(battle, .1f).AttackerShare, .5f), "balanced endurance is 50 percent");
        round.AttackerDamage = .2f;
        var advantage = BattleReadout.Assess(battle, .1f);
        Check(advantage.AttackerShare > .6f, "legacy damage direction is fire dealt, not received");
        Check(Near(advantage.ShareFor(true) + advantage.ShareFor(false), 1), "defender sees complementary balance");
        round.HasOrganisationTelemetry = true;
        round.AttackerOrganisationSpent = .2f; round.DefenderOrganisationSpent = .05f;
        Check(BattleReadout.Assess(battle, .1f).AttackerShare < .25f, "actual organisation loss overrides nominal damage");
        round.AttackerOrganisationSpent = round.DefenderOrganisationSpent = 0;
        Check(!BattleReadout.Assess(battle, .1f).Known, "zero actual loss does not fall back to legacy damage");
        round.AttackerOrganisationSpent = .1f; round.DefenderOrganisationSpent = .1f;
        for (var i = 0; i < 3; i++) battle.Rounds.Add(new LandBattle.Round
        { AttackerOrganisation = .8f, DefenderOrganisation = .8f, HasOrganisationTelemetry = true,
            AttackerOrganisationSpent = .1f, DefenderOrganisationSpent = .1f });
        round.AttackerOrganisationSpent = 99;
        Check(Near(BattleReadout.Assess(battle, .1f).AttackerShare, .5f), "only recent three rounds affect trend");
        battle.Rounds[^1].AttackerOrganisation = float.NaN;
        Check(float.IsFinite(BattleReadout.Assess(battle, .1f).AttackerShare), "malformed telemetry is safe to display");
        battle.Ended = true; battle.WinnerId = 2;
        Check(Near(BattleReadout.Assess(battle, .1f).ShareFor(false), 1), "finished result is actual winner");
        battle.WinnerId = -1;
        Check(!BattleReadout.Assess(battle, .1f).Known, "withdrawal without winner has no fake result");
        var a = new Army { Divisions = 1, Organisation = 1, Equipment = .5f, Cohesion = .8f };
        var b = new Army { Divisions = 3, Organisation = .2f, Equipment = 1, Cohesion = .4f };
        var condition = BattleReadout.Summarise(new[] { a, b, new Army { Divisions = 0 }, null });
        Check(Near(condition.Organisation, .4f) && Near(condition.Equipment, .875f), "condition weighted by represented divisions");
        Check(condition.Formations == 2 && Near(condition.Cohesion, .5f), "dead formations excluded and cohesion preserved");
        Check(BattleReadout.Summarise(Array.Empty<Army>()).Formations == 0, "empty roster handled");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var before = JsonSerializer.Serialize(round, options);
        BattleReadout.Assess(battle, .1f); BattleReadout.Summarise(new[] { a, b });
        Check(before == JsonSerializer.Serialize(round, options), "readout does not mutate combat state");
        var loaded = JsonSerializer.Deserialize<LandBattle.Round>(before, options);
        Check(loaded.HasOrganisationTelemetry && Near(loaded.DefenderOrganisationSpent, .1f), "new telemetry round-trips through save JSON");
        var legacy = JsonSerializer.Deserialize<LandBattle.Round>("{\"AttackerDamage\":0.2,\"DefenderDamage\":0.1}", options);
        Check(!legacy.HasOrganisationTelemetry, "old save remains compatible");
    }
}
