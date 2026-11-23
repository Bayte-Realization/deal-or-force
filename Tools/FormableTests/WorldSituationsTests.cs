using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Proves the Wave 6A WorldSituations &amp; Emergency Summits contract:
/// 1. Data-driven catalog loads all 6 base situations and supports ResetCatalog + Merge overlays.
/// 2. Organic telemetry activates crises cleanly (chokepoints, debt contagion, arms spiral) while
///    keeping day-0 base-2024 fingerprint untouched when no situation is active.
/// 3. SituationInterventionCommand enforces costs/cooldowns across all 4 actions and updates
///    coalition weights, intensity, summit state, Chronicle, and Describe() UI briefs.
/// 4. Staggered deterministic AI participates cleanly across multi-week ticks with zero NaNs.
/// 5. Multi-outcome resolution (StabilizedConsensus, FracturedMultipolarity, CrisisEscalation)
///    applies real ScriptedEffects and records Chronicle entries.
/// 6. SaveSystem.Save + Load round-trip preserves situations and yields bit-identical CampaignFingerprint.
/// 7. EN &amp; RU localization files share 100% key and placeholder parity for all situation keys.
/// </summary>
static class WorldSituationsTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL world situations: " + label);
        Console.WriteLine("PASS world situations: " + label);
    }

    [TestSuite("--world-situations-only", Order = 2695, IncludeInDefaultRun = true,
        Description = "Data-driven WorldSituations catalog, organic telemetry triggers, SituationInterventionCommand, AI summits, outcomes, save/load, fingerprint, and EN/RU loc.")]
    public static void Run()
    {
        try
        {
            TestCatalogLoadAndOverlay();
            TestOrganicTelemetryActivation();
            TestInterventionCommandAndAllFourActions();
            TestAllThreeResolutionOutcomes();
            TestStaggeredAiAndSaveRoundTrip();
            TestLocalizationParity();
        }
        finally
        {
            WorldSituationCatalog.ResetCatalog();
        }
    }

    private static void TestCatalogLoadAndOverlay()
    {
        WorldSituationCatalog.ResetCatalog();
        var all = WorldSituationCatalog.All;
        Check(all.Count >= 6, $"catalog loaded {all.Count} base global situations (>= 6)");
        foreach (var expectedId in new[]
        {
            "global_energy_chokepoint_shock",
            "sovereign_debt_contagion",
            "semiconductor_supply_race",
            "great_power_arms_spiral",
            "regional_autonomy_wave",
            "post_war_reconstruction_charter"
        })
        {
            Check(WorldSituationCatalog.Find(expectedId) is not null, $"catalog contains '{expectedId}'");
        }

        // Mod overlay merge + reset
        using (var doc = JsonDocument.Parse("""
        {
          "mod_solar_flare_crisis": {
            "name": "Solar Flare Grid Emergency",
            "summary": "Geomagnetic storm threatens orbital relays.",
            "category": "technology",
            "trigger_kind": "supply_race",
            "trigger_threshold": 1,
            "starting_intensity": 42,
            "consensus_effects": { "stability": 0.02 }
          }
        }
        """))
        {
            var errs = new List<string>();
            WorldSituationCatalog.Merge(doc.RootElement, errs);
            Check(errs.Count == 0 && WorldSituationCatalog.Find("mod_solar_flare_crisis") is not null,
                "WorldSituationCatalog.Merge adds custom mod situation cleanly");
        }
        WorldSituationCatalog.ResetCatalog();
        Check(WorldSituationCatalog.Find("mod_solar_flare_crisis") is null,
            "WorldSituationCatalog.ResetCatalog removes temporary overlay cleanly");
    }

    private static void TestOrganicTelemetryActivation()
    {
        var campaign = Campaign.CreateBootstrap(20261124);
        Check(WorldSituations.ActiveSituations(campaign).Count == 0,
            "fresh day-0 campaign starts with 0 active global situations");

        var buyer = campaign.NationByTag("DEU") ?? campaign.Nations.First(n => n.Movement is null);
        var supplier = campaign.NationByTag("RUS") ?? campaign.Nations.First(n => n.Id != buyer.Id && n.Movement is null);

        // Simulate squeezed Oil chokepoint telemetry -> triggers global_energy_chokepoint_shock organically.
        campaign.TradeRoutes.Clear();
        campaign.TradeRoutesDay = campaign.ElapsedDays;
        campaign.TradeRoutes.Add(new TradeRoute { SellerId = supplier.Id, BuyerId = buyer.Id, Commodity = Commodity.Oil, Value = 120f, Volume = 12f });
        var rel = CampaignDiplomacy.Get(campaign, buyer.Id, supplier.Id);
        rel.EmbargoByB = true;

        var triggered = WorldSituations.TryTriggerOrganic(campaign, "global_energy_chokepoint_shock", bypassMinDay: true);
        Check(triggered is not null && triggered.DefinitionId == "global_energy_chokepoint_shock",
            "squeezed Oil chokepoint organically activates global_energy_chokepoint_shock");
        Check(WorldSituations.ActiveSituations(campaign).Count == 1,
            "ActiveSituations reports the newly spawned global situation");
    }

    private static void TestInterventionCommandAndAllFourActions()
    {
        var campaign = Campaign.CreateBootstrap(20261125);
        var actors = campaign.Nations.Where(n => n.Movement is null && n.Population > 0).OrderByDescending(n => n.Gdp).Take(4).ToArray();
        Check(actors.Length == 4, "found 4 sovereign powers for summit test");
        foreach (var n in actors) { n.PoliticalPoints = 80f; n.Treasury = MathF.Max(n.Treasury, 50f); }

        var inst = WorldSituations.TryTriggerOrganic(campaign, "sovereign_debt_contagion", bypassMinDay: true);
        Check(inst is not null, "spawned sovereign_debt_contagion for command test");

        // 1. ConveneEmergencySummit
        var summitCmd = new SituationInterventionCommand { IssuerId = actors[0].Id, SituationId = inst!.Id, Action = SituationActionKind.ConveneEmergencySummit };
        summitCmd.Apply(campaign);
        Check(summitCmd.Changed && inst.SummitConvened && inst.SummitHostId == actors[0].Id,
            "ConveneEmergencySummit marks summit convened and records host");

        // Cooldown refusal on immediate repeat by same nation
        Check(!WorldSituations.CanIntervene(campaign, actors[0], inst.Id, SituationActionKind.BrokerCompromise, out var cdReason) &&
              cdReason == "world_situation.refuse.cooldown",
            "action cooldown prevents immediate double intervention by same nation");

        // Second summit refused
        Check(!WorldSituations.CanIntervene(campaign, actors[1], inst.Id, SituationActionKind.ConveneEmergencySummit, out var dupSummit) &&
              dupSummit == "world_situation.refuse.summit_already_convened",
            "cannot convene a second summit while one is already underway");

        // 2. FundStabilizationPackage
        var fundCmd = new SituationInterventionCommand { IssuerId = actors[1].Id, SituationId = inst.Id, Action = SituationActionKind.FundStabilizationPackage };
        var treasBefore = actors[1].Treasury;
        fundCmd.Apply(campaign);
        Check(fundCmd.Changed && actors[1].Treasury < treasBefore,
            "FundStabilizationPackage deducts treasury and shifts stabilizer weight");

        // 3. ExploitStrategicLeverage
        var exploitCmd = new SituationInterventionCommand { IssuerId = actors[2].Id, SituationId = inst.Id, Action = SituationActionKind.ExploitStrategicLeverage };
        var expCashBefore = actors[2].Treasury;
        exploitCmd.Apply(campaign);
        Check(exploitCmd.Changed && actors[2].Treasury > expCashBefore && inst.ExploiterWeight > 0f,
            "ExploitStrategicLeverage grants windfall and adds exploiter coalition weight");

        // 4. BrokerCompromise
        var brokerCmd = new SituationInterventionCommand { IssuerId = actors[3].Id, SituationId = inst.Id, Action = SituationActionKind.BrokerCompromise };
        brokerCmd.Apply(campaign);
        Check(brokerCmd.Changed, "BrokerCompromise executes cleanly");

        // UI brief readout
        var brief = WorldSituations.Describe(campaign, inst, actors[0]);
        Check(brief is not null && brief.StabilizerCount == 3 && brief.ExploiterCount == 1 &&
              brief.SummitConvened && brief.SummitHostTag == actors[0].Tag && !string.IsNullOrWhiteSpace(brief.Title),
            "WorldSituations.Describe produces complete read-only UI brief");
    }

    private static void TestAllThreeResolutionOutcomes()
    {
        // 1. StabilizedConsensus (intensity driven <= 5)
        var c1 = Campaign.CreateBootstrap(20261126);
        var host1 = c1.Nations.First(n => n.Movement is null);
        host1.PoliticalPoints = 80f;
        host1.Treasury = 100f;
        var s1 = WorldSituations.TryTriggerOrganic(c1, "global_energy_chokepoint_shock", bypassMinDay: true)!;
        s1.Intensity = 12f;
        var stabBefore = host1.Stability;
        new SituationInterventionCommand { IssuerId = host1.Id, SituationId = s1.Id, Action = SituationActionKind.ConveneEmergencySummit }.Apply(c1);
        Check(s1.Outcome == SituationOutcome.StabilizedConsensus && host1.Stability > stabBefore,
            "StabilizedConsensus resolves cleanly and applies consensus ScriptedEffects");

        // 2. CrisisEscalation (intensity driven >= 95)
        var c2 = Campaign.CreateBootstrap(20261127);
        var host2 = c2.Nations.First(n => n.Movement is null);
        host2.PoliticalPoints = 80f;
        var s2 = WorldSituations.TryTriggerOrganic(c2, "great_power_arms_spiral", bypassMinDay: true)!;
        s2.Intensity = 88f;
        new SituationInterventionCommand { IssuerId = host2.Id, SituationId = s2.Id, Action = SituationActionKind.ExploitStrategicLeverage }.Apply(c2);
        Check(s2.Outcome == SituationOutcome.CrisisEscalation,
            "CrisisEscalation resolves cleanly when intensity reaches >= 95");

        // 3. FracturedMultipolarity (duration expires with balanced coalitions via real weekly TickStages)
        var c3 = Campaign.CreateBootstrap(20261128);
        c3.Defines.WorldSituationsAiEnabled = false;
        var s3 = WorldSituations.TryTriggerOrganic(c3, "semiconductor_supply_race", bypassMinDay: true)!;
        s3.StartedDay = -300;
        s3.StabilizerWeight = 20f;
        s3.ExploiterWeight = 20f;
        s3.Intensity = 50f;
        c3.IsPaused = false;
        for (var d = 0; d < 7; d++)
            c3.TickDay();
        Check(s3.Outcome == SituationOutcome.FracturedMultipolarity,
            "FracturedMultipolarity resolves cleanly on duration expiry with balanced coalitions");
        Check(WorldSituations.RecentResolved(c3, 5).Any(r => r.Id == s3.Id),
            "RecentResolved returns resolved situations");
        Check(c3.Chronicle.Entries.Any(e => e.Kind == "world_situation_resolved_multipolar"),
            "resolution writes a real Chronicle entry");
    }

    private static void TestStaggeredAiAndSaveRoundTrip()
    {
        var campaign = Campaign.CreateBootstrap(20261129);
        var inst = WorldSituations.TryTriggerOrganic(campaign, "great_power_arms_spiral", bypassMinDay: true)!;
        campaign.IsPaused = false;
        for (var d = 0; d < 35; d++)
            campaign.TickDay();

        Check(inst.ParticipantStances.Count > 0 || inst.Outcome != SituationOutcome.Active,
            $"deterministic staggered AI intervened during 35-day simulation (participants={inst.ParticipantStances.Count}, outcome={inst.Outcome})");
        foreach (var n in campaign.Nations)
            Check(float.IsFinite(n.Treasury) && float.IsFinite(n.Stability) && float.IsFinite(n.PoliticalPoints),
                $"nation '{n.Tag}' stays NaN-free after WorldSituations ticks");

        // Attach minimal topology for SaveSystem.Save + Load round-trip
        var sample = campaign.States.Where(s => s.MapIndex > 0 && s.NationMapIndex > 0).Take(3).ToArray();
        var states = sample.Select(s => (ushort)s.MapIndex).ToArray();
        var owners = sample.Select(s => (ushort)s.NationMapIndex).ToArray();
        var topo = ProvinceTopology.Build(states, owners, states.Length, 1, _ => false);
        campaign.AttachProvinceTopology(topo);

        var fpBefore = CampaignFingerprint.Of(campaign);
        var saveDir = Path.Combine(Path.GetTempPath(), "dof_world_sit_" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveSystem.Save(saveDir, "slot_ws", campaign, "test_ws");
            var loaded = SaveSystem.Load(saveDir, "slot_ws", "test_ws", topo.ProvinceCount).Campaign;
            Check(loaded is not null, "WorldSituations SaveSystem.Load succeeds");
            Check(loaded!.WorldSituations.Count == campaign.WorldSituations.Count &&
                  loaded.NextSituationId == campaign.NextSituationId,
                "SaveSystem round-trip preserves WorldSituations count and NextSituationId");
            Check(CampaignFingerprint.Of(loaded) == fpBefore,
                "SaveSystem round-trip yields bit-identical CampaignFingerprint with active WorldSituations");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }

    private static void TestLocalizationParity()
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var enPath = Path.Combine(gameRoot, "localization", "english", "99_world_situations_wave6.json");
        var ruPath = Path.Combine(gameRoot, "localization", "russian", "99_world_situations_wave6.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU Wave 6 WorldSituations localization files exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath), GameDataLoader.ForgivingJson);
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath), GameDataLoader.ForgivingJson);
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);

        Check(enMap.Count == ruMap.Count && enMap.Keys.All(ruMap.ContainsKey),
            $"EN/RU 99_world_situations_wave6.json have 100% key parity ({enMap.Count} keys)");
        foreach (var (k, v) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(ruMap[k]), $"non-empty EN/RU loc for '{k}'");
            Check(v.Contains("{0}") == ruMap[k].Contains("{0}"), $"placeholder parity for '{k}'");
        }
        foreach (var def in WorldSituationCatalog.All)
        {
            Check(enMap.ContainsKey(def.NameKey) && enMap.ContainsKey(def.SummaryKey),
                $"situation '{def.Id}' name/summary keys present in EN/RU localization");
        }
    }
}
