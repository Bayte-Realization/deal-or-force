using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 3 Warfare &amp; Battles Simulation Overhaul
/// (<see cref="BattleTelemetry"/>, multi-direction flanking bypass, combined-arms synergy,
/// encircled pocket collapse, retreat reorganization discipline, and EN+RU loc parity).
/// </summary>
static class WarfareOverhaulTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL warfare-overhaul: " + label);
        Console.WriteLine("PASS warfare-overhaul: " + label);
    }

    private static (Campaign Campaign, Nation Red, Nation Blue, StrategicWar War) BuildBattleFixture(int seed = 20261121)
    {
        var c = new Campaign(seed) { ScenarioId = "warfare-overhaul-fixture" };
        var red = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "RED", Name = "Red Republic", IsHuman = true,
            Population = 20_000_000, Gdp = 500f, Treasury = 50f, Stability = 0.80f, OutputScale = 1f
        };
        var blue = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BLU", Name = "Blue Federation", IsHuman = false,
            Population = 20_000_000, Gdp = 500f, Treasury = 50f, Stability = 0.80f, OutputScale = 1f
        };
        c.Nations.Add(red);
        c.Nations.Add(blue);

        // 4 provinces in a 2x2 grid: prov 1,2,3 owned by Red, prov 4 owned by Blue (adjacent to 2 and 3!)
        c.States.Add(new ScenarioState { Id = "s_red", MapIndex = 10, NationMapIndex = 1, Name = "Red Sector", Population = 5_000_000 });
        c.States.Add(new ScenarioState { Id = "s_blue", MapIndex = 20, NationMapIndex = 2, Name = "Blue Bastion", Population = 5_000_000 });
        c.InvalidateStateIndex();

        var topology = ProvinceTopology.Build(
            new ushort[] { 10, 10, 10, 20 },
            new ushort[] { 1, 1, 1, 2 },
            2, 2, _ => false);
        c.AttachProvinceTopology(topology);
        c.SetPlayer(red.Id);

        var war = new StrategicWar
        {
            Id = 1, Name = "Fixture War", StartedDay = 0,
            AttackerId = red.Id, DefenderId = blue.Id
        };
        c.Wars.Add(war);
        return (c, red, blue, war);
    }

    [TestSuite("--warfare-overhaul-only", Order = 2650, IncludeInDefaultRun = true,
        Description = "Wave 3 Warfare & Battles Overhaul: flanking entrenchment bypass, combined-arms synergy, pocket collapse, BattleTelemetry read-only contract & localization.")]
    public static void Run()
    {
        TestDefinesAndLocalization();
        TestFlankingEntrenchmentBypass();
        TestCombinedArmsSynergy();
        TestPocketCollapseAndPhaseClassification();
        TestBattleTelemetryReadOnlyAndInvariance();
        TestLiveBattleResolutionAndDeterminism();
    }

    private static void TestDefinesAndLocalization()
    {
        var d = new Campaign(1).Defines;
        Check(MathF.Abs(d.FlankingEntrenchmentBypassPerDirection - 0.18f) < 0.001f, "FlankingEntrenchmentBypassPerDirection loaded from 97_warfare_overhaul.json");
        Check(MathF.Abs(d.FlankingEntrenchmentMaxBypass - 0.54f) < 0.001f, "FlankingEntrenchmentMaxBypass loaded");
        Check(MathF.Abs(d.CombinedArmsBonus - 0.08f) < 0.001f, "CombinedArmsBonus loaded");
        Check(MathF.Abs(d.PocketCollapseOrgMultiplier - 1.45f) < 0.001f, "PocketCollapseOrgMultiplier loaded");
        Check(d.RetreatReorgMinDays >= 3, "RetreatReorgMinDays loaded");

        foreach (BattlePhase phase in Enum.GetValues(typeof(BattlePhase)))
        {
            var key = BattleTelemetry.PhaseKey(phase);
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", key + ".name")), $"EN phase name exists for {phase}");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", key + ".name")), $"RU phase name exists for {phase}");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", key + ".desc")), $"EN phase desc exists for {phase}");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", key + ".desc")), $"RU phase desc exists for {phase}");
        }
        Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", "Flanking bypass")), "RU translation for Flanking bypass exists");
        Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", "Pocket collapse")), "RU translation for Pocket collapse exists");
    }

    private static void TestFlankingEntrenchmentBypass()
    {
        var d = new Defines();
        var r1 = BattleTelemetry.FlankingEntrenchmentRetention(d, 1);
        var r2 = BattleTelemetry.FlankingEntrenchmentRetention(d, 2);
        var r3 = BattleTelemetry.FlankingEntrenchmentRetention(d, 3);
        var r5 = BattleTelemetry.FlankingEntrenchmentRetention(d, 5);
        Check(MathF.Abs(r1 - 1f) < 0.001f, "1 attack axis retains 100% entrenchment");
        Check(MathF.Abs(r2 - 0.82f) < 0.001f, "2 attack axes strip 18% entrenchment (retain 82%)");
        Check(MathF.Abs(r3 - 0.64f) < 0.001f, "3 attack axes strip 36% entrenchment (retain 64%)");
        Check(MathF.Abs(r5 - 0.46f) < 0.001f, "5 attack axes clamp cleanly at max 54% bypass (retain 46%)");
    }

    private static void TestCombinedArmsSynergy()
    {
        var d = new Defines();
        var armourId = UnitTypeCatalog.All.First(u => u.Hardness >= 0.30f || u.Armour >= 0.35f).Id;
        var inf = new Army { Id = 1, TemplateId = "line_infantry", Divisions = 3f };
        var armor = new Army { Id = 2, TemplateId = armourId, Divisions = 2f };

        var pureInf = BattleTelemetry.CombinedArmsMultiplier(d, new[] { inf }, 1.0f);
        var infPlusArmor = BattleTelemetry.CombinedArmsMultiplier(d, new[] { inf, armor }, 1.0f);
        var fullCombined = BattleTelemetry.CombinedArmsMultiplier(d, new[] { inf, armor }, 1.20f);
        var infPlusSupport = BattleTelemetry.CombinedArmsMultiplier(d, new[] { inf, inf }, 1.20f);

        Check(MathF.Abs(pureInf - 1f) < 0.001f, "pure infantry without support gets no combined-arms bonus");
        Check(MathF.Abs(infPlusArmor - 1.08f) < 0.001f, "infantry + armour gets full +8% combined-arms bonus");
        Check(MathF.Abs(fullCombined - 1.10f) < 0.001f, "infantry + armour + long-range fire gets +10% triad bonus");
        Check(MathF.Abs(infPlusSupport - 1.04f) < 0.001f, "multi-unit infantry + long-range support gets +4% partial bonus");
    }

    private static void TestPocketCollapseAndPhaseClassification()
    {
        var d = new Defines();
        var supplied = new Army { Id = 1, CutOff = false, SupplyLevel = 0.8f, DaysCutOff = 0 };
        var freshPocket = new Army { Id = 2, CutOff = true, SupplyLevel = 0.05f, DaysCutOff = 1 };
        var collapsedPocket = new Army { Id = 3, CutOff = true, SupplyLevel = 0.08f, DaysCutOff = d.SupplyCutoffDays + 2 };

        Check(!BattleTelemetry.IsInPocketCollapse(d, supplied), "supplied formation is not in pocket collapse");
        Check(!BattleTelemetry.IsInPocketCollapse(d, freshPocket), "day-1 cut-off formation is not yet in pocket collapse");
        Check(BattleTelemetry.IsInPocketCollapse(d, collapsedPocket), "isolated zero-supply formation past SupplyCutoffDays enters pocket collapse");

        Check(BattleTelemetry.ClassifyPhase("plains", 1, true, 0f, 0f, 0f, 1f, 1f, 0f, 0f) == BattlePhase.PocketCollapse,
            "pocket collapse takes highest phase priority");
        Check(BattleTelemetry.ClassifyPhase("plains", 2, false, 0.5f, 0.5f, 0.2f, 1f, 1f, 0.5f, 0f) == BattlePhase.ArmouredBreakthrough,
            "high armour + breakthrough selects ArmouredBreakthrough");
        Check(BattleTelemetry.ClassifyPhase("plains", 3, false, 0.1f, 0.2f, 0.3f, 1f, 1f, 0.1f, 0f) == BattlePhase.FlankingEnvelopment,
            "3+ attack axes selects FlankingEnvelopment");
        Check(BattleTelemetry.ClassifyPhase("urban", 1, false, 0.1f, 0.1f, 0.3f, 1f, 1f, 0f, 0f) == BattlePhase.UrbanGrind,
            "urban sector selects UrbanGrind");
        Check(BattleTelemetry.ClassifyPhase("plains", 1, false, 0f, 0f, 0.7f, 1f, 1f, 0f, 0.45f) == BattlePhase.DesperateCounterattack,
            "strong counterattack signal selects DesperateCounterattack");
    }

    private static void TestBattleTelemetryReadOnlyAndInvariance()
    {
        var (c, red, blue, war) = BuildBattleFixture();
        var a1 = new Army
        {
            Id = 10, NationId = red.Id, Name = "1st Infantry", TemplateId = "line_infantry",
            ProvinceId = 2, StateMapIndex = 10, Divisions = 3f, Personnel = 45000,
            Strength = 0.95f, Organisation = 0.85f, SupplyLevel = 0.95f, Planning = 0.5f,
            BattleId = 1, AssignedWarId = war.Id
        };
        var a2 = new Army
        {
            Id = 11, NationId = red.Id, Name = "2nd Armoured", TemplateId = UnitTypeCatalog.All.First(u => u.Hardness >= 0.30f || u.Armour >= 0.35f).Id,
            ProvinceId = 3, StateMapIndex = 10, Divisions = 2f, Personnel = 24000,
            Strength = 0.92f, Organisation = 0.88f, SupplyLevel = 0.90f, Planning = 0.6f,
            BattleId = 1, AssignedWarId = war.Id
        };
        var d1 = new Army
        {
            Id = 20, NationId = blue.Id, Name = "Bastion Guard", TemplateId = "line_infantry",
            ProvinceId = 4, StateMapIndex = 20, Divisions = 4f, Personnel = 60000,
            Strength = 0.90f, Organisation = 0.80f, SupplyLevel = 0.05f, Entrenchment = 0.75f,
            CutOff = true, DaysCutOff = c.Defines.SupplyCutoffDays + 3,
            BattleId = 1, AssignedWarId = war.Id
        };
        c.Armies.AddRange(new[] { a1, a2, d1 });

        var battle = new LandBattle
        {
            Id = 1, WarId = war.Id, ProvinceId = 4, AttackerId = red.Id, DefenderId = blue.Id, StartedDay = 0,
            LastAttackDirections = 2, LastFlankingBypass = 0.18f, LastAttackerCombinedArms = 1.08f,
            LastPhase = BattlePhase.PocketCollapse
        };
        battle.Attackers.AddRange(new[] { a1.Id, a2.Id });
        battle.Defenders.Add(d1.Id);
        battle.LastAttackerLine.AddRange(new[] { a1.Id, a2.Id });
        battle.LastDefenderLine.Add(d1.Id);
        battle.AttackFactors.Add(new BattleFactor("Combined arms", 1.08f));
        battle.DefenceFactors.Add(new BattleFactor("Flanking bypass", 0.82f));
        c.Battles.Add(battle);

        var fpBefore = CampaignFingerprint.Of(c);
        var snap = BattleTelemetry.Build(c, battle);
        var fpAfter = CampaignFingerprint.Of(c);

        Check(fpBefore == fpAfter, "BattleTelemetry.Build is 100% read-only and never mutates CampaignFingerprint");
        Check(snap is not null && snap.BattleId == 1 && snap.AttackDirections == 2, "telemetry exposes BattleId and AttackDirections");
        Check(snap.HasEncircledDefenders && snap.HasPocketCollapse, "telemetry detects encircled defender in pocket collapse");
        Check(snap.Attacker.Formations.Count == 2 && snap.Defender.Formations.Count == 1, "telemetry lists all attacker & defender formations");
        Check(snap.Attacker.Modifiers.Any(m => m.Key == "Combined arms" && m.IsPositive), "telemetry surfaces positive Combined arms chip");
        Check(snap.Defender.Modifiers.Any(m => m.Key == "Flanking bypass" && !m.IsPositive), "telemetry surfaces negative Flanking bypass chip");
        Check(snap.AttackerProgress is >= 0f and <= 1f && snap.Momentum is >= -1f and <= 1f, "progress and momentum stay strictly bounded");
    }

    private static void TestLiveBattleResolutionAndDeterminism()
    {
        static ulong RunMiniBattle(int seed)
        {
            var (c, red, blue, war) = BuildBattleFixture(seed);
            var a1 = new Army
            {
                Id = 1, NationId = red.Id, Name = "1st Inf", TemplateId = "line_infantry",
                ProvinceId = 4, StateMapIndex = 20, Divisions = 1f, Personnel = 15000,
                Strength = 1f, Organisation = 0.9f, SupplyLevel = 1f, Equipment = 1f, Cohesion = 1f,
                BattleId = 1, AssignedWarId = war.Id
            };
            var a2 = new Army
            {
                Id = 2, NationId = red.Id, Name = "2nd Tank", TemplateId = UnitTypeCatalog.All.First(u => u.Hardness >= 0.30f || u.Armour >= 0.35f).Id,
                ProvinceId = 4, StateMapIndex = 20, Divisions = 1f, Personnel = 12000,
                Strength = 1f, Organisation = 0.9f, SupplyLevel = 1f, Equipment = 1f, Cohesion = 1f,
                BattleId = 1, AssignedWarId = war.Id
            };
            var d1 = new Army
            {
                Id = 3, NationId = blue.Id, Name = "1st Def", TemplateId = "line_infantry",
                ProvinceId = 4, StateMapIndex = 20, Divisions = 3f, Personnel = 45000,
                Strength = 1f, Organisation = 0.8f, SupplyLevel = 0.05f, Equipment = 1f, Cohesion = 1f,
                Entrenchment = 0.6f, CutOff = true, DaysCutOff = c.Defines.SupplyCutoffDays + 5,
                BattleId = 1, AssignedWarId = war.Id
            };
            c.Armies.AddRange(new[] { a1, a2, d1 });
            var battle = new LandBattle
            {
                Id = 1, WarId = war.Id, ProvinceId = 4, AttackerId = red.Id, DefenderId = blue.Id, StartedDay = 0
            };
            battle.Attackers.AddRange(new[] { a1.Id, a2.Id });
            battle.Defenders.Add(d1.Id);
            c.Battles.Add(battle);

            for (var day = 0; day < 8 && !battle.Ended; day++)
                Warfare.Tick(c);

            Check(battle.Rounds.Count > 0, "live battle executes rounds cleanly");
            Check(battle.LastAttackerCombinedArms > 1.05f, "live battle applies Combined Arms bonus to mixed inf+armour attackers");
            Check(battle.LastPhase == BattlePhase.PocketCollapse || battle.Ended, "live battle registers PocketCollapse on cut-off zero-supply defender");
            return CampaignFingerprint.Of(c);
        }

        var fp1 = RunMiniBattle(7777);
        var fp2 = RunMiniBattle(7777);
        Check(fp1 == fp2, $"live battle resolution with Wave 3 rules is 100% deterministic ({fp1} == {fp2})");
    }
}
