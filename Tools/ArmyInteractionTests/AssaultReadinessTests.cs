using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class AssaultReadinessTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL assault readiness: " + label);
        Console.WriteLine("PASS assault readiness: " + label);
    }

    public static void Run()
    {
        ExhaustedFormationDoesNotStartAnAssault();
        PausedPlanDoesNotJoinNextDoorsAssault();
        ConditionRecoversWithoutLosingTheOrder();
        OffensiveReinforcementsUseTheSameMinimums();
        DefenceAndEmptyCountryRemainAvailable();
        DirectOrderPreservesPlayerIntent();
        BoundariesAndModFloors();
        AssessmentIsReadOnly();
        ConfigurationIsAuthoritative();
    }

    static void ExhaustedFormationDoesNotStartAnAssault()
    {
        foreach (var supply in new[] { .05f, 1f })
        {
            var c = Fixture();
            var army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
            army.SupplyLevel = supply;
            army.Organisation = supply < 1 ? 1 : .12f;
            Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
            Warfare.Tick(c);
            Check(!army.InBattle && c.Battles.Count == 0,
                supply < 1 ? "starved formation does not begin a defended assault" :
                    "disorganised formation does not begin a defended assault");
        }
    }

    static void PausedPlanDoesNotJoinNextDoorsAssault()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var paused = Unit(c, 1, 0, 2, ArmyOrder.AdvanceFront);
        paused.PlanActive = false;
        paused.Planning = 0;
        Unit(c, 2, 1, 3, ArmyOrder.DefendFront);
        Warfare.Tick(c);
        Check(attacker.InBattle && !paused.InBattle && !paused.PlanActive,
            "a paused formation is not pulled into a neighbouring offensive");
    }

    static void ConditionRecoversWithoutLosingTheOrder()
    {
        foreach (var order in new[] { ArmyOrder.AdvanceFront, ArmyOrder.Spearhead })
        {
            var c = Fixture();
            var army = Unit(c, 0, 0, 2, order);
            Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
            army.SupplyLevel = .10f;
            var random = c.Random.State;
            Warfare.Tick(c);
            Check(!army.InBattle && army.PlanActive && army.OrderKind == order &&
                army.OrderProvinceId == 3 && army.AssignedWarId == 883 && army.ReadyDay == 0,
                order + " waits without deleting its plan or imposing a fake recovery delay");
            Check(c.Random.State == random, order + " waiting consumes no random numbers");
            army.SupplyLevel = 1;
            Warfare.Tick(c);
            Check(c.Battles.Count == 1 && c.Battles[0].Attackers.Contains(army.Id),
                order + " resumes at the next review after the stores recover");
        }
    }

    static void OffensiveReinforcementsUseTheSameMinimums()
    {
        foreach (var condition in new[] { "supply", "organisation", "mod supply", "mod organisation" })
        {
            var c = Fixture();
            var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
            var reserve = Unit(c, 1, 0, 2, ArmyOrder.Spearhead);
            var defender = Unit(c, 2, 1, 3, ArmyOrder.DefendFront);
            if (condition == "supply") reserve.SupplyLevel = .1f;
            if (condition == "organisation") reserve.Organisation = .1f;
            if (condition == "mod supply") { reserve.SupplyLevel = .4f; c.Defines.AutonomousAssaultMinimumSupply = .5f; }
            if (condition == "mod organisation") { reserve.Organisation = .4f; c.Defines.AutonomousAssaultMinimumOrganisation = .5f; }
            var battle = new LandBattle { Id = 884, WarId = 883, ProvinceId = 3,
                AttackerId = 0, DefenderId = 1, StartedDay = c.ElapsedDays };
            battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id);
            attacker.BattleId = defender.BattleId = battle.Id;
            c.Battles.Add(battle);
            Warfare.Tick(c);
            Check(!reserve.InBattle && !battle.Attackers.Contains(reserve.Id) && battle.AttackerReinforcements == 0,
                condition + " floor also protects an offensive reinforcement");
        }
    }

    static void DefenceAndEmptyCountryRemainAvailable()
    {
        var c = Fixture();
        var attacker = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        var defender = Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
        var defensiveReserve = Unit(c, 2, 1, 4, ArmyOrder.ReserveFront);
        defensiveReserve.PlanActive = false;
        var battle = new LandBattle { Id = 884, WarId = 883, ProvinceId = 3,
            AttackerId = 0, DefenderId = 1, StartedDay = c.ElapsedDays };
        battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id);
        attacker.BattleId = defender.BattleId = battle.Id;
        c.Battles.Add(battle);
        c.Defines.AutonomousAssaultMinimumSupply = 1f;
        c.Defines.AutonomousAssaultMinimumOrganisation = 1f;
        Warfare.Tick(c);
        Check(battle.Defenders.Contains(defensiveReserve.Id) && battle.DefenderReinforcements == 1,
            "a paused defensive reserve still responds; offensive mod thresholds do not gate defence");

        c = Fixture();
        var marcher = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        marcher.SupplyLevel = .05f; marcher.Organisation = .12f; marcher.CutOff = true;
        Warfare.Tick(c);
        Check(marcher.MoveProgress > 0 && c.Battles.Count == 0,
            "a depleted isolated formation can still march into empty ground, not freeze its escape");
    }

    static void DirectOrderPreservesPlayerIntent()
    {
        var c = Fixture();
        c.Nations[0].IsHuman = true;
        var army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        army.DirectOrder = true; army.SupplyLevel = .05f; army.Organisation = .12f;
        Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
        var assessment = AssaultReadiness.Assess(c, army);
        Check(assessment.Ready && assessment.Reason == AssaultReadiness.Reason.DirectOrder &&
            assessment.Supply == .05f && assessment.Organisation == .12f,
            "a direct order explicitly overrides condition floors without concealing poor condition");
        Warfare.Tick(c);
        Check(c.Battles.Count == 1 && c.Battles[0].Attackers.Contains(army.Id),
            "the actual battle path honours a risky direct player order");

        c = Fixture(); c.Nations[0].IsHuman = true;
        army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        army.DirectOrder = true; army.PlanActive = false;
        Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.PlanPaused,
            "an explicit direct order does not unpause a paused plan");
        Warfare.Tick(c);
        Check(c.Battles.Count == 0, "the battle path also preserves a paused human plan");
        army.PlanActive = true; army.ReadyDay = c.ElapsedDays + 2;
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.Recovering &&
            AssaultReadiness.Assess(c, army).RecoveryDays == 2,
            "a direct order does not erase the existing recovery interval");
        Warfare.Tick(c);
        Check(c.Battles.Count == 0, "a recovering direct order cannot prematurely restart combat");
    }

    static void BoundariesAndModFloors()
    {
        var c = Fixture();
        var army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        army.Organisation = .16f;
        Check(!AssaultReadiness.Assess(c, army).Ready, "organisation at the existing floor is insufficient");
        army.Organisation = .16001f; army.SupplyLevel = .20f;
        Check(!AssaultReadiness.Assess(c, army).Ready, "supply at the existing floor is insufficient");
        army.SupplyLevel = .20001f;
        Check(AssaultReadiness.Assess(c, army).Ready, "both conditions just above the existing floors are sufficient");
        c.Defines.AutonomousAssaultMinimumOrganisation = .5f;
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.LowOrganisation &&
            AssaultReadiness.Assess(c, army).MinimumOrganisation == .5f,
            "modded organisation floor is displayed and used by the assessment");
        army.Organisation = 1; c.Defines.AutonomousAssaultMinimumSupply = .6f;
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.LowSupply &&
            AssaultReadiness.Assess(c, army).MinimumSupply == .6f,
            "modded supply floor is displayed and used by the assessment");
        army.Organisation = army.SupplyLevel = 0;
        c.Defines.AutonomousAssaultMinimumOrganisation = c.Defines.AutonomousAssaultMinimumSupply = 0;
        Check(AssaultReadiness.Assess(c, army).Ready, "a mod can disable both health floors with zero");
        Unit(c, 1, 1, 3, ArmyOrder.DefendFront);
        Warfare.Tick(c);
        Check(c.Battles.Count == 1, "disabled floors reach the actual assault path, not only the UI");

        c = Fixture(); army = Unit(c, 0, 0, 2, ArmyOrder.AdvanceFront);
        c.Defines.AutonomousAssaultMinimumOrganisation = float.NaN;
        c.Defines.AutonomousAssaultMinimumSupply = float.PositiveInfinity;
        var invalid = AssaultReadiness.Assess(c, army);
        Check(invalid.MinimumOrganisation == .16f && invalid.MinimumSupply == .20f,
            "nonfinite mod thresholds safely fall back without modifying the defines");
        c.Defines.AutonomousAssaultMinimumOrganisation = -5;
        c.Defines.AutonomousAssaultMinimumSupply = 5;
        invalid = AssaultReadiness.Assess(c, army);
        Check(invalid.MinimumOrganisation == 0 && invalid.MinimumSupply == 1,
            "out-of-range mod thresholds are clamped to meaningful condition ranges");
        army.Organisation = float.NaN;
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.Unavailable,
            "nonfinite unit condition never starts a silent autonomous assault");
        army.Organisation = 1; army.SupplyLevel = float.NegativeInfinity; army.DirectOrder = true;
        Check(!AssaultReadiness.Assess(c, army).Ready,
            "a direct order cannot propagate an invalid condition value");
        army.SupplyLevel = 1; army.OrderKind = ArmyOrder.DefendFront;
        Check(AssaultReadiness.Assess(c, army).Reason == AssaultReadiness.Reason.NoAssaultOrder,
            "a defensive order is not advertised as an assault plan");
        Check(!AssaultReadiness.Assess(null, army).Ready && !AssaultReadiness.Assess(c, null).Ready,
            "missing contexts are safe readouts");
    }

    static void AssessmentIsReadOnly()
    {
        var c = Fixture();
        var army = Unit(c, 0, 0, 2, ArmyOrder.Spearhead);
        army.SupplyLevel = .1f;
        var fingerprint = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var before = JsonSerializer.Serialize(army, new JsonSerializerOptions { IncludeFields = true });
        for (var i = 0; i < 100; i++) AssaultReadiness.Assess(c, army);
        Check(before == JsonSerializer.Serialize(army, new JsonSerializerOptions { IncludeFields = true }) &&
            CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
            "repeated interface assessments leave orders, authoritative fingerprint and RNG unchanged");
        var first = AssaultReadiness.Assess(c, army);
        army.SupplyLevel = 1;
        Check(first.Reason == AssaultReadiness.Reason.LowSupply && AssaultReadiness.Assess(c, army).Ready,
            "the readout is immutable and a fresh read observes same-day recovery");
    }

    static void ConfigurationIsAuthoritative()
    {
        var c = Fixture();
        var baseline = CampaignFingerprint.Of(c);
        c.Defines.AutonomousAssaultMinimumOrganisation = .3f;
        Check(CampaignFingerprint.Of(c) != baseline,
            "a changed organisation floor is visible to peer checks before any battle starts");
        c.Defines.AutonomousAssaultMinimumOrganisation = .16f;
        c.Defines.AutonomousAssaultMinimumSupply = .4f;
        Check(CampaignFingerprint.Of(c) != baseline,
            "a changed supply floor is visible to peer checks before any battle starts");
        c.Defines.AutonomousAssaultMinimumSupply = .20f;
        Check(CampaignFingerprint.Of(c) == baseline,
            "restoring both defaults restores the authoritative fingerprint");

        // Same fixture and current catalogue golden as ClaudeStateFingerprintTests.
        // Counterfactual verified 21 October 2026 against the 20 October probe assembly:
        // removing ONLY culture_union from Defines.Features and MechanicFeatures.BuiltIns
        // restores 0x8A569EFA7BB8A70D exactly. Also removing the FeatureCommands bindings
        // CommissionNewFleetCommand and ConsolidateSubjectsCommand restores the earlier
        // 0xDA7C6F9772777A1D. The feature and command tables are authoritative; default
        // assault floors still add no bytes. Keep a single strict golden.
        c = new Campaign(112017) { ScenarioId = "living_world_fingerprint_fixture" };
        for (var id = 0; id < 3; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 501 + id, Tag = "FP" + id,
                Name = "Fingerprint country " + id, Population = 1_000_000, Gdp = 100, Treasury = 40 });
        c.Armies.Add(new Army { Id = 7, NationId = 0, ProvinceId = 3, Divisions = 1,
            Strength = 1, Organisation = 1 });
        c.Wars.Add(new StrategicWar { Id = 12, AttackerId = 0, DefenderId = 1 });
        var actual = CampaignFingerprint.Of(c);
        Check(actual == 0xE51AA42BB91EDE15UL,
            $"default assault floors preserve the current catalogue golden fingerprint (got 0x{actual:X16})");
    }

    static Army Unit(Campaign c, int id, int nation, int province, ArmyOrder order)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], OrderKind = order,
            OrderProvinceId = nation == 0 ? 3 : -1, AssignedWarId = c.Wars[0].Id,
            Latitude = centre.Latitude, Longitude = centre.Longitude,
            Divisions = 1, Organisation = 1, SupplyLevel = 1, Strength = 1,
            Planning = 1, PlanActive = true, OrderIssuedDay = -100 };
        c.Armies.Add(army);
        return army;
    }

    static Campaign Fixture()
    {
        const int width = 128, height = 64;
        var states = new ushort[width * height];
        var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = x / 32 + 1;
            var index = y * width + x;
            states[index] = (ushort)(3000 + p);
            controllers[index] = (ushort)(p < 3 ? 500 : 501);
            rgb[index * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 4).ToDictionary(p => p, p => p));
        var c = new Campaign(20261014) { ScenarioId = "assault_readiness_fixture" };
        c.Defines.HoursPerTick = 24;
        c.Defines.FrontStaffCallsUpIdle = false;
        for (var id = 0; id < 2; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 500 + id, Tag = "AR" + id,
                Name = "Country " + id, Population = 10_000_000, Gdp = 100, Treasury = 20 });
        for (var p = 1; p <= 4; p++)
            c.States.Add(new ScenarioState { Id = "assault_region_" + p, Name = "Region " + p,
                MapIndex = 3000 + p, NationMapIndex = p < 3 ? 500 : 501,
                NationId = p < 3 ? "AR0" : "AR1", Population = 1_000_000 });
        c.AttachProvinceTopology(map);
        c.Armies.Clear();
        c.Wars.Add(new StrategicWar { Id = 883, AttackerId = 0, DefenderId = 1,
            LastFrontDay = c.ElapsedDays, FrontDirty = false });
        return c;
    }
}
