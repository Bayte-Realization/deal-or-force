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
            "dry-land neighbors do not qualify as cross-strait Mainland liberators");

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
        var divsBeforeBridgehead = c.Armies.Count(a => a.NationId == twn.Id);
        var chnStabBefore = chn.Stability;
        c.ProvinceController[beachProvince] = (ushort)twn.MapIndex;

        MainlandReclamation.Tally.Clear();
        MainlandReclamation.Tick(c);

        Check(MainlandReclamation.Tally.GetValueOrDefault("bridgehead_secured") == 1,
            "occupying a mainland province establishes a Mainland bridgehead");
        Check(ModifierSystem.Has(twn, "mainland_return_momentum"),
            "bridgehead grants mainland_return_momentum modifier to Taiwan");
        Check(ModifierSystem.Has(chn, "mainland_regime_shock"),
            "bridgehead inflicts mainland_regime_shock on China");
        Check(c.Armies.Count(a => a.NationId == twn.Id) > divsBeforeBridgehead,
            "bridgehead spawns initial volunteer formation on the liberated beach");
        Check(chn.Stability < chnStabBefore, "bridgehead shocks rival stability");

        // Idempotency: ticking again must NOT duplicate the bridgehead.
        var divsAfterBridgehead = c.Armies.Count(a => a.NationId == twn.Id);
        MainlandReclamation.Tick(c);
        Check(MainlandReclamation.Tally.GetValueOrDefault("bridgehead_secured") == 1,
            "bridgehead fires only once per war");
        Check(c.Armies.Count(a => a.NationId == twn.Id) == divsAfterBridgehead,
            "re-ticking without new liberated states does not duplicate divisions");

        // Liberate Fujian -> triggers regional volunteer rally once.
        foreach (var p in fujianProvs) c.ProvinceController[p] = (ushort)twn.MapIndex;
        MainlandReclamation.Tick(c);
        Check(MainlandReclamation.Tally.GetValueOrDefault("region_rallied") == 1,
            "liberating Fujian triggers a regional volunteer rally");
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
    }
}
