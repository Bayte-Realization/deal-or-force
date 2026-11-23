using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 5E (`ECO-02/03`):
/// - Switchable production methods (`ProductionMethods`, `SetProductionMethodCommand`)
/// - Real output, labor-efficiency, energy, coal-reduction, and bonus-output shifts
/// - Modern strategic buildings (`CleanEnergyPlant`, `IndustrialPark`, `SemiconductorFab`, `LogisticsHub`)
/// - Verified 2024 World Bank / IMF Nominal GDP calibration across top economies
/// - Save/load round-trip, `CampaignFingerprint` sensitivity, and 90-day 1-thread vs N-thread determinism.
/// </summary>
static class EconomyOverhaulTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("economy overhaul: " + label);
        Console.WriteLine("PASS economy overhaul: " + label);
    }

    [TestSuite("--economy-overhaul-only", Order = 2670, IncludeInDefaultRun = true,
        Description = "Switchable production methods, Wave 5 strategic buildings, 2024 GDP calibration, save/fingerprint & 90d stability.")]
    public static void Run()
    {
        TestProductionMethodCatalogAndValidation();
        TestProductionMethodCommandAndSimulationEffects();
        TestWave5BuildingsAndChains();
        Test2024GdpCalibration();
        TestSaveLoadAndFingerprintDeterminism();
        TestNinetyDayMacroStabilityAndThreadParity();
    }

    static void TestProductionMethodCatalogAndValidation()
    {
        ProductionChains.ResetCatalog();
        Check(ProductionMethods.MethodCount >= 24,
            $"loaded {ProductionMethods.MethodCount} switchable production methods across 8 building classes");

        var expectedBuildings = new[]
        {
            BuildingKind.CivilianIndustry,
            BuildingKind.PowerPlant,
            BuildingKind.Agriculture,
            BuildingKind.ExtractionIndustry,
            BuildingKind.Refinery,
            BuildingKind.Smelter,
            BuildingKind.ElectronicsPlant,
            BuildingKind.ArmsIndustry
        };
        foreach (var kind in expectedBuildings)
        {
            var methods = ProductionMethods.ForBuilding(kind);
            var def = ProductionMethods.DefaultFor(kind);
            Check(methods.Count >= 3 && def is not null && def.IsDefault,
                $"{kind} has {methods.Count} methods with default '{def?.Id}'");
        }

        // Verify EN + RU localization exists for every production method name & description.
        var allLocOk = true;
        foreach (var method in ProductionMethods.All)
        {
            if (string.IsNullOrEmpty(Loc.TextIn("english", method.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", method.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("english", method.Description)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", method.Description)))
            {
                allLocOk = false;
                break;
            }
        }
        Check(allLocOk, "every production method has English and Russian name + description localization");
    }

    static void TestProductionMethodCommandAndSimulationEffects()
    {
        ProductionChains.ResetCatalog();
        var c = new Campaign(50501);
        var nation = c.AddNation(new Nation
        {
            Id = 0,
            MapIndex = 1,
            Tag = "TST",
            Name = "Industrial Republic",
            Population = 20_000_000,
            Gdp = 800f,
            Treasury = 80f,
            Literacy = 0.96f,
            Productivity = 1.15f,
            EmployedShare = 0.80f,
            OutputScale = 1f
        });
        var state = new ScenarioState
        {
            Id = "tst_capital",
            MapIndex = 1,
            NationMapIndex = 1,
            Population = 20_000_000,
            Urbanization = 0.80f,
            StandardOfLiving = 0.72f
        };
        state.Buildings[BuildingKind.CivilianIndustry] = 10;
        state.Buildings[BuildingKind.PowerPlant] = 6;
        state.Buildings[BuildingKind.Refinery] = 4;
        c.States.Add(state);
        c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(nation.Id);

        foreach (var good in CommodityCatalog.All)
            nation.Market[good] = new MarketQuote { Commodity = good, Price = CommodityCatalog.Get(good).BasePrice, Satisfaction = 1f };

        void RunOnce()
        {
            foreach (var q in nation.Market.Values) { q.Production = 0f; q.Demand = 0f; }
            ProductionChains.RunRegion(nation, state, 1f, ProductionProgramme.Allocate(c, nation));
        }

        // Baseline run
        RunOnce();
        var baseConsumer = nation.Market[Commodity.ConsumerGoods].Production;
        var baseEnergyDemand = nation.Market[Commodity.Energy].Demand;
        var baseMachineryDemand = nation.Market[Commodity.Machinery].Demand;
        var baseJobs = ProductionMethods.EffectiveJobs(nation, state);

        // Switch CivilianIndustry -> pm_civilian_automated
        new SetProductionMethodCommand
        {
            IssuerId = nation.Id,
            BuildingId = "CivilianIndustry",
            MethodId = "pm_civilian_automated"
        }.Apply(c);
        Check(ProductionMethods.ActiveFor(nation, BuildingKind.CivilianIndustry)?.Id == "pm_civilian_automated",
            "SetProductionMethodCommand switches CivilianIndustry to pm_civilian_automated");

        RunOnce();
        var autoConsumer = nation.Market[Commodity.ConsumerGoods].Production;
        var autoEnergyDemand = nation.Market[Commodity.Energy].Demand;
        var autoMachineryDemand = nation.Market[Commodity.Machinery].Demand;
        var autoJobs = ProductionMethods.EffectiveJobs(nation, state);

        Check(autoConsumer > baseConsumer * 1.25f,
            $"Automated factories raise ConsumerGoods output ({baseConsumer:F2} -> {autoConsumer:F2})");
        Check(autoEnergyDemand > baseEnergyDemand + 1f && autoMachineryDemand > baseMachineryDemand + 1f,
            $"Automated factories raise Energy ({baseEnergyDemand:F2}->{autoEnergyDemand:F2}) & Machinery ({baseMachineryDemand:F2}->{autoMachineryDemand:F2}) demand");
        Check(autoJobs < baseJobs * 0.90f,
            $"Automated factories lower job demand ({baseJobs:F2} -> {autoJobs:F2})");

        // Switch PowerPlant -> pm_power_clean_grid cuts Coal demand and increases Energy output
        var coalBeforeClean = nation.Market[Commodity.Coal].Demand;
        var powerBeforeClean = nation.Market[Commodity.Energy].Production;
        new SetProductionMethodCommand
        {
            IssuerId = nation.Id,
            BuildingId = "PowerPlant",
            MethodId = "pm_power_clean_grid"
        }.Apply(c);
        RunOnce();
        Check(nation.Market[Commodity.Coal].Demand < coalBeforeClean - 5f &&
              nation.Market[Commodity.Energy].Production > powerBeforeClean * 1.15f,
            $"Clean-grid cuts Coal demand ({coalBeforeClean:F2} -> {nation.Market[Commodity.Coal].Demand:F2}) and raises Energy ({powerBeforeClean:F2} -> {nation.Market[Commodity.Energy].Production:F2})");

        // Switch Refinery -> pm_refinery_petrochemical adds bonus Plastics output
        Check(CommodityCatalog.TryParse("Plastics", out var plastics), "Plastics good resolves");
        var plasticsBefore = nation.Market[plastics].Production;
        new SetProductionMethodCommand
        {
            IssuerId = nation.Id,
            BuildingId = "Refinery",
            MethodId = "pm_refinery_petrochemical"
        }.Apply(c);
        RunOnce();
        Check(nation.Market[plastics].Production > plasticsBefore + 0.5f,
            $"Integrated Petrochemical Complex adds bonus Plastics output ({plasticsBefore:F2} -> {nation.Market[plastics].Production:F2})");

        // Literacy gate blocks automated factories for low-literacy nation
        var lowLit = c.AddNation(new Nation { Id = 1, MapIndex = 2, Tag = "LOW", Name = "LowLit", Population = 5_000_000, Literacy = 0.45f, Productivity = 0.70f });
        new SetProductionMethodCommand { IssuerId = lowLit.Id, BuildingId = "CivilianIndustry", MethodId = "pm_civilian_automated" }.Apply(c);
        Check(ProductionMethods.ActiveFor(lowLit, BuildingKind.CivilianIndustry)?.Id == "pm_civilian_baseline",
            "Literacy/productivity gate rejects pm_civilian_automated for low-literacy nation");

        // Switching back to baseline removes the dictionary key cleanly
        new SetProductionMethodCommand { IssuerId = nation.Id, BuildingId = "CivilianIndustry", MethodId = "pm_civilian_baseline" }.Apply(c);
        Check(!nation.ActiveProductionMethods.ContainsKey("CivilianIndustry"),
            "Switching back to baseline removes non-default dictionary entry");
    }

    static void TestWave5BuildingsAndChains()
    {
        BuildingCatalog.ResetCatalog();
        ProductionChains.ResetCatalog();
        var wave5Buildings = new[] { "CleanEnergyPlant", "IndustrialPark", "SemiconductorFab", "LogisticsHub" };
        var makes = new List<Commodity>();
        var uses = new List<Commodity>();
        foreach (var id in wave5Buildings)
        {
            Check(BuildingCatalog.TryParse(id, out var kind), $"Wave 5 building '{id}' registered in BuildingCatalog");
            var def = BuildingCatalog.Get(kind);
            Check(def.Cost > 0f && def.Days > 0 && def.CapacityGain > 0f && def.Jobs > 0f,
                $"{id} has valid cost={def.Cost:F2}, days={def.Days}, cap={def.CapacityGain:F2}, jobs={def.Jobs:F2}");
            ProductionChains.RolesOf(kind, makes, uses);
            Check(makes.Count > 0 && uses.Count > 0,
                $"{id} wired into live production chains (makes={makes.Count}, uses={uses.Count})");
            Check(!string.IsNullOrEmpty(Loc.TextIn("russian", def.Name)),
                $"{id} name '{def.Name}' localized in Russian");
        }
    }

    static void Test2024GdpCalibration()
    {
        var c = Campaign.CreateBootstrap(42);
        var checks = new (string Tag, float Expected)[]
        {
            ("USA", 29298f),
            ("CHN", 18730f),
            ("DEU", 4686f),
            ("JPN", 4190f),
            ("IND", 3761f),
            ("RUS", 2186f),
            ("BRA", 2186f),
            ("TUR", 1359f),
            ("SAU", 1254f),
            ("UKR", 191f),
            ("BLR", 79f)
        };
        foreach (var (tag, expected) in checks)
        {
            var n = c.NationByTag(tag);
            Check(n is not null && MathF.Abs(n.Gdp - expected) <= expected * 0.02f,
                $"{tag} opens at calibrated 2024 GDP ${n?.Gdp:N1}B (expected ${expected:N0}B)");
            Check(n is not null && n.OutputScale > 0f && float.IsFinite(n.OutputScale),
                $"{tag} calibrated finite OutputScale={n?.OutputScale:0.####}");
        }
    }

    static void TestSaveLoadAndFingerprintDeterminism()
    {
        var c = Campaign.CreateBootstrap(50502);
        var usa = c.NationByTag("USA");
        Check(usa is not null, "USA found for save/fingerprint test");
        var usaState = c.States.First(r => r.NationMapIndex == usa!.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)usaState.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)usa!.MapIndex, 512).ToArray(),
            32, 16, _ => false));
        c.Armies.Clear();
        var fpBefore = CampaignFingerprint.Of(c);

        new SetProductionMethodCommand
        {
            IssuerId = usa!.Id,
            BuildingId = "CivilianIndustry",
            MethodId = "pm_civilian_automated"
        }.Apply(c);
        var fpAfter = CampaignFingerprint.Of(c);
        Check(fpBefore != fpAfter,
            $"CampaignFingerprint changes on production method switch ({fpBefore} -> {fpAfter})");

        var tempDir = Path.Combine(Path.GetTempPath(), "dof-eco-wave5-" + Guid.NewGuid().ToString("N"));
        try
        {
            c.Defines.SaveTextFormat = true;
            SaveSystem.Save(tempDir, "wave5_eco", c, "test");
            var loadResult = SaveSystem.Load(tempDir, "wave5_eco", "test");
            Check(loadResult.Campaign is not null, $"SaveSystem.Load succeeded ({loadResult.Warning ?? "ok"})");
            var loaded = loadResult.Campaign!;
            var loadedUsa = loaded.NationByTag("USA");
            Check(loadedUsa is not null &&
                  ProductionMethods.ActiveFor(loadedUsa, BuildingKind.CivilianIndustry)?.Id == "pm_civilian_automated",
                "ActiveProductionMethods survives save/load round-trip");
            Check(CampaignFingerprint.Of(loaded) == fpAfter,
                "CampaignFingerprint matches exactly after save/load round-trip");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    static void TestNinetyDayMacroStabilityAndThreadParity()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;
            ulong Run(int threads, out int aiAdopted, out float sauGdp, out float turGdp, out string issue)
            {
                DeterministicParallel.MaxThreadsOverride = threads;
                var c = Campaign.CreateBootstrap(42);
                c.IsPaused = false;
                c.Defines.HoursPerTick = 24;
                for (var d = 0; d < 90; d++) c.TickDay();
                aiAdopted = c.Nations.Count(n => n.ActiveProductionMethods is { Count: > 0 });
                sauGdp = c.NationByTag("SAU")?.Gdp ?? 0f;
                turGdp = c.NationByTag("TUR")?.Gdp ?? 0f;
                issue = NumericIntegrity.FirstLiveIssue(c);
                return CampaignFingerprint.Of(c);
            }

            var fp1 = Run(1, out var ai1, out var sau1, out var tur1, out var issue1);
            var fpN = Run(0, out var aiN, out _, out _, out var issueN);

            Check(issue1 is null && issueN is null, "90-day calibrated economy has zero NaN / integrity issues");
            Check(fp1 == fpN, $"90-day CampaignFingerprint bit-identical 1-thread ({fp1}) == N-thread ({fpN})");
            Check(ai1 > 0 && ai1 == aiN, $"AI nations deterministically adopted production methods over 90d ({ai1} nations)");
            Check(sau1 > 900f && sau1 < 1800f && tur1 > 950f && tur1 < 2000f,
                $"90-day macro stability holds calibrated 2024 bands (SAU=${sau1:N1}B, TUR=${tur1:N1}B)");
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }
}
