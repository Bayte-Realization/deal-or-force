using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 6H (Data-Driven Diseases &amp; Natural Disasters):
/// 1. 6 diseases &amp; 8 natural disasters load from common/diseases &amp; common/disasters with ResetCatalog + Merge + remove.
/// 2. Realistic regional/climate/seasonal/coastal triggers and severity tiers.
/// 3. HealthResponseCommand &amp; DisasterReliefCommand trade-offs, costs, cooldowns, refusals &amp; OpportunityAtlas read API.
/// 4. Cross-border/trade contagion, quarantine containment, demographic/economic/stability effects &amp; Chronicle.
/// 5. Deterministic MT == ST TickStages, ObserverMode AI responses, SaveSystem round-trip &amp; fingerprint sensitivity.
/// 6. 100% EN + RU localization key &amp; placeholder parity.
/// </summary>
static class DiseasesAndDisastersTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL diseases-disasters: " + label);
        Console.WriteLine("PASS diseases-disasters: " + label);
    }

    [TestSuite("--diseases-disasters-only", Order = 2710, IncludeInDefaultRun = true,
        Description = "Wave 6H data-driven 6 diseases & 8 natural disasters, regional triggers, agency commands, AI, MT==ST & loc.")]
    public static void Run()
    {
        try
        {
            TestCatalogsAndModOverlays();
            TestRegionalClimateTriggersAndSeverityTiers();
            TestCommandsTradeoffsRefusalsAndReadApi();
            TestContagionProgressionAndChronicle();
            TestObserverAiSaveRoundTripAndThreadDeterminism();
            TestLocalizationParity();
        }
        finally
        {
            DiseaseCatalog.ResetCatalog();
            DisasterCatalog.ResetCatalog();
        }
    }

    private static void TestCatalogsAndModOverlays()
    {
        DiseaseCatalog.ResetCatalog();
        DisasterCatalog.ResetCatalog();

        var diseases = DiseaseCatalog.All;
        var disasters = DisasterCatalog.All;
        Check(diseases.Count == 6, $"loaded {diseases.Count} base diseases (expected 6)");
        Check(disasters.Count == 8, $"loaded {disasters.Count} base natural disasters (expected 8)");

        string[] expectedDiseases =
        {
            "seasonal_pandemic_influenza", "tropical_vector_fever", "conflict_cholera_outbreak",
            "antimicrobial_superbug", "zoonotic_viral_pneumonia", "arctic_winter_bronchitis"
        };
        foreach (var id in expectedDiseases)
            Check(DiseaseCatalog.Find(id) is not null, $"disease '{id}' loaded");

        string[] expectedDisasters =
        {
            "tectonic_earthquake", "coastal_typhoon", "riverine_megaflood",
            "prolonged_agricultural_drought", "megafire_complex", "tsunami_surge",
            "volcanic_ash_eruption", "polar_vortex_blizzard"
        };
        foreach (var id in expectedDisasters)
            Check(DisasterCatalog.Find(id) is not null, $"disaster '{id}' loaded");

        // Mod overlay Merge + "remove": true + ResetCatalog
        var errs = new List<string>();
        using (var dDoc = JsonDocument.Parse("""
        {
          "mod_lunar_flu": {
            "name": "Lunar Strain",
            "summary": "Modded outbreak.",
            "cause_summary": "Modded cause.",
            "category": "respiratory",
            "annual_chance": 0.04,
            "starting_severity": 35
          },
          "arctic_winter_bronchitis": { "remove": true }
        }
        """))
        {
            DiseaseCatalog.Merge(dDoc.RootElement, errs);
        }
        using (var natDoc = JsonDocument.Parse("""
        {
          "mod_geomagnetic_storm": {
            "name": "Geomagnetic Surge",
            "summary": "Modded grid disruption.",
            "cause_summary": "Solar storm.",
            "category": "meteorological",
            "annual_chance": 0.03,
            "starting_severity": 45
          },
          "polar_vortex_blizzard": { "remove": true }
        }
        """))
        {
            DisasterCatalog.Merge(natDoc.RootElement, errs);
        }
        Check(errs.Count == 0 &&
              DiseaseCatalog.Find("mod_lunar_flu") is not null &&
              DiseaseCatalog.Find("arctic_winter_bronchitis") is null &&
              DisasterCatalog.Find("mod_geomagnetic_storm") is not null &&
              DisasterCatalog.Find("polar_vortex_blizzard") is null,
            "Merge adds mod entries and honors \"remove\": true cleanly");

        DiseaseCatalog.ResetCatalog();
        DisasterCatalog.ResetCatalog();
        Check(DiseaseCatalog.All.Count == 6 && DiseaseCatalog.Find("arctic_winter_bronchitis") is not null &&
              DisasterCatalog.All.Count == 8 && DisasterCatalog.Find("polar_vortex_blizzard") is not null,
            "ResetCatalog restores exact 6 diseases + 8 disasters");
    }

    private static void TestRegionalClimateTriggersAndSeverityTiers()
    {
        var c = new Campaign(20261130);
        c.Date = new GameDate(2025, 1, 15); // Northern winter, Southern summer

        var arctic = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "ARC", Name = "Arctic State",
            Population = 5_000_000, Gdp = 300f, Treasury = 50f, CapitalLatitude = 66f
        });
        var arcticState = new ScenarioState
        {
            Id = "arc_1", MapIndex = 10, NationMapIndex = arctic.MapIndex,
            Population = 5_000_000, Urbanization = 0.55f
        };

        var tropicLandlocked = c.AddNation(new Nation
        {
            Id = 1, MapIndex = 2, Tag = "TRP", Name = "Tropic Interior",
            Population = 10_000_000, Gdp = 200f, Treasury = 30f, CapitalLatitude = 8f
        });
        var tropicState = new ScenarioState
        {
            Id = "trp_1", MapIndex = 20, NationMapIndex = tropicLandlocked.MapIndex,
            Population = 10_000_000, Urbanization = 0.35f
        };

        var coastalTropic = c.AddNation(new Nation
        {
            Id = 2, MapIndex = 3, Tag = "CST", Name = "Coastal Tropics",
            Population = 12_000_000, Gdp = 250f, Treasury = 40f, CapitalLatitude = 14f
        });
        var coastalState = new ScenarioState
        {
            Id = "cst_1", MapIndex = 30, NationMapIndex = coastalTropic.MapIndex,
            Population = 12_000_000, Urbanization = 0.60f
        };
        coastalState.Buildings[BuildingKind.Port] = 1;

        c.States.Add(arcticState);
        c.States.Add(tropicState);
        c.States.Add(coastalState);
        c.InvalidateStateIndex();

        var blizzard = DisasterCatalog.Find("polar_vortex_blizzard")!;
        var arcticBronchitis = DiseaseCatalog.Find("arctic_winter_bronchitis")!;
        var tropicFever = DiseaseCatalog.Find("tropical_vector_fever")!;
        var typhoon = DisasterCatalog.Find("coastal_typhoon")!;
        var tsunami = DisasterCatalog.Find("tsunami_surge")!;

        Check(DiseasesAndDisasters.IsRegionEligibleForDisaster(c, arctic, arcticState, blizzard, out _),
            "polar_vortex_blizzard eligible in high-latitude January winter");
        Check(DiseasesAndDisasters.IsRegionEligibleForDisease(c, arctic, arcticState, arcticBronchitis, out _),
            "arctic_winter_bronchitis eligible in high-latitude January winter");
        Check(!DiseasesAndDisasters.IsRegionEligibleForDisaster(c, tropicLandlocked, tropicState, blizzard, out _),
            "polar_vortex_blizzard ineligible in equatorial region");
        Check(!DiseasesAndDisasters.IsRegionEligibleForDisease(c, arctic, arcticState, tropicFever, out _),
            "tropical_vector_fever ineligible in polar latitude");
        Check(!DiseasesAndDisasters.IsRegionEligibleForDisaster(c, tropicLandlocked, tropicState, tsunami, out _) &&
              !DiseasesAndDisasters.IsRegionEligibleForDisaster(c, tropicLandlocked, tropicState, typhoon, out _),
            "landlocked nation cannot trigger coastal typhoon or tsunami");
        Check(DiseasesAndDisasters.IsRegionEligibleForDisaster(c, coastalTropic, coastalState, tsunami, out _),
            "coastal region eligible for tsunami_surge");

        c.Date = new GameDate(2025, 7, 15); // Northern summer / northern tropical wet season
        Check(!DiseasesAndDisasters.IsRegionEligibleForDisaster(c, arctic, arcticState, blizzard, out _),
            "polar_vortex_blizzard ineligible in high-latitude July summer");
        Check(DiseasesAndDisasters.IsRegionEligibleForDisease(c, tropicLandlocked, tropicState, tropicFever, out _),
            "tropical_vector_fever eligible in northern tropics July wet season");

        Check(DiseasesAndDisasters.TierOf(20f) == CrisisSeverityTier.Minor &&
              DiseasesAndDisasters.TierOf(45f) == CrisisSeverityTier.Moderate &&
              DiseasesAndDisasters.TierOf(65f) == CrisisSeverityTier.Severe &&
              DiseasesAndDisasters.TierOf(88f) == CrisisSeverityTier.Critical,
            "CrisisSeverityTier maps Minor/Moderate/Severe/Critical cleanly");
    }

    private static void TestCommandsTradeoffsRefusalsAndReadApi()
    {
        var c = new Campaign(20261131);
        c.Date = new GameDate(2025, 1, 10);
        var nation = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "KOR", Name = "Korea",
            Population = 50_000_000, Gdp = 1700f, Treasury = 80f, PoliticalPoints = 50f,
            Stability = 0.70f, CapitalLatitude = 37.5f, IsHuman = true
        });
        var state = new ScenarioState
        {
            Id = "kor_11", MapIndex = 100, NationMapIndex = nation.MapIndex,
            Name = "Seoul", Population = 20_000_000, Urbanization = 0.85f,
            StandardOfLiving = 0.72f, Radicalism = 0.20f, WarDamage = 0.05f
        };
        c.States.Add(state);
        c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(nation.Id);

        // Trigger a disease & a disaster
        var hc = DiseasesAndDisasters.TryTriggerDisease(c, nation, "seasonal_pandemic_influenza", state, bypassConditions: true);
        var nd = DiseasesAndDisasters.TryTriggerDisaster(c, nation, "tectonic_earthquake", state, bypassConditions: true);
        Check(hc is not null && nd is not null, "triggered both active disease and natural disaster");
        Check(state.WarDamage > 0.05f, $"tectonic_earthquake damaged regional works (WarDamage={state.WarDamage:F3})");

        // Read API check (must not alter fingerprint)
        var fpBeforeRead = CampaignFingerprint.Of(c);
        var hcBriefs = DiseasesAndDisasters.ActiveHealthCrises(c, nation);
        var ndBriefs = DiseasesAndDisasters.ActiveDisasters(c, nation);
        Check(hcBriefs.Count == 1 && ndBriefs.Count == 1 &&
              !string.IsNullOrWhiteSpace(hcBriefs[0].Name) && !string.IsNullOrWhiteSpace(hcBriefs[0].Cause) &&
              !string.IsNullOrWhiteSpace(ndBriefs[0].Name) && !string.IsNullOrWhiteSpace(ndBriefs[0].Cause) &&
              hcBriefs[0].CanRespond && ndBriefs[0].CanRespond,
            "ActiveHealthCrises & ActiveDisasters read API returns rich localized UI briefs");
        Check(CampaignFingerprint.Of(c) == fpBeforeRead,
            "reading ActiveHealthCrises / ActiveDisasters leaves CampaignFingerprint untouched");

        // Enact TargetedQuarantine via CampaignCommandQueue -> trade-off: drops spread risk, imposes income penalty & small stab friction
        var spreadBefore = hcBriefs[0].SpreadRisk;
        var cashBefore = nation.Treasury;
        var ppBefore = nation.PoliticalPoints;
        var penaltiesBefore = nation.IncomePenalties?.Count ?? 0;

        c.Commands.Enqueue(new HealthResponseCommand
        {
            IssuerId = nation.Id, CrisisId = hc!.Id, Action = HealthResponseAction.TargetedQuarantine
        });
        c.Commands.ApplyAll(c);
        var hcAfter = DiseasesAndDisasters.ActiveHealthCrises(c, nation);
        Check(hc.Response == HealthResponseAction.TargetedQuarantine &&
              nation.Treasury < cashBefore && nation.PoliticalPoints < ppBefore &&
              (nation.IncomePenalties?.Count ?? 0) > penaltiesBefore &&
              (hcAfter.Count == 0 || hcAfter[0].SpreadRisk < spreadBefore),
            "HealthResponseCommand(TargetedQuarantine) deducts cash/PP, trades off income penalty, and cuts spread risk");

        // Cooldown refusal on immediate follow-up response
        Check(!DiseasesAndDisasters.CanRespondHealth(c, nation, hc.Id, HealthResponseAction.MassVaccinationAndMedicalDrive, out var cdReason) &&
              cdReason.Length > 0,
            $"health response cooldown refuses immediate second response ('{cdReason}')");

        // Enact InfrastructureReconstruction via CampaignCommandQueue
        var dmgBefore = state.WarDamage;
        var prepBefore = nation.DisasterPreparedness;
        c.Commands.Enqueue(new DisasterReliefCommand
        {
            IssuerId = nation.Id, DisasterId = nd!.Id, Action = DisasterReliefAction.InfrastructureReconstruction
        });
        c.Commands.ApplyAll(c);
        Check(nd.Response == DisasterReliefAction.InfrastructureReconstruction &&
              state.WarDamage < dmgBefore && nation.DisasterPreparedness > prepBefore,
            "DisasterReliefCommand(InfrastructureReconstruction) repairs regional damage and boosts DisasterPreparedness");

        // Low-treasury and disabled-feature refusals
        nation.Treasury = 0f;
        var hc2 = DiseasesAndDisasters.TryTriggerDisease(c, nation, "antimicrobial_superbug", state, bypassConditions: true);
        Check(hc2 is not null, "triggered second concurrent disease up to cap");
        Check(!DiseasesAndDisasters.CanRespondHealth(c, nation, hc2!.Id, HealthResponseAction.MassVaccinationAndMedicalDrive, out var cashReason) &&
              cashReason.Length > 0,
            $"insufficient treasury refuses MassVaccinationAndMedicalDrive ('{cashReason}')");

        c.Defines.DiseasesAndDisastersEnabled = false;
        Check(!DiseasesAndDisasters.CanRespondHealth(c, nation, hc2.Id, HealthResponseAction.TargetedQuarantine, out var disReason) &&
              disReason.Length > 0,
            "DiseasesAndDisastersEnabled=false blocks commands cleanly");
    }

    private static void TestContagionProgressionAndChronicle()
    {
        static (Campaign Campaign, Nation Src, Nation Dst, ScenarioState S0) BuildContagionCampaign(bool enabled)
        {
            var camp = new Campaign(20261132) { ScenarioId = "wave6h-contagion" };
            camp.Defines.DiseasesAndDisastersEnabled = enabled;
            camp.Defines.DiseasesAndDisastersAiEnabled = false;
            camp.Defines.DiseaseSpreadMultiplier = 25.0f; // Guarantee deterministic short-window cross-border spread
            camp.Date = new GameDate(2025, 1, 1);
            camp.IsPaused = false;

            var sNation = camp.AddNation(new Nation
            {
                Id = 0, MapIndex = 1, Tag = "SRC", Name = "Source Nation",
                Population = 20_000_000, Gdp = 400f, Treasury = 50f, PoliticalPoints = 40f,
                Stability = 0.75f, CapitalLatitude = 45f
            });
            var dNation = camp.AddNation(new Nation
            {
                Id = 1, MapIndex = 2, Tag = "DST", Name = "Neighbor Nation",
                Population = 20_000_000, Gdp = 400f, Treasury = 50f, PoliticalPoints = 40f,
                Stability = 0.75f, CapitalLatitude = 45f
            });
            var state0 = new ScenarioState
            {
                Id = "s0", MapIndex = 10, NationMapIndex = sNation.MapIndex, Name = "Alpha",
                Population = 20_000_000, Urbanization = 0.7f, Literacy = 0.92f, StandardOfLiving = 0.85f
            };
            var state1 = new ScenarioState
            {
                Id = "s1", MapIndex = 11, NationMapIndex = dNation.MapIndex, Name = "Beta",
                Population = 20_000_000, Urbanization = 0.7f, Literacy = 0.92f, StandardOfLiving = 0.85f
            };
            camp.States.Add(state0);
            camp.States.Add(state1);

            var statesGrid = new ushort[16];
            var ownersGrid = new ushort[16];
            for (var i = 0; i < 16; i++)
            {
                var left = (i % 4) < 2;
                statesGrid[i] = left ? (ushort)state0.MapIndex : (ushort)state1.MapIndex;
                ownersGrid[i] = left ? (ushort)sNation.MapIndex : (ushort)dNation.MapIndex;
            }
            camp.AttachProvinceTopology(ProvinceTopology.Build(statesGrid, ownersGrid, 4, 4, _ => false));
            camp.Armies.Clear();
            camp.InvalidateStateIndex();
            return (camp, sNation, dNation, state0);
        }

        var (c, src, dst, s0) = BuildContagionCampaign(enabled: true);
        var (ctrl, _, _, ctrlS0) = BuildContagionCampaign(enabled: false);

        var outbreak = DiseasesAndDisasters.TryTriggerDisease(c, src, "zoonotic_viral_pneumonia", s0, bypassConditions: true);
        Check(outbreak is not null, "seeded zoonotic_viral_pneumonia outbreak in Source Nation");
        var popBefore = s0.Population;

        for (var d = 0; d < 42; d++)
        {
            c.TickDay();
            foreach (var _ in DiseasesAndDisasters.TickStages(c)) { }
            ctrl.TickDay();
        }

        Check(dst.ActiveHealthCrises.Any(h => h.OriginNationId == src.Id),
            "uncontained outbreak spreads deterministically across land borders to Neighbor Nation");
        Check(outbreak!.TotalDeaths > 0 && s0.Population < popBefore && s0.Population < ctrlS0.Population && src.Population == s0.Population,
            $"disease progression applies bounded demographic deaths ({outbreak.TotalDeaths}) preserving nation/state sum (pop={s0.Population}, ctrl={ctrlS0.Population})");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "health_outbreak") &&
              c.Chronicle.Entries.Any(e => e.Kind == "health_spread"),
            "Chronicle records outbreak and cross-border spread events");
    }

    private static void TestObserverAiSaveRoundTripAndThreadDeterminism()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;

            ulong RunWorld(int threads, out int aiResponded, out string integrityIssue)
            {
                DeterministicParallel.MaxThreadsOverride = threads;
                var boot = Campaign.CreateBootstrap(20261133);
                boot.IsPaused = false;
                boot.ObserverMode = true; // Must STILL let AI pilot IsHuman former player seat!
                var player = boot.Player ?? boot.Nations[0];
                player.IsHuman = true;
                player.Treasury = MathF.Max(player.Treasury, MathF.Max(250f, player.Gdp * 0.02f));
                player.PoliticalPoints = MathF.Max(player.PoliticalPoints, 60f);

                var second = boot.Nations.First(n => n.Id != player.Id && n.Movement is null && n.Population > 0);
                second.Treasury = MathF.Max(second.Treasury, MathF.Max(250f, second.Gdp * 0.02f));
                second.PoliticalPoints = MathF.Max(second.PoliticalPoints, 60f);

                DiseasesAndDisasters.TryTriggerDisease(boot, player, "seasonal_pandemic_influenza", bypassConditions: true);
                DiseasesAndDisasters.TryTriggerDisaster(boot, second, "tectonic_earthquake", bypassConditions: true);

                for (var d = 0; d < 28; d++)
                {
                    boot.TickDay();
                    foreach (var _ in DiseasesAndDisasters.TickStages(boot)) { }
                }

                aiResponded = (player.ActiveHealthCrises.Any(h => h.Response != HealthResponseAction.None) ? 1 : 0) +
                              (second.ActiveDisasters.Any(x => x.Response != DisasterReliefAction.None) ? 1 : 0);
                integrityIssue = NumericIntegrity.FirstLiveIssue(boot);
                var h = CampaignFingerprint.Of(boot);
                DiseasesAndDisasters.WriteFingerprint(boot, ref h);
                return h;
            }

            var fp1 = RunWorld(1, out var resp1, out var issue1);
            var fpN = RunWorld(0, out var respN, out var issueN);
            Check(issue1 is null && issueN is null, "28-day bootstrap + diseases/disasters is 100% NaN/integrity clean");
            Check(resp1 == 2 && respN == 2, $"ObserverMode AI + sovereign AI autonomously responded to crises ({resp1}/2)");
            Check(fp1 == fpN, $"1-thread ({fp1}) == N-thread ({fpN}) bit-identical CampaignFingerprint + WriteFingerprint");

            // SaveSystem round-trip verification
            var saveWorld = Campaign.CreateBootstrap(20261134);
            var usa = saveWorld.NationByTag("USA") ?? saveWorld.Nations[0];
            var usaState = saveWorld.States.First(r => r.NationMapIndex == usa.MapIndex);
            saveWorld.AttachProvinceTopology(ProvinceTopology.Build(
                Enumerable.Repeat((ushort)usaState.MapIndex, 256).ToArray(),
                Enumerable.Repeat((ushort)usa.MapIndex, 256).ToArray(),
                16, 16, _ => false));
            saveWorld.Armies.Clear();
            usa.Treasury = MathF.Max(usa.Treasury, MathF.Max(250f, usa.Gdp * 0.02f));
            usa.PoliticalPoints = 80f;

            var baseDdHash = DiseasesAndDisasters.ComputeFingerprint(saveWorld);
            var hc = DiseasesAndDisasters.TryTriggerDisease(saveWorld, usa, "antimicrobial_superbug", usaState, bypassConditions: true);
            var nd = DiseasesAndDisasters.TryTriggerDisaster(saveWorld, usa, "megafire_complex", usaState, bypassConditions: true);
            new HealthResponseCommand { IssuerId = usa.Id, CrisisId = hc!.Id, Action = HealthResponseAction.EmergencyHealthSubsidies }.Apply(saveWorld);
            new DisasterReliefCommand { IssuerId = usa.Id, DisasterId = nd!.Id, Action = DisasterReliefAction.CivilDefenseMobilization }.Apply(saveWorld);

            var ddAfter = DiseasesAndDisasters.ComputeFingerprint(saveWorld);
            var fpAfter = CampaignFingerprint.Of(saveWorld);
            Check(ddAfter != baseDdHash, "DiseasesAndDisasters.ComputeFingerprint sensitive to crises and responses");

            var tempDir = Path.Combine(Path.GetTempPath(), "dof-dd-wave6h-" + Guid.NewGuid().ToString("N"));
            try
            {
                saveWorld.Defines.SaveTextFormat = true;
                SaveSystem.Save(tempDir, "wave6h_dd", saveWorld, "test_dd");
                var loaded = SaveSystem.Load(tempDir, "wave6h_dd", "test_dd").Campaign;
                Check(loaded is not null, "SaveSystem.Load succeeded");
                DiseasesAndDisasters.NormalizeAfterLoad(loaded!);
                var loadedUsa = loaded.NationByTag(usa.Tag)!;
                Check(loadedUsa.ActiveHealthCrises.Count == 1 &&
                      loadedUsa.ActiveHealthCrises[0].Response == HealthResponseAction.EmergencyHealthSubsidies &&
                      loadedUsa.ActiveDisasters.Count == 1 &&
                      loadedUsa.ActiveDisasters[0].Response == DisasterReliefAction.CivilDefenseMobilization,
                    "SaveSystem round-trip preserves active diseases, disasters, and responses");
                Check(CampaignFingerprint.Of(loaded) == fpAfter &&
                      DiseasesAndDisasters.ComputeFingerprint(loaded) == ddAfter,
                    "SaveSystem round-trip yields bit-identical CampaignFingerprint and DiseasesAndDisasters fingerprint");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }

    private static void TestLocalizationParity()
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var enPath = Path.Combine(gameRoot, "localization", "english", "99_wave6_diseases_disasters.json");
        var ruPath = Path.Combine(gameRoot, "localization", "russian", "99_wave6_diseases_disasters.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_wave6_diseases_disasters.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath), GameDataLoader.ForgivingJson);
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath), GameDataLoader.ForgivingJson);
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);

        Check(enMap.Count == ruMap.Count && enMap.Keys.All(ruMap.ContainsKey),
            $"EN/RU 99_wave6_diseases_disasters.json have 100% key parity ({enMap.Count} keys)");
        foreach (var (k, v) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(ruMap[k]), $"non-empty EN/RU loc for '{k}'");
            for (var p = 0; p < 5; p++)
            {
                var slot = char.ConvertFromUtf32(123) + p;
                Check(v.Contains(slot, StringComparison.Ordinal) == ruMap[k].Contains(slot, StringComparison.Ordinal),
                    $"placeholder slot {p} parity for '{k}'");
            }
        }
        foreach (var def in DiseaseCatalog.All)
            Check(enMap.ContainsKey(def.NameKey) && enMap.ContainsKey(def.SummaryKey) && enMap.ContainsKey(def.CauseSummaryKey),
                $"disease '{def.Id}' loc keys present");
        foreach (var def in DisasterCatalog.All)
            Check(enMap.ContainsKey(def.NameKey) && enMap.ContainsKey(def.SummaryKey) && enMap.ContainsKey(def.CauseSummaryKey),
                $"disaster '{def.Id}' loc keys present");
    }
}
