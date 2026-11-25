using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MainlandReclamationTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL mainland-reclamation: " + label);
        Console.WriteLine("PASS mainland-reclamation: " + label);
    }

    static void SetElapsedDays(Campaign c, int days)
    {
        var prop = typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours));
        prop!.SetValue(c, (long)days * 24L);
    }

    [TestSuite("--mainland-reclamation-only",
        Description = "Taiwan cross-strait Mainland reclamation: data, geography, bridgehead/rallies, caps, defections, junior-ally peace guard, and save/fingerprint determinism.")]
    public static void Run()
    {
        MapRasterCatalog.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        FormableNations.ResetCatalog();
        AiStrategyCatalog.ResetCatalog();
        Loc.ResetCatalog();

        var c = Campaign.CreateBootstrap(20260830);
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapErr),
            "2024 world map loads cleanly: " + mapErr);
        var pristineMap = map.CloneForOverrides();
        c.AttachProvinceTopology(map);

        var twn = c.NationByTag("TWN");
        var chn = c.NationByTag("CHN");
        var rus = c.NationByTag("RUS");
        var jpn = c.NationByTag("JPN");
        Check(twn is not null && chn is not null && rus is not null && jpn is not null,
            "core scenario nations (TWN, CHN, RUS, JPN) exist");

        // 1. Authored setup, cores, claims, fleets, marines, strategy, decisions, modifiers, events, formable, and EN/RU loc.
        var fujian = c.States.FirstOrDefault(s => string.Equals(s.Id, "cn_fj", StringComparison.OrdinalIgnoreCase));
        Check(fujian is not null && TerritorialClaims.TitleOf(c, twn, fujian) == TerritorialTitle.Homeland,
            "Taiwan holds a homeland core on Fujian (cn_fj)");
        foreach (var claimState in new[] { "cn_gd", "cn_zj", "cn_js", "cn_ah", "cn_jx", "cn_bj" })
        {
            var st = c.States.FirstOrDefault(s => string.Equals(s.Id, claimState, StringComparison.OrdinalIgnoreCase));
            Check(st is not null && TerritorialClaims.TitleOf(c, twn, st) == TerritorialTitle.Claim,
                "Taiwan holds constitutional claim on " + claimState);
        }

        Check(AiStrategyCatalog.ProfileOf(twn.Tag) == "maritime_reclaimer",
            "Taiwan uses the maritime_reclaimer AI operational character");

        // Rebalanced Defines agree between C# defaults and shipped game/common/defines/99_mainland_reclamation.json.
        Check(c.Defines.MainlandBridgeheadRallyDivisions == 2 &&
              c.Defines.MainlandRegionRallyDivisions == 1 &&
              c.Defines.MainlandRallyMaxDivisionsPerWar == 18 &&
              Math.Abs(c.Defines.AiNarrowStraitInvasionOdds - 1.35f) < 0.001f &&
              c.Defines.AiMainlandReturnEarliestDay == 540 &&
              Math.Abs(c.Defines.AiMainlandReturnChance - 0.12f) < 0.001f &&
              c.Defines.AiMainlandReturnMinimumDivisions == 22 &&
              Math.Abs(c.Defines.MainlandDefectionChanceBonus - 0.06f) < 0.001f,
            "Mainland reclamation defines reflect rebalanced fair thresholds");

        // AI war-window fairness: Pacific backing + prepared doctrine never open offensive window against calm PRC.
        {
            var fair = Campaign.CreateBootstrap(20260830);
            fair.AttachProvinceTopology(map.CloneForOverrides());
            var ftwn = fair.NationByTag("TWN")!;
            var fchn = fair.NationByTag("CHN")!;
            SetElapsedDays(fair, 600);
            ModifierSystem.Grant(fair, ftwn, "twn_project_national_glory_spirit", 730);
            Check(MainlandReclamation.AiLaunchChance(fair, ftwn, fchn, out _) == 0f,
                "calm peaceful PRC never triggers AI Taiwan launch window even with Pacific backing & Project National Glory");
            fchn.Stability = 0.46f;
            Check(MainlandReclamation.AiLaunchChance(fair, ftwn, fchn, out _) == 0f,
                "PRC at 46% stability at peace keeps AI invasion window closed");
            fchn.Stability = 0.32f;
            fchn.WarSupport = 0.32f;
            Check(MainlandReclamation.AiLaunchChance(fair, ftwn, fchn, out _) > 0f,
                "genuinely strained PRC opens bounded AI Mainland reclamation window");
        }

        var twnHulls = c.Fleets.Where(f => f.NationId == twn.Id).Sum(f => f.Hulls);
        var twnMarines = c.Armies.Count(a => a.NationId == twn.Id && a.TemplateId == "marine_brigade");
        Check(twnHulls >= 70, $"Taiwan starts with substantial strait lift fleet ({twnHulls} hulls)");
        Check(twnMarines >= 2, $"Taiwan starts with amphibious marine brigades ({twnMarines})");

        foreach (var decId in new[] { "twn_project_national_glory", "twn_strait_armada", "twn_mainland_underground" })
            Check(DecisionCatalog.Find(decId) is not null, "decision loads: " + decId);
        foreach (var modId in new[] {
            "twn_project_national_glory_spirit", "twn_strait_armada_spirit",
            "twn_mainland_underground_spirit", "mainland_return_momentum", "mainland_regime_shock" })
            Check(ModifierCatalog.Get(modId) is not null, "modifier loads: " + modId);
        foreach (var evId in new[] { "mr_twn_national_glory_council", "mr_twn_strait_window_briefing", "mr_chn_coastal_ferment" })
            Check(EventCatalog.All.Any(e => e.Id == evId), "event loads: " + evId);

        var chinaReunified = FormableNations.Find("china_reunified");
        Check(chinaReunified is not null && Math.Abs(chinaReunified.MinimumComponentShare - 0.65f) < 0.001f && chinaReunified.MinimumStability <= 0.50f,
            "china_reunified uses 0.65 component share and achievable stability gate");
        Check(!FormableNations.Evaluate(c, chn, chinaReunified).CanForm,
            "Mainland China alone still cannot proclaim china_reunified without Taiwan");
        Check(!FormableNations.Evaluate(c, twn, chinaReunified).CanForm,
            "Taiwan alone cannot proclaim china_reunified without Mainland China");

        var savedLang = Loc.Language;
        try
        {
            foreach (var lang in new[] { "english", "russian" })
            {
                Loc.Language = lang;
                foreach (var key in new[] {
                    "mr.mod.national_glory.name", "mr.mod.return_momentum.name", "mr.mod.regime_shock.name",
                    "mr.dec.national_glory.name", "mr.dec.strait_armada.name", "mr.dec.mainland_underground.name",
                    "mr.ev.glory_council.title", "mr.ev.strait_window.title", "mr.ev.coastal_ferment.title",
                    "chronicle.mainland_campaign_launched", "chronicle.mainland_bridgehead",
                    "chronicle.mainland_rally", "chronicle.mainland_defection" })
                {
                    Check(Loc.Has(key) && !string.IsNullOrWhiteSpace(Loc.Get(key)),
                        $"localization ({lang}) has '{key}'");
                }
            }
        }
        finally { Loc.Language = savedLang; }

        // 2. Geography & Mainland liberator predicate edge cases.
        Check(MainlandReclamation.IsSeparatedBySea(c, twn, chn),
            "Taiwan and Mainland China are detected as sea-separated despite kinmen_ferry sea crossing");
        Check(!MainlandReclamation.IsSeparatedBySea(c, chn, rus),
            "China and Russia share a true dry-land border (not sea-separated)");

        var dryWar = new StrategicWar { Id = 9001, AttackerId = rus.Id, DefenderId = chn.Id, StartedDay = 1 };
        Check(!MainlandReclamation.IsMainlandLiberator(c, dryWar, rus, chn),
            "dissimilar dry-land neighbors (RUS vs CHN) do not qualify as divided-homeland liberators");
        Check(MainlandReclamation.TryGetDividedRivalPair(c, twn, chn, out var pairContender, out var pairHolder) &&
              pairContender == twn && pairHolder == chn &&
              MainlandReclamation.TryGetDividedRivalPair(c, chn, twn, out pairContender, out pairHolder) &&
              pairContender == twn && pairHolder == chn,
            "TryGetDividedRivalPair identifies contender and holder in either argument order");
        Check(!MainlandReclamation.TryGetDividedRivalPair(c, rus, chn, out _, out _),
            "TryGetDividedRivalPair rejects unrelated neighbors");

        foreach (var decId in new[] { "twn_project_national_glory", "twn_strait_armada", "twn_mainland_underground" })
            Check(DecisionCatalog.Find(decId) is { Trigger.HasTagFilter: false }, $"decision '{decId}' uses organic triggers without hardcoded tags");
        foreach (var evId in new[] { "mr_twn_national_glory_council", "mr_twn_strait_window_briefing", "mr_chn_coastal_ferment" })
        {
            var ev = EventCatalog.All.First(e => e.Id == evId);
            Check(ev.Trigger is { HasTagFilter: false }, $"event '{evId}' uses organic triggers without hardcoded tags");
        }

        var civilWar = new StrategicWar { Id = 9002, AttackerId = twn.Id, DefenderId = chn.Id, StartedDay = 1, CivilConflict = true };
        Check(!MainlandReclamation.IsMainlandLiberator(c, civilWar, twn, chn),
            "internal civil conflicts do not trigger Mainland reclamation");

        // 3. Bridgehead, liberation rally, idempotency, save/load fingerprint, and per-war division cap.
        c.IsPaused = false;
        var war = CampaignDiplomacy.DeclareWar(c, twn.Id, chn.Id, fujian.MapIndex, "reconquest");
        Check(war is not null, "Taiwan can declare reconquest war on China over Fujian");
        Check(MainlandReclamation.IsMainlandLiberator(c, war, twn, chn),
            "Taiwan qualifies as Mainland liberator against China");
        Check(!MainlandReclamation.IsMainlandLiberator(c, war, chn, twn),
            "China does not qualify as Mainland liberator against Taiwan");

        // Reintegration full-conquest annexation works across the strait (> 600 km capital distance).
        Check(Reintegration.WantsFullConquestAnnexation(c, war, twn, chn, 85f, 0.70f, 0.70f),
            "Taiwan reunification war qualifies for full conquest annexation despite Beijing > 600 km away");

        // Find Fujian provinces belonging to CHN.
        var fujianProvs = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => !map.WaterByProvince[p] && map.ProvinceToState[p] == fujian.MapIndex && map.ProvinceToNation[p] == chn.MapIndex)
            .ToArray();
        Check(fujianProvs.Length >= 2, $"Fujian has {fujianProvs.Length} land provinces");

        var beachProvince = fujianProvs[0];
        var inlandState = c.States.First(s => s.NationMapIndex == chn.MapIndex && s.MapIndex != fujian.MapIndex);
        var fujianRadBefore = fujian.Radicalism;
        var inlandRadBefore = inlandState.Radicalism;
        var divsBeforeBridgehead = c.Armies.Count(a => a.NationId == twn.Id);
        var chnStabBefore = chn.Stability;
        c.ProvinceController[beachProvince] = (ushort)twn.MapIndex;

        MainlandReclamation.Tally.Clear();
        MainlandReclamation.Tick(c);

        Check(MainlandReclamation.Tally.GetValueOrDefault("bridgehead_secured") == 1,
            "occupying a mainland province establishes a Mainland bridgehead");
        Check(MainlandReclamation.Tally.GetValueOrDefault("region_rallied") == 0,
            "a single beach province does not trigger regional liberation rally");
        Check(ModifierSystem.Has(twn, "mainland_return_momentum"),
            "bridgehead grants mainland_return_momentum modifier to Taiwan");
        Check(ModifierSystem.Has(chn, "mainland_regime_shock"),
            "bridgehead inflicts mainland_regime_shock on China");
        Check(c.Armies.Count(a => a.NationId == twn.Id) > divsBeforeBridgehead,
            "bridgehead spawns initial volunteer formation on the liberated beach");
        Check(chn.Stability < chnStabBefore, "bridgehead shocks rival stability");
        Check(fujian.Radicalism - fujianRadBefore >= c.Defines.MainlandLiberationRadicalismSurge * 0.9f &&
              inlandState.Radicalism - inlandRadBefore <= c.Defines.MainlandLiberationRadicalismSurge * 0.25f,
            "bridgehead radicalism surge hits contested state at full rate and uncontested inland states at 20%");

        // Idempotency: ticking again must NOT duplicate the bridgehead.
        var divsAfterBridgehead = c.Armies.Count(a => a.NationId == twn.Id);
        MainlandReclamation.Tick(c);
        Check(MainlandReclamation.Tally.GetValueOrDefault("bridgehead_secured") == 1,
            "bridgehead fires only once per war");
        Check(c.Armies.Count(a => a.NationId == twn.Id) == divsAfterBridgehead,
            "re-ticking without new liberated states does not duplicate divisions");

        // Below-60% occupation (even >= 2 provinces) must NOT trigger regional rally.
        var subMajorityCount = Math.Max(1, (int)Math.Ceiling(fujianProvs.Length * 0.60) - 1);
        for (var idx = 0; idx < subMajorityCount; idx++)
            c.ProvinceController[fujianProvs[idx]] = (ushort)twn.MapIndex;
        MainlandReclamation.Tick(c);
        Check(MainlandReclamation.Tally.GetValueOrDefault("region_rallied") == 0,
            $"holding {subMajorityCount}/{fujianProvs.Length} (<60%) of Fujian does not spark regional volunteer rally");

        // Liberate Fujian (>= 60% majority) -> triggers regional volunteer rally once.
        foreach (var p in fujianProvs) c.ProvinceController[p] = (ushort)twn.MapIndex;
        MainlandReclamation.Tick(c);
        Check(MainlandReclamation.Tally.GetValueOrDefault("region_rallied") == 1,
            "liberating Fujian (>= 60%) triggers a regional volunteer rally");
        var divsAfterFujian = c.Armies.Count(a => a.NationId == twn.Id);
        Check(divsAfterFujian > divsAfterBridgehead, "Fujian liberation raises local volunteers");

        // Save/Load round-trip & deterministic CampaignFingerprint equality.
        var fpBeforeSave = CampaignFingerprint.Of(c);
        var blob = SaveSystem.ExportNetworkSnapshot(c, "mainland_reclamation_test");
        var loaded = SaveSystem.ImportNetworkSnapshot(blob, "mainland_reclamation_test");
        Check(loaded.Success && loaded.Campaign is not null, "campaign with active Mainland reclamation saves and loads cleanly");
        loaded.Campaign.AttachProvinceTopology(map.CloneForOverrides());
        var fpAfterLoad = CampaignFingerprint.Of(loaded.Campaign);
        Check(fpBeforeSave == fpAfterLoad, "CampaignFingerprint is identical across save/load round-trip");

        // Ticking the reloaded campaign must NOT double-fire bridgehead or Fujian rally.
        MainlandReclamation.Tally.Clear();
        MainlandReclamation.Tick(loaded.Campaign);
        Check(MainlandReclamation.Tally.GetValueOrDefault("bridgehead_secured") == 0 &&
              MainlandReclamation.Tally.GetValueOrDefault("region_rallied") == 0,
            "reloaded campaign remembers already-fired bridgehead and regional rally markers");

        // 4. Junior coalition member cannot hijack whole-war peace negotiation during partial occupation.
        war.AttackerAllies.Add(jpn.Id);
        war.StartedDay = -500;
        war.LastPeaceProposalDay = -500;
        war.WarScore = 55f;
        AiStatecraft.Negotiate(c, jpn, war);
        Check(!war.Ended && war.PendingOfferFromId < 0,
            "junior coalition ally cannot end or negotiate away the war leader's Mainland campaign");

        // Cap enforcement: occupy many CHN states and verify spawned divisions never exceed MainlandRallyMaxDivisionsPerWar.
        var cap = c.Defines.MainlandRallyMaxDivisionsPerWar;
        var divsBeforeCapTest = c.Armies.Count(a => a.NationId == twn.Id);
        foreach (var p in Enumerable.Range(1, map.ProvinceCount))
            if (!map.WaterByProvince[p] && map.ProvinceToNation[p] == chn.MapIndex)
                c.ProvinceController[p] = (ushort)twn.MapIndex;
        MainlandReclamation.Tick(c);
        var totalSpawnedInWar = c.Armies.Count(a => a.NationId == twn.Id) - divsBeforeBridgehead;
        Check(totalSpawnedInWar <= cap,
            $"volunteer rallies strictly respect per-war division cap (spawned={totalSpawnedInWar}, cap={cap})");
        Check(c.Armies.Count(a => a.NationId == twn.Id) >= divsBeforeCapTest,
            "additional liberated states raise volunteers up to the cap");

        // 5. Reunification defection flips low-cohesion Mainland garrison to Taiwan.
        var defectState = c.States.FirstOrDefault(s => string.Equals(s.Id, "cn_gd", StringComparison.OrdinalIgnoreCase));
        Check(defectState is not null, "Guangdong state exists for defection check");
        var defectProv = Enumerable.Range(1, map.ProvinceCount)
            .First(p => !map.WaterByProvince[p] && map.ProvinceToState[p] == defectState.MapIndex && map.ProvinceToNation[p] == chn.MapIndex);
        c.ProvinceController[defectProv] = (ushort)chn.MapIndex;
        c.Armies.RemoveAll(a => a.ProvinceId == defectProv);
        var nextId = c.Armies.Count > 0 ? c.Armies.Max(a => a.Id) + 1 : 1;
        var garrisons = new Army[4];
        for (var k = 0; k < garrisons.Length; k++)
        {
            garrisons[k] = new Army
            {
                Id = nextId + k,
                NationId = chn.Id,
                ProvinceId = defectProv,
                Divisions = 1f,
                Strength = 0.45f,
                Cohesion = 0.25f,
                SupplyLevel = 0.25f,
                Personnel = 6_000,
                MaxPersonnel = 15_000,
                TemplateId = "line_infantry"
            };
            c.Armies.Add(garrisons[k]);
        }
        defectState.Stability = 0.15f;
        defectState.Radicalism = 0.75f;
        chn.Stability = 0.15f;
        chn.WarSupport = 0.15f;
        twn.Stability = 0.85f;
        twn.WarSupport = 0.90f;
        c.Defines.SecessionDefectionShare = 1f;
        var defectionChance = StrandedGarrisons.DefectionChance(c, chn, twn, garrisons[0], 1f);
        Check(defectionChance >= 0.85f,
            $"demoralized Mainland garrison reaches high defection odds ({defectionChance:F2})");
        var flipped = Reintegration.Defect(c, chn, twn, new[] { defectProv });
        Check(flipped >= 1 && garrisons.Any(g => g.NationId == twn.Id),
            "demoralized Mainland garrison defects to Taiwan in reunification war");

        // 6. End-to-end Reunified China proclamation once Taiwan owns Mainland China at peace.
        war.Ended = true;
        war.EndedDay = c.ElapsedDays;
        foreach (var p in Enumerable.Range(1, map.ProvinceCount))
        {
            if (!map.WaterByProvince[p] && map.ProvinceToNation[p] == chn.MapIndex)
            {
                map.ProvinceToNation[p] = (ushort)twn.MapIndex;
                c.ProvinceController[p] = (ushort)twn.MapIndex;
            }
        }
        foreach (var st in c.States)
            if (st.NationMapIndex == chn.MapIndex) st.NationMapIndex = twn.MapIndex;
        c.MarkStrategicChange();
        twn.Stability = 0.30f;
        twn.Treasury = Math.Max(twn.Treasury, twn.Gdp * 0.05f + 50f);
        var eval = FormableNations.Evaluate(c, twn, chinaReunified);
        Check(eval.CanForm,
            "Taiwan can proclaim china_reunified at 30% stability after conquering Mainland China: " +
            string.Join("; ", eval.Requirements.Where(r => !r.Met).Select(r => r.Label)));
        new FormNationCommand { IssuerId = twn.Id, FormableId = "china_reunified" }.Apply(c);
        Check(string.Equals(twn.FormedNationId, "china_reunified", StringComparison.OrdinalIgnoreCase),
            "FormNationCommand proclaims Reunified China for Taiwan");

        // 7. Frontline army defection & provincial collapse defection fairness guards.
        VerifyDefectionAndCollapseFairness(pristineMap);
    }

    static void VerifyDefectionAndCollapseFairness(ProvinceTopology map)
    {
        // A. Frontline army defection guards (Defections.cs).
        {
            var c = Campaign.CreateBootstrap(20260830);
            c.AttachProvinceTopology(map.CloneForOverrides());
            c.IsPaused = false;
            var twn = c.NationByTag("TWN")!;
            var chn = c.NationByTag("CHN")!;
            var fujian = c.States.First(s => string.Equals(s.Id, "cn_fj", StringComparison.OrdinalIgnoreCase));
            var war = CampaignDiplomacy.DeclareWar(c, twn.Id, chn.Id, fujian.MapIndex, "reconquest")!;

            var fujianProv = 0;
            var adjProv = 0;
            for (var p = 1; p <= map.ProvinceCount && adjProv == 0; p++)
            {
                if (map.IsWater(p) || map.ProvinceToState[p] != fujian.MapIndex || map.ProvinceToNation[p] != chn.MapIndex) continue;
                foreach (var nb in map.LandNeighbors[p])
                {
                    if (nb > 0 && nb <= map.ProvinceCount && !map.IsWater(nb) && map.ProvinceToNation[nb] == chn.MapIndex && nb != p)
                    {
                        fujianProv = p;
                        adjProv = nb;
                        break;
                    }
                }
            }
            Check(fujianProv > 0 && adjProv > 0, "found mainland Fujian province with dry-land CHN neighbor");
            c.ProvinceController[fujianProv] = (ushort)twn.MapIndex;
            MainlandReclamation.Tick(c);
            Check(ModifierSystem.Has(chn, "mainland_regime_shock"), "mainland_regime_shock active for Defections fairness check");

            c.Armies.RemoveAll(a => a.ProvinceId == adjProv);
            var nextId = c.Armies.Count > 0 ? c.Armies.Max(a => a.Id) + 1 : 1;
            var testArmies = new List<Army>();
            for (var i = 0; i < 120; i++)
            {
                var army = new Army
                {
                    Id = nextId + i,
                    NationId = chn.Id,
                    ProvinceId = adjProv,
                    Divisions = 1f,
                    Strength = 0.90f,
                    Cohesion = 0.85f,
                    SupplyLevel = 0.85f,
                    Personnel = 12_000,
                    MaxPersonnel = 15_000,
                    TemplateId = "line_infantry"
                };
                testArmies.Add(army);
                c.Armies.Add(army);
            }

            // Healthy cohesive PRC armies never defect purely from mainland_regime_shock.
            chn.Stability = 0.55f;
            chn.WarSupport = 0.55f;
            war.WarScore = 0f;
            Check(Defections.Tick(c, 30) == 0, "cohesive PRC armies at 55% stability never defect from regime shock alone");

            // Even under low stability/WS, cohesive (>= 0.55) armies stay loyal.
            chn.Stability = 0.25f;
            chn.WarSupport = 0.30f;
            war.WarScore = 25f;
            foreach (var a in testArmies) a.Cohesion = 0.65f;
            Check(Defections.Tick(c, 60) == 0, "cohesive (>= 0.55) frontline armies stay loyal even under strain");

            // Low-cohesion armies still require war score >= 10 before non-exhausted defection.
            foreach (var a in testArmies) a.Cohesion = 0.40f;
            war.WarScore = 5f;
            Check(Defections.Tick(c, 90) == 0, "non-exhausted reunification defection requires rebelScore >= 10");

            // When all criteria hold (Stability < 0.30, WarSupport < 0.35, Cohesion < 0.55, WarScore >= 10),
            // defections can occur but remain capped at <= 8% odds.
            war.WarScore = 40f;
            ModifierSystem.Grant(c, twn, "twn_mainland_underground_spirit", 365);
            var defected = Defections.Tick(c, 120);
            var flippedCount = testArmies.Count(a => a.NationId == twn.Id);
            Check(defected > 0 && flippedCount <= 22,
                $"reunification frontline defection occurs only on collapsing cohesion/morale and obeys 8% cap ({flippedCount}/120)");
        }

        // B. Provincial defection prerequisite & smallest-population-first order.
        {
            var c = Campaign.CreateBootstrap(20260830);
            c.AttachProvinceTopology(map.CloneForOverrides());
            c.IsPaused = false;
            var twn = c.NationByTag("TWN")!;
            var chn = c.NationByTag("CHN")!;
            var fujian = c.States.First(s => string.Equals(s.Id, "cn_fj", StringComparison.OrdinalIgnoreCase));
            var guangdong = c.States.First(s => string.Equals(s.Id, "cn_gd", StringComparison.OrdinalIgnoreCase));
            var jiangxi = c.States.First(s => string.Equals(s.Id, "cn_jx", StringComparison.OrdinalIgnoreCase));
            var war = CampaignDiplomacy.DeclareWar(c, twn.Id, chn.Id, fujian.MapIndex, "reconquest")!;

            // Pick 6 non-adjacent or distant CHN states (excluding Guangdong & Jiangxi so both remain adjacent rival candidates to Fujian).
            var fujianProvs = Enumerable.Range(1, map.ProvinceCount)
                .Where(p => !map.WaterByProvince[p] && map.ProvinceToState[p] == fujian.MapIndex && map.ProvinceToNation[p] == chn.MapIndex)
                .ToArray();
            var otherStates = c.States
                .Where(s => s.NationMapIndex == chn.MapIndex && s.MapIndex != fujian.MapIndex &&
                            s.MapIndex != guangdong.MapIndex && s.MapIndex != jiangxi.MapIndex &&
                            s.MapIndex != chn.CapitalStateMapIndex)
                .OrderBy(s => s.MapIndex)
                .Take(5)
                .ToArray();
            Check(otherStates.Length == 5, "found 5 extra CHN states for provincial defection threshold check");

            // 6 provinces across 6 states (< 8 provinces) must NOT trigger provincial defection.
            foreach (var p in fujianProvs) c.ProvinceController[p] = (ushort)twn.MapIndex;
            for (var i = 0; i < 4; i++)
            {
                var p = Enumerable.Range(1, map.ProvinceCount)
                    .First(pr => !map.WaterByProvince[pr] && map.ProvinceToState[pr] == otherStates[i].MapIndex && map.ProvinceToNation[pr] == chn.MapIndex);
                c.ProvinceController[p] = (ushort)twn.MapIndex;
            }
            // Reset to exactly 6 provinces across 6 states by restoring all but one fujian prov.
            for (var i = 1; i < fujianProvs.Length; i++) c.ProvinceController[fujianProvs[i]] = (ushort)chn.MapIndex;
            var fifthProv = Enumerable.Range(1, map.ProvinceCount)
                .First(pr => !map.WaterByProvince[pr] && map.ProvinceToState[pr] == otherStates[4].MapIndex && map.ProvinceToNation[pr] == chn.MapIndex);
            c.ProvinceController[fifthProv] = (ushort)twn.MapIndex;

            chn.Stability = 0.10f;
            SetElapsedDays(c, 15);
            MainlandReclamation.Tally.Clear();
            MainlandReclamation.Tick(c);
            Check(MainlandReclamation.Tally.GetValueOrDefault("provincial_defection") == 0,
                "fewer than 8 held provinces cannot trigger provincial defection even at 10% stability");

            // Restore 5th state so only 5 states are held, while holding >= 8 provinces across those 5 states -> blocked by heldStateCount < 6.
            c.ProvinceController[fifthProv] = (ushort)chn.MapIndex;
            foreach (var p in fujianProvs) c.ProvinceController[p] = (ushort)twn.MapIndex;
            var totalHeldNow = Enumerable.Range(1, map.ProvinceCount)
                .Count(p => !map.WaterByProvince[p] && map.ProvinceToNation[p] == chn.MapIndex && c.ProvinceController[p] == (ushort)twn.MapIndex);
            if (totalHeldNow < 8)
            {
                foreach (var p in Enumerable.Range(1, map.ProvinceCount)
                             .Where(pr => !map.WaterByProvince[pr] && map.ProvinceToState[pr] == otherStates[0].MapIndex && map.ProvinceToNation[pr] == chn.MapIndex)
                             .Take(8 - totalHeldNow))
                    c.ProvinceController[p] = (ushort)twn.MapIndex;
            }
            SetElapsedDays(c, 45);
            MainlandReclamation.Tick(c);
            Check(MainlandReclamation.Tally.GetValueOrDefault("provincial_defection") == 0,
                "fewer than 6 contested states cannot trigger provincial defection");

            // Now add the 6th state (>= 8 provinces across >= 6 states), check stability ceiling (> 0.22 blocked), then collapse (0.10) triggers smallest-population-first.
            c.Armies.RemoveAll(a => a.NationId == chn.Id);
            c.ProvinceController[fifthProv] = (ushort)twn.MapIndex;
            for (var step = 0; step < 4; step++)
            {
                chn.Stability = 0.30f;
                SetElapsedDays(c, 75 + step * 30);
                MainlandReclamation.Tick(c);
            }
            Check(MainlandReclamation.Tally.GetValueOrDefault("provincial_defection") == 0,
                "provincial defection stays blocked above stability collapse ceiling");

            chn.Stability = 0.10f;
            var twnCultures = new HashSet<string>(DemographyCatalog.PrimaryCultures(twn.Tag), StringComparer.OrdinalIgnoreCase);
            foreach (var fc in twn.FoundedCultures) twnCultures.Add(fc);
            var eligibleAdjacent = new List<ScenarioState>();
            var adjStateIdx = new HashSet<int>();
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (c.ProvinceController[p] != (ushort)twn.MapIndex || map.IsWater(p)) continue;
                foreach (var next in map.LandNeighbors[p])
                {
                    if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                    if (map.ProvinceToNation[next] != (ushort)chn.MapIndex || c.ProvinceController[next] != (ushort)chn.MapIndex) continue;
                    var st = map.ProvinceToState[next];
                    if (st > 0) adjStateIdx.Add(st);
                }
            }
            foreach (var st in c.States)
            {
                if (st.NationMapIndex != chn.MapIndex || !adjStateIdx.Contains(st.MapIndex)) continue;
                if (TerritorialClaims.TitleOf(c, twn, st) != TerritorialTitle.None || WarAims.CultureShare(st, twnCultures) >= 0.45f)
                    eligibleAdjacent.Add(st);
            }
            var smallestEligible = eligibleAdjacent.OrderBy(s => s.Population).ThenBy(s => s.MapIndex).First();
            Check(smallestEligible.Population < guangdong.Population,
                $"smaller eligible adjacent state ({smallestEligible.Name}: {smallestEligible.Population}) exists vs Guangdong ({guangdong.Population})");

            var seenSeq = c.Chronicle.Entries.LastOrDefault()?.Sequence ?? 0L;
            for (var step = 0; step < 12 && MainlandReclamation.Tally.GetValueOrDefault("provincial_defection") == 0; step++)
            {
                SetElapsedDays(c, 195 + step * 30);
                MainlandReclamation.Tick(c);
            }
            Check(MainlandReclamation.Tally.GetValueOrDefault("provincial_defection") == 1,
                "provincial defection fires once collapse + foothold thresholds are met");
            var defectionEntry = c.Chronicle.Entries
                .FirstOrDefault(e => e.Sequence > seenSeq && string.Equals(e.Kind, "mainland_defection", StringComparison.Ordinal));
            Check(defectionEntry is not null && string.Equals(defectionEntry.Subject, smallestEligible.Name, StringComparison.Ordinal),
                $"provincial defection targets smallest eligible state '{smallestEligible.Name}' first instead of largest megacity state (got '{defectionEntry?.Subject}')");
        }

        // C. Dry-land divided-homeland reclamation works without requiring naval hulls.
        {
            var dry = Campaign.CreateBootstrap(20260830);
            var dryMap = map.CloneForOverrides();
            dry.AttachProvinceTopology(dryMap);
            dry.IsPaused = false;
            var dryTwn = dry.NationByTag("TWN")!;
            var dryChn = dry.NationByTag("CHN")!;
            var dryFujian = dry.States.First(s => string.Equals(s.Id, "cn_fj", StringComparison.OrdinalIgnoreCase));
            var dryGuangdong = dry.States.First(s => string.Equals(s.Id, "cn_gd", StringComparison.OrdinalIgnoreCase));

            var dryBorderProv = 0;
            for (var p = 1; p <= dryMap.ProvinceCount && dryBorderProv == 0; p++)
            {
                if (dryMap.IsWater(p) || dryMap.ProvinceToState[p] != dryFujian.MapIndex || dryMap.ProvinceToNation[p] != dryChn.MapIndex) continue;
                foreach (var nb in dryMap.LandNeighbors[p])
                {
                    if (nb > 0 && nb <= dryMap.ProvinceCount && !dryMap.IsWater(nb) && dryMap.ProvinceToNation[nb] == dryChn.MapIndex && nb != p)
                    {
                        dryBorderProv = p;
                        break;
                    }
                }
            }
            Check(dryBorderProv > 0, "found mainland border province for dry-land reclamation test");
            dryMap.ProvinceToNation[dryBorderProv] = (ushort)dryTwn.MapIndex;
            dry.ProvinceController[dryBorderProv] = (ushort)dryTwn.MapIndex;
            dry.Fleets.RemoveAll(f => f.NationId == dryTwn.Id);

            Check(!MainlandReclamation.IsSeparatedBySea(dry, dryTwn, dryChn),
                "shared land province eliminates sea separation");
            Check(MainlandReclamation.TryGetDividedRivalPair(dry, dryTwn, dryChn, out var dryContender, out var dryHolder) &&
                  dryContender == dryTwn && dryHolder == dryChn,
                "TryGetDividedRivalPair qualifies land-adjacent divided kindred rivals");

            SetElapsedDays(dry, 600);
            dryTwn.Stability = 0.85f;
            dryTwn.WarSupport = 0.85f;
            var baseArmyId = dry.Armies.Count > 0 ? dry.Armies.Max(a => a.Id) + 1 : 1;
            for (var i = 0; i < dry.Defines.AiMainlandReturnMinimumDivisions; i++)
                dry.Armies.Add(new Army { Id = baseArmyId + i, NationId = dryTwn.Id, ProvinceId = dryBorderProv, Divisions = 1f, Strength = 1f, TemplateId = "line_infantry" });
            var readiness = MainlandReclamation.ExplainAiReadiness(dry, dryTwn, dryChn);
            Check(readiness.StartsWith("READY(", StringComparison.Ordinal) &&
                  readiness.Contains("sea=False", StringComparison.Ordinal) &&
                  readiness.Contains("hulls=0", StringComparison.Ordinal),
                $"dry-land divided contender passes AI readiness without fleet hulls ({readiness})");

            var dryWar = CampaignDiplomacy.DeclareWar(dry, dryTwn.Id, dryChn.Id, dryGuangdong.MapIndex, "reconquest")!;
            Check(MainlandReclamation.IsMainlandLiberator(dry, dryWar, dryTwn, dryChn),
                "IsMainlandLiberator qualifies land-border divided homeland war without naval requirement");
        }
    }
}
