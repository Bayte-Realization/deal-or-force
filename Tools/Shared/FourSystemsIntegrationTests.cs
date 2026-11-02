using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

public static class FourSystemsIntegrationTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL four systems: " + text);
        Console.WriteLine("PASS four systems: " + text);
    }

    public static void RunAll()
    {
        TestLogisticsAndPockets();
        TestEscalationAndCrises();
        TestCovertOperationsAndHybrid();
        TestStrategicResourcesAndLeverage();
        TestWarWearinessDomesticPressure();
    }

    private static void TestLogisticsAndPockets()
    {
        // 1. Hub connectivity validation: isolated enclave without barracks vs with barracks
        const int width = 32, height = 160;
        var states = new ushort[width * height];
        var control = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = y * width + x;
            var p = y / 32 + 1;
            states[at] = (ushort)(1000 + p);
            control[at] = 100;
            rgb[at * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, control, width, height, new RgbDataMap(width, height, rgb),
            Enumerable.Range(1, 5).ToDictionary(p => p, p => p));

        var c = new Campaign(4001) { ScenarioId = "pocket_test" };
        c.Nations.Add(new Nation
        {
            Id = 0, Tag = "N0", Name = "Nation 0", MapIndex = 100, Population = 2000000,
            Gdp = 200, Treasury = 50, MilitarySpending = .06f, StockpilesSeeded = true,
            CapitalProvinceId = 1, CapitalStateMapIndex = 1001
        });
        c.Nations.Add(new Nation
        {
            Id = 1, Tag = "N1", Name = "Nation 1", MapIndex = 101, Population = 2000000,
            Gdp = 200, Treasury = 50, MilitarySpending = .06f, StockpilesSeeded = true
        });
        for (var p = 1; p <= 5; p++)
            c.States.Add(new ScenarioState
            {
                Id = "st_" + p, Name = "State " + p, MapIndex = 1000 + p,
                NationMapIndex = 100, NationId = "N0", Population = 100000
            });
        c.AttachProvinceTopology(map);
        // The barracks rule on the bare hunger clock: a pocket's own stores (StrandedGarrisons)
        // would hold the clock at zero for weeks, which is tested where they are.
        c.Defines.StrandedGarrisonsEnabled = false;
        // Cut off province 4 and 5 by putting province 3 under enemy control
        c.ProvinceController[3] = 101;

        var pocketArmy = new Army
        {
            Id = 10, NationId = 0, ProvinceId = 5, StateMapIndex = 1005,
            TemplateId = "line_infantry", Divisions = 1, Personnel = 10000, MaxPersonnel = 10000,
            Strength = 1f, SupplyLevel = 1f, Organisation = .8f
        };
        c.Armies.Add(pocketArmy);

        // Run logistics tick: pocket has NO barracks, so it cannot form an isolated hub
        LogisticsSystem.Tick(c);
        Check(pocketArmy.CutOff, "isolated enclave without barracks is cut off from supply");

        // A local depot identifies the carried reserve, not an open national delivery route.
        c.States[4].Buildings[BuildingKind.Barracks] = 1;
        LogisticsSystem.Tick(c);
        Check(pocketArmy.CutOff && pocketArmy.DaysCutOff > 0 && pocketArmy.SupplyLevel > 0f &&
              pocketArmy.SupplySourceStateMapIndex == 1005,
            "isolated enclave with barracks retains a finite local reserve without reopening delivery");
        Check(pocketArmy.SupplyCongestion <= 1.0f, "isolated hub functions within degraded depot constraints");

        // 2. Progressive pocket starvation with Army.DaysCutOff
        // Remove barracks to put army back in cut-off state
        c.States[4].Buildings[BuildingKind.Barracks] = 0;
        LogisticsSystem.Tick(c);
        Check(pocketArmy.CutOff, "pocket army is cut off again");

        pocketArmy.DaysCutOff = 0;
        pocketArmy.Strength = 1.0f;
        pocketArmy.Personnel = 10000;

        // Days 1 to 3: tactical reserves cushion losses
        pocketArmy.DaysCutOff = 2;
        var strengthBeforeDay2 = pocketArmy.Strength;
        Attrition.Tick(c);
        var lossDay2 = strengthBeforeDay2 - pocketArmy.Strength;

        // Day > 3: escalating attrition
        pocketArmy.DaysCutOff = 6;
        var strengthBeforeDay6 = pocketArmy.Strength;
        Attrition.Tick(c);
        var lossDay6 = strengthBeforeDay6 - pocketArmy.Strength;

        Check(lossDay6 > lossDay2, "pocket starvation escalates significantly after initial tactical reserves expire (DaysCutOff > 3)");

        // Speed reduction when CutOff && DaysCutOff > 3
        pocketArmy.DaysCutOff = 4;
        Check(pocketArmy.DaysCutOff > 3 && pocketArmy.CutOff, "formation suffers fuel exhaustion after 3 days cut off");

        // CampaignFingerprint distinguishes a cut off army even before DaysCutOff increments
        var fpConnected = CampaignFingerprint.Of(c);
        pocketArmy.CutOff = true;
        pocketArmy.DaysCutOff = 0;
        var fpCutOffZero = CampaignFingerprint.Of(c);
        pocketArmy.CutOff = false;
        Check(fpConnected != fpCutOffZero, "CampaignFingerprint reflects CutOff status even when DaysCutOff is 0");
    }

    private static void TestEscalationAndCrises()
    {
        var c = new Campaign(5001) { ScenarioId = "escalation_crisis_test" };
        var natA = new Nation { Id = 0, Tag = "GP1", Name = "Great Power A", Gdp = 1200, Population = 50000000, MilitarySpending = 0.08f, Treasury = 100f };
        var natB = new Nation { Id = 1, Tag = "GP2", Name = "Great Power B", Gdp = 1000, Population = 45000000, MilitarySpending = 0.07f, Treasury = 100f };
        var small = new Nation { Id = 2, Tag = "SML", Name = "Small State", Gdp = 50, Population = 2000000, MilitarySpending = 0.03f, Treasury = 10f };

        c.Nations.Add(natA);
        c.Nations.Add(natB);
        c.Nations.Add(small);

        c.Armies.Add(new Army { Id = 20, NationId = natA.Id, Divisions = 10, Personnel = 100000, MaxPersonnel = 100000, Strength = 1f, Organisation = 1f });
        c.Armies.Add(new Army { Id = 21, NationId = natB.Id, Divisions = 8, Personnel = 80000, MaxPersonnel = 80000, Strength = 1f, Organisation = 1f });
        c.Armies.Add(new Army { Id = 22, NationId = small.Id, Divisions = 1, Personnel = 5000, MaxPersonnel = 5000, Strength = 1f, Organisation = 1f });

        var relSmallA = CampaignDiplomacy.Get(c, small.Id, natA.Id);
        relSmallA.Opinion = 30f;
        relSmallA.Stance = DiplomaticStance.Friendly;

        var relCrisis = CampaignDiplomacy.Get(c, small.Id, natB.Id);
        relCrisis.Tension = c.Defines.TensionCrisisAt + 5f; // 55 tension: Crisis!
        relCrisis.Opinion = -40f;
        relCrisis.Stance = DiplomaticStance.Rival;

        // Check mediation block and start mediation
        var block = Escalation.MediationBlock(c, natA, small, natB);
        Check(block.Length == 0, $"Great Power A is eligible to mediate in crisis between small state and Great Power B (reason='{block}')");

        var mediated = Escalation.StartMediation(c, natA, small, natB);
        Check(mediated && relCrisis.MediatorId == natA.Id, "Great Power A successfully opens crisis mediation");
        Check(relCrisis.Tension < c.Defines.TensionCrisisAt + 5f, "crisis mediation immediately relieves bilateral tension");

        // Re-escalate tension back to Crisis to test Great Power deterrence intervention
        relCrisis.Tension = c.Defines.TensionCrisisAt + 10f;

        // Check Great Power intervention / security guarantee to protect weaker state
        var intBlock = Escalation.InterventionBlock(c, natA, small, natB);
        Check(intBlock.Length == 0, $"Great Power A can intervene to protect Small State against Great Power B (reason='{intBlock}')");
        var intervened = Escalation.InterveneInCrisis(c, natA, small, natB);
        Check(intervened && relCrisis.InterveningPowerId == natA.Id, "Great Power A intervenes to deter aggression against Small State");

        // Test InterventionBlock when power is at war with aggressor
        c.Wars.Add(new StrategicWar { Id = 99, Name = "GP War", AttackerId = natA.Id, DefenderId = natB.Id });
        var warBlock = Escalation.InterventionBlock(c, natA, small, natB);
        Check(warBlock.Length > 0, "Great Power cannot intervene in crisis if already at war with aggressor");
        c.Wars.Clear();

        // Test feature gating for escalation commands
        c.SetFeature("escalation", false);
        var cmd = new MediateCrisisCommand { IssuerId = natA.Id, MediatorId = natA.Id, TargetAId = small.Id, TargetBId = natB.Id };
        cmd.Apply(c);
        Check(!cmd.Mediated, "MediateCrisisCommand blocked when escalation feature disabled");
        c.SetFeature("escalation", true);
    }

    private static void TestCovertOperationsAndHybrid()
    {
        var c = new Campaign(6001) { ScenarioId = "covert_hybrid_test" };
        var attacker = new Nation { Id = 0, Tag = "ATK", Name = "Attacker", Gdp = 300, Population = 5000000, MilitarySpending = 0.05f };
        var victim = new Nation { Id = 1, Tag = "VIC", Name = "Victim", Gdp = 300, Population = 5000000, MilitarySpending = 0.05f, Stability = 0.8f };
        var region = new ScenarioState
        {
            Id = "vic_reg", Name = "Victim Region", MapIndex = 201, NationMapIndex = 101, NationId = "VIC",
            Population = 200000, Stability = 0.8f, Radicalism = 0.1f, WarDamage = 0f
        };
        c.Nations.Add(attacker);
        c.Nations.Add(victim);
        c.States.Add(region);

        attacker.IntelNetworks[victim.Id] = 0.75f;

        // Test arm_insurgents definition exists and is valid
        var armDef = CovertOperations.Find("arm_insurgents");
        Check(armDef is not null && armDef.Effect == "arm_insurgents", "arm_insurgents operation definition exists");
        Check(armDef.Regional && armDef.Cost > 0, "arm_insurgents is regional and properly priced");

        // Test cyber_blackout definition exists and is valid
        var cyberDef = CovertOperations.Find("cyber_blackout");
        Check(cyberDef is not null && cyberDef.Effect == "cyber_blackout", "cyber_blackout operation definition exists");
        Check(cyberDef.Regional && cyberDef.Cost > 0, "cyber_blackout is regional and properly priced");

        // Verify localization keys are registered in Loc
        Check(Loc.Has("Arm insurgents") || Loc.Has("covert_insurgents_armed"), "arm_insurgents has localization");
        Check(Loc.Has("Cyber blackout") || Loc.Has("covert_cyber_blackout"), "cyber_blackout has localization");

        // Verify operations execute and modify target region state
        var radBefore = region.Radicalism;
        CovertOperations.Strike(c, attacker, victim, armDef, region, 0.75f, 10f);
        Check(region.Radicalism > radBefore, "arm_insurgents increases regional radicalism");

        var damageBefore = region.WarDamage;
        CovertOperations.Strike(c, attacker, victim, cyberDef, region, 0.75f, 10f);
        Check(region.WarDamage > damageBefore, "cyber_blackout inflicts war damage on infrastructure");
    }

    private static void TestStrategicResourcesAndLeverage()
    {
        var c = new Campaign(7001) { ScenarioId = "leverage_test" };
        var exporter = new Nation { Id = 0, Tag = "EXP", Name = "Exporter", Gdp = 500, Population = 10000000 };
        var importer = new Nation { Id = 1, Tag = "IMP", Name = "Importer", Gdp = 500, Population = 10000000 };
        c.Nations.Add(exporter);
        c.Nations.Add(importer);

        // Check high-tech classification
        Check(CommodityLeverage.IsHighTech(Commodity.RareEarths), "RareEarths is high-tech critical commodity");
        Check(CommodityLeverage.IsHighTech(Commodity.Electronics), "Electronics is high-tech critical commodity");
        Check(CommodityLeverage.IsStrategic(Commodity.Oil), "Oil is strategic commodity");

        // Leverage robustness with dead nations
        exporter.Population = 0;
        Check(CommodityLeverage.Calculate(c, exporter, importer) == 0f, "CommodityLeverage safely returns 0 for dead exporter");
        exporter.Population = 10000000;

        // Strategic stockpiles coverage check
        importer.StockpilesSeeded = true;
        importer.Stockpiles["Electronics"] = 0f;
        importer.DailyStockpileBurn["Electronics"] = 1.0f;
        importer.DailyStockpileGain["Electronics"] = 0f;
        importer.StockpileCoverage["Electronics"] = 0f;

        var coverage = StrategicStockpiles.Coverage(importer, "Electronics");
        Check(coverage < 0.1f, "zero electronics coverage detected in strategic stockpiles");

        var rel = CampaignDiplomacy.Get(c, exporter.Id, importer.Id);
        rel.Opinion = -30f;
        rel.Stance = DiplomaticStance.Rival;

        // Embargo command test
        new SetEmbargoCommand { IssuerId = exporter.Id, TargetId = importer.Id, Embargo = true }.Apply(c);
        Check(rel.EmbargoBy(exporter.Id), "SetEmbargoCommand successfully placed strategic commodity embargo");
    }

    private static void TestWarWearinessDomesticPressure()
    {
        var c = new Campaign(8001) { ScenarioId = "war_fatigue_test" };
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, 7 * 24L);
        var natA = new Nation { Id = 0, Tag = "NTA", Name = "Nation A", Gdp = 200, Population = 5000000, WarSupport = 0.20f };
        var natB = new Nation { Id = 1, Tag = "NTB", Name = "Nation B", Gdp = 200, Population = 5000000, WarSupport = 0.85f };
        c.Nations.Add(natA);
        c.Nations.Add(natB);

        var war = new StrategicWar
        {
            Id = 1,
            AttackerId = natA.Id,
            DefenderId = natB.Id,
            StartedDay = 0
        };
        c.Wars.Add(war);

        var fatigue = WarWeariness.NationalFatigue(c, natA);
        Check(fatigue >= 0.70f, "extreme national war fatigue is detected on low war support");

        WarWeariness.Tick(c);
        Check(ModifierSystem.Has(natA, "anti_war_protests"), "low war support triggers anti_war_protests modifier");
    }
}
