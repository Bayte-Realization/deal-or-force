using System;
using System.Linq;
using DealOrForce.Simulation;

static class Backlog69Tests
{
    public static void Run(Campaign c)
    {
        var failures = 0;
        void Check(bool ok, string text) { Console.WriteLine((ok ? "PASS " : "FAIL ") + text); if (!ok) failures++; }
        var n = c.NationByTag("KOR");
        // With public debt on, an overdraft is carried as debt (SovereignFinance.NormalizeAccounts),
        // so what an effect must get right is the country's position - cash less debt - not
        // which of the two columns the money ends up in.
        float Position() => n.Treasury - n.PublicDebt;
        void Overdrawn() { n.PublicDebt = 0; n.Treasury = -10; }
        Overdrawn();
        ScriptedEffects.From(("treasury", -2f)).Apply(c, n);
        Check(Math.Abs(Position() + 12) < .0001f, "negative cash event preserves debt and charges its cost");
        Overdrawn();
        ScriptedEffects.From(("treasury_percent", -.1f)).Apply(c, n);
        Check(Math.Abs(Position() + 11) < .0001f, "percentage cost cannot forgive debt");
        Overdrawn();
        ScriptedEffects.From(("treasury_percent", .1f)).Apply(c, n);
        Check(Math.Abs(Position() + 9) < .0001f, "percentage income reduces debt instead of increasing it");
        n.PublicDebt = 0;
        n.Treasury = 10;
        ScriptedEffects.From(("treasury_percent", -.1f)).Apply(c, n);
        Check(Math.Abs(n.Treasury - 9) < .0001f, "positive treasury percent semantics unchanged");
        ScriptedEffects.From(("treasury_set", -5f)).Apply(c, n);
        Check(Math.Abs(Position() + 5) < .0001f, "explicit signed treasury assignment supported");

        c.Construction.Clear();
        c.Construction.Add(new ConstructionProject { Id = 901, NationId = n.Id, Cost = 1, Paid = .4f });
        c.Construction.Add(new ConstructionProject { Id = 902, NationId = n.Id, Cost = 2, Paid = .2f });
        var cash = n.Treasury;
        new CancelConstructionCommand { IssuerId = c.NationByTag("USA").Id, ProjectId = 902 }.Apply(c);
        Check(c.Construction.Count == 2, "cannot cancel another country's construction");
        new CancelConstructionCommand { IssuerId = n.Id, ProjectId = 902 }.Apply(c);
        new CancelConstructionCommand { IssuerId = n.Id, ProjectId = 902 }.Apply(c);
        Check(c.Construction.Count == 1 && c.Construction[0].Id == 901 && n.Treasury == cash,
            "cancel selected project only, no refund exploit or repeated cancellation");

        c.Wars.Clear(); c.Battles.Clear();
        c.Armies.RemoveAll(a => a.NationId == n.Id);
        var region = c.StatesOf(n).First();
        Check(MilitiaCore.Found(c, n, region), "militia fixture raised");
        var militia = c.Armies.Last(a => a.NationId == n.Id);
        n.MilitiaQuietSince[militia.Id] = c.ElapsedDays - 60;
        c.Wars.Add(new StrategicWar { Id = 9901, AttackerId = n.Id, DefenderId = c.NationByTag("USA").Id });
        c.MarkMapChanged();
        MilitiaCore.Tick(c);
        Check(c.Armies.Contains(militia) && !n.MilitiaQuietSince.ContainsKey(militia.Id),
            "new distant war cancels militia stand-down instead of dissolving it");
        c.Wars[0].Ended = true; c.MarkMapChanged();
        MilitiaCore.Tick(c);
        Check(c.Armies.Contains(militia), "peace starts a fresh demobilisation grace period");
        n.MilitiaQuietSince[militia.Id] = c.ElapsedDays - MilitiaCore.Weights.StandDownDays - 1;
        MilitiaCore.Tick(c);
        Check(!c.Armies.Contains(militia), "militia demobilises after sustained peace");

        var movement = c.NationByTag("HMS"); var parent = c.NationByTag("PSE");
        var land = c.States.First(s => s.Id == "ps_wbk");
        var inheritedGdp = RegionalStatistics.EstimatedGdp(c, land);
        var parentGdp = parent.Gdp;
        var movementCash = movement.Treasury;
        MovementRules.TakeRegion(c, movement, land, parent);
        Console.WriteLine($"STATEHOOD GDP {movement.Gdp:0.000} expected region {inheritedGdp:0.000}, population {movement.Population}, scale {movement.OutputScale:0.000}, parent scale {parent.OutputScale:0.000}");
        Check(movement.Gdp >= inheritedGdp * .99f && movement.Population >= land.Population,
            "new state receives its regional economy immediately, not the old movement budget");
        Check(Math.Abs(parent.Gdp + movement.Gdp - parentGdp) <= .011f && movement.Treasury == movementCash,
            "statehood transfers economic base without minting cash or duplicating parent GDP");
        Check(Math.Abs(movement.OutputScale - parent.OutputScale) < .001f,
            "first region retains its economic calibration");
        c.Wars.Clear(); c.Battles.Clear(); c.Construction.Clear(); c.MarkMapChanged();
        c.ConfigureSinglePlayer(movement.Id); c.IsPaused = false;
        for (var day = 0; day < 28; day++) c.TickDay();
        Console.WriteLine($"STATEHOOD day28 GDP {movement.Gdp:0.000}, tax {movement.WeeklyTaxIncome:0.000}, balance {movement.WeeklyBalance:0.000}, cash {movement.Treasury:0.000}");
        Check(float.IsFinite(movement.Treasury) && movement.WeeklyTaxIncome > 0 && movement.Gdp > inheritedGdp * .5f,
            "new state's real monthly economy retains production and collects taxes");
        if (failures > 0) throw new Exception($"{failures} backlog regressions failed");
    }
}
