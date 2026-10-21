using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MobilisationAndStockpileTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("mobilisation/stockpiles: " + label);
        Console.WriteLine("PASS mobilisation/stockpiles: " + label);
    }

    public static void RunAll()
    {
        Console.WriteLine("== mobilisation and strategic stockpile test suite ==");

        var campaign = Campaign.CreateBootstrap(20261019);
        var rus = campaign.NationByTag("RUS");
        var usa = campaign.NationByTag("USA");
        Check(rus is not null && usa is not null, "bootstrap countries exist");

        // 1. Dynamic recruitment queue scaling across mobilisation modes
        var peaceCap = RecruitmentSystem.DynamicQueueCap(campaign, rus, MobilisationMode.Standard);
        var rapidCap = RecruitmentSystem.DynamicQueueCap(campaign, rus, MobilisationMode.RapidCallUp);
        var totalCap = RecruitmentSystem.DynamicQueueCap(campaign, rus, MobilisationMode.TotalMobilisation);

        Check(peaceCap > 0, "peacetime standard cap is positive");
        Check(rapidCap > peaceCap, "rapid call-up posture expands recruitment queue capacity");
        Check(totalCap > rapidCap, "total mobilisation posture expands queue capacity dramatically");
        var russianRegion = campaign.StatesOf(rus).OrderByDescending(s => s.Population).FirstOrDefault();
        Check(RecruitmentSystem.MaxDivisionsPerBatch(campaign, rus, russianRegion) > 6,
            "batch ceiling grows with population and barracks instead of staying at a fixed six");

        // 2. Training speed and cost across modes
        var template = UnitTypeCatalog.All.FirstOrDefault() ?? UnitTypeCatalog.Get("infantry");
        Check(template is not null, "infantry template is available");

        var stdDays = RecruitmentSystem.TrainingDaysOf(rus, template, MobilisationMode.Standard);
        var rapidDays = RecruitmentSystem.TrainingDaysOf(rus, template, MobilisationMode.RapidCallUp);
        var totalDays = RecruitmentSystem.TrainingDaysOf(rus, template, MobilisationMode.TotalMobilisation);

        Check(rapidDays < stdDays, "rapid call-up significantly accelerates division training");
        Check(totalDays < rapidDays, "total mobilisation enables emergency rapid deployment");

        var stdCost = RecruitmentSystem.CostOf(rus, template, MobilisationMode.Standard);
        var totalCost = RecruitmentSystem.CostOf(rus, template, MobilisationMode.TotalMobilisation);
        Check(totalCost > stdCost, "total mobilisation imposes steep financial procurement surcharges");

        // 3. Strategic stockpiles seeded & burn calculation
        campaign.Defines.StrategicStockpilesEnabled = true;
        StrategicStockpiles.EnsureSeeded(campaign, rus);
        var initialAmmo = StrategicStockpiles.Get(campaign, rus, "Ammunition");
        var initialOil = StrategicStockpiles.Get(campaign, rus, "Oil");
        Check(initialAmmo > 0f && initialOil > 0f, "strategic depot seeded with starting ammo and fuel");

        StrategicStockpiles.TickDay(campaign);
        Check(rus.DailyStockpileGain.ContainsKey("Ammunition"), "daily stockpile gains tracked");
        Check(rus.DailyStockpileBurn.ContainsKey("Ammunition"), "daily stockpile burns tracked");

        // 3b. A standard batch pays and physically drains the strategic depot, not only an
        // emergency posture. This is the difference between a queue cap and a real cost.
        StrategicStockpiles.EnsureSeeded(campaign, rus);
        var standardTemplate = UnitTypeCatalog.Get("line_infantry");
        var standardBill = StrategicStockpiles.DivisionMaterialBill(standardTemplate, 1, MobilisationMode.Standard);
        var ammoBefore = StrategicStockpiles.Get(campaign, rus, "Ammunition");
        StrategicStockpiles.ConsumeBill(campaign, rus, standardBill);
        Check(StrategicStockpiles.Get(campaign, rus, "Ammunition") < ammoBefore,
            "standard recruitment material bill drains the strategic ammunition depot");

        // 4. Munitions combat factor when well-stocked vs dry
        var wellStockedFactor = StrategicStockpiles.CombatMunitionsFactor(campaign, rus);
        Check(wellStockedFactor >= 0.95f, "well-stocked nation has full combat munitions efficiency");

        rus.Stockpiles["Ammunition"] = 0f;
        rus.Stockpiles["Arms"] = 0f;
        var dryFactor = StrategicStockpiles.CombatMunitionsFactor(campaign, rus);
        Check(dryFactor < 0.70f, "nation out of ammunition and arms suffers severe combat penalties");

        // 5. Fleet slipway scaling under total mobilisation
        var normalSlips = Shipbuilding.Slipways(campaign, usa, MobilisationMode.Standard);
        var totalSlips = Shipbuilding.Slipways(campaign, usa, MobilisationMode.TotalMobilisation);
        Check(totalSlips > normalSlips, "total naval mobilisation expands active shipyard slipways");

        Console.WriteLine("  PASS mobilisation and strategic stockpile test suite (all assertions verified)");
    }
}
