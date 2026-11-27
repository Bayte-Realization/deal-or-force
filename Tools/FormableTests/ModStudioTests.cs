using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class ModStudioTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL mod-studio: " + label);
        Console.WriteLine("PASS mod-studio: " + label);
    }

    [TestSuite("--mod-studio-only", Order = 2720, IncludeInDefaultRun = true,
        Description = "Wave 6I+ Mod Content Studio Pro & Scenario Editor: all 17 guided templates across 5 sections, prestige/income_share/gdp_share schemas, ContentWorkbench domain validation, CountryEditor prestige/stability/war_support round-trip, HotReloadCatalogs, source hygiene, and EN/RU parity.")]
    public static void Run()
    {
        TestAllTemplatesValidateAndRoundTrip();
        TestValidationRejectsBrokenDrafts();
        TestSaveLoadPresetReloadAndCountryEditor();
        TestScenarioDraftAudit();
        TestSourceAndLocalizationContracts();
    }

    private static void TestAllTemplatesValidateAndRoundTrip()
    {
        var templates = ModStudioDocument.AllTemplates;
        Check(templates.Count == 17, $"ModStudioDocument exposes all 17 authoring templates (got {templates.Count})");
        foreach (ModStudioSection sec in Enum.GetValues(typeof(ModStudioSection)))
            Check(templates.Any(t => t.Section == sec), $"section {sec} has at least one studio category");

        foreach (var tpl in templates)
        {
            var doc = ModStudioDocument.CreateFromTemplate(tpl.Category, $"test_{tpl.Category.ToString().ToLowerInvariant()}", "Test Entry");
            var report = doc.Validate();
            Check(report.Valid, $"default template {tpl.Category} validates cleanly ({string.Join("; ", report.Issues.Select(i => i.Message))})");
            Check(ContentWorkbench.IsEditable(report.SuggestedPath), $"{tpl.Category} suggested path '{report.SuggestedPath}' is editable by ContentWorkbench");
            var json = doc.RenderJson();
            using var parsed = JsonDocument.Parse(json);
            Check(parsed.RootElement.ValueKind == JsonValueKind.Object, $"{tpl.Category} renders valid JSON object");

            doc.RenderLocalizationStubs(out var enJson, out var ruJson);
            using var enDoc = JsonDocument.Parse(enJson);
            using var ruDoc = JsonDocument.Parse(ruJson);
            var enKeys = enDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var ruKeys = ruDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Check(enKeys.Length > 0 && enKeys.SequenceEqual(ruKeys), $"{tpl.Category} generates balanced EN/RU localization stubs ({enKeys.Length} keys)");

            var h1 = ModStudioDocument.ComputeDocumentHash(json);
            var h2 = ModStudioDocument.ComputeDocumentHash(json);
            Check(h1 == h2 && h1 != 0UL, $"{tpl.Category} deterministic FNV-1a document hash is stable");
        }

        // Verify upgraded Event / Decision / Defines schemas match engine semantics.
        var evDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Event);
        var evJson = evDoc.RenderJson();
        Check(evJson.Contains("\"income_share\"", StringComparison.Ordinal) &&
              evJson.Contains("\"gdp_share\"", StringComparison.Ordinal) &&
              evJson.Contains("\"prestige\"", StringComparison.Ordinal) &&
              !evJson.Contains("\"treasury_percent\"", StringComparison.Ordinal),
            "Event template exposes income_share, gdp_share, and prestige rather than treasury_percent");

        var decDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Decision);
        var decJson = decDoc.RenderJson();
        Check(decJson.Contains("\"gdp_cost_share\"", StringComparison.Ordinal) &&
              decJson.Contains("\"income_share\"", StringComparison.Ordinal) &&
              decJson.Contains("\"prestige\"", StringComparison.Ordinal),
            "Decision template exposes optional income_share, gdp_cost_share, and prestige");

        var defDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.DefinesOverride);
        var defJson = defDoc.RenderJson();
        Check(defJson.Contains("\"MaxMonthlyGrowth\"", StringComparison.Ordinal) &&
              defJson.Contains("\"DefaultTaxRate\"", StringComparison.Ordinal) &&
              defJson.Contains("\"NotorietyPerUnjustifiedWar\"", StringComparison.Ordinal) &&
              defJson.Contains("\"PeaceWearinessAfterDays\"", StringComparison.Ordinal) &&
              defJson.Contains("\"AiAggression\"", StringComparison.Ordinal) &&
              defJson.Contains("\"MainlandReclamationEnabled\"", StringComparison.Ordinal) &&
              defJson.Contains("\"PrestigeEnabled\"", StringComparison.Ordinal) &&
              defJson.Contains("\"PrestigeWeeklyDriftRate\"", StringComparison.Ordinal),
            "DefinesOverride template exposes growth, tax, notoriety, weariness, aggression, reclamation, and prestige drift");

        var formDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.FormableNation, "test_nordic_union", "Nordic Union");
        Check(formDoc.Validate().Valid && formDoc.RenderJson().Contains("\"required_regions\"", StringComparison.Ordinal),
            "FormableNation template validates cleanly and emits required_regions");
        var ctryDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Country, "test_albion", "Albion");
        Check(ctryDoc.Validate().Valid && ctryDoc.RenderJson().Contains("\"starting_treasury\"", StringComparison.Ordinal),
            "Country template validates cleanly and emits starting_treasury");
        var mapDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.MapPack, "test_archipelago", "Archipelago Map");
        Check(mapDoc.Validate().Valid && mapDoc.RenderJson().Contains("\"siberia_multiplier\"", StringComparison.Ordinal) &&
              ContentWorkbench.IsEditable(mapDoc.Validate().SuggestedPath),
            "MapPack template validates cleanly, emits siberia_multiplier, and is editable via ContentWorkbench maps/");
    }

    private static void TestValidationRejectsBrokenDrafts()
    {
        var badId = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Event);
        badId.SetField("id", "../escape");
        var r1 = badId.Validate();
        Check(!r1.Valid && r1.Issues.Any(i => i.IsError && i.FieldKey == "id"), "rejects path-traversal / unsafe identifier");

        var badNum = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Decision);
        badNum.SetField("pp_cost", "9999");
        var r2 = badNum.Validate();
        Check(!r2.Valid && r2.Issues.Any(i => i.IsError && i.FieldKey == "pp_cost"), "rejects out-of-range numeric parameter");

        var badLat = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Disease);
        badLat.SetField("min_abs_lat", "75");
        badLat.SetField("max_abs_lat", "20");
        var r3 = badLat.Validate();
        Check(!r3.Valid && r3.Issues.Any(i => i.IsError && i.FieldKey == "min_abs_lat"), "rejects min_abs_lat > max_abs_lat on disease/disaster templates");

        var badChoice = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Modifier);
        badChoice.SetField("modifier_key", "nonexistent_stat_key");
        var r4 = badChoice.Validate();
        Check(!r4.Valid && r4.Issues.Any(i => i.IsError && i.FieldKey == "modifier_key"), "rejects unknown NationalModifiers stat key");

        var badColour = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Ideology);
        badColour.SetField("colour", "not-a-hex");
        Check(!badColour.Validate().Valid, "rejects malformed ideology hex colour");

        var badRegionLaw = ModStudioDocument.CreateFromTemplate(ModStudioCategory.PolicyLaw);
        badRegionLaw.SetField("scope", "region");
        badRegionLaw.SetField("modifier_key", "output");
        Check(!badRegionLaw.Validate().Valid, "rejects unsupported national modifier key on regional policy law");

        var badGood = ModStudioDocument.CreateFromTemplate(ModStudioCategory.GrandProject);
        badGood.SetField("key_good", "Unobtanium");
        Check(!badGood.Validate().Valid, "rejects unknown commodity in GrandProject key_good");

        var badUnit = ModStudioDocument.CreateFromTemplate(ModStudioCategory.UnitType);
        badUnit.SetField("attack", "0");
        Check(!badUnit.Validate().Valid, "rejects non-positive unit attack");

        Check(ModStudioDocument.NormalizeId("  My Cool Event!! 2026 ") == "my_cool_event_2026",
            "NormalizeId sanitizes arbitrary user input into safe snake_case");
    }

    private static void TestSaveLoadPresetReloadAndCountryEditor()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dof-mod-studio-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var doc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Disaster, "volcanic_ash_cloud", "Volcanic Ash Cloud");
            doc.SetField("title_ru", "Вулканическое облако пепла");
            var writtenPath = doc.SaveToMod(tempRoot, writeLocalizationStubs: true);
            Check(File.Exists(writtenPath), "SaveToMod writes content JSON file inside temp mod folder");
            Check(File.Exists(Path.Combine(tempRoot, "manifest.json")), "SaveToMod ensures manifest.json manifest");
            var enStub = Path.Combine(tempRoot, "localization", "english", "99_studio_volcanic_ash_cloud.json");
            var ruStub = Path.Combine(tempRoot, "localization", "russian", "99_studio_volcanic_ash_cloud.json");
            Check(File.Exists(enStub) && File.Exists(ruStub), "SaveToMod writes matching EN and RU localization stubs");

            using var enDoc = JsonDocument.Parse(File.ReadAllText(enStub));
            using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruStub));
            Check(enDoc.RootElement.GetProperty("studio.haz.volcanic_ash_cloud.name").GetString() == "Volcanic Ash Cloud",
                "EN localization stub contains authored English title");
            Check(ruDoc.RootElement.GetProperty("studio.haz.volcanic_ash_cloud.name").GetString() == "Вулканическое облако пепла",
                "RU localization stub contains authored Russian title");

            var savedDisasters = ModStudioDocument.ListSavedEntries(tempRoot, ModStudioCategory.Disaster);
            Check(savedDisasters.Contains("99_studio_volcanic_ash_cloud.json", StringComparer.OrdinalIgnoreCase),
                "ListSavedEntries returns saved studio files by category");
            Check(ModStudioDocument.TryLoadFromMod(tempRoot, ModStudioCategory.Disaster, "99_studio_volcanic_ash_cloud.json", out var reloaded, out var loadErr),
                $"TryLoadFromMod reloads saved disaster entry cleanly ({loadErr})");
            Check(reloaded.GetField("id") == "volcanic_ash_cloud" &&
                  reloaded.GetField("title_ru") == "Вулканическое облако пепла",
                "TryLoadFromMod restores id and RU localization string");

            // Test Preset + Duplicate + HotReloadCatalogs on Technology
            var techDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Technology, "studio_test_fusion", "Studio Test Fusion");
            techDoc.SaveToMod(tempRoot, writeLocalizationStubs: true);
            var techCopy = techDoc.DuplicateAs("studio_test_fusion_v2");
            Check(techCopy.GetField("id") == "studio_test_fusion_v2", "DuplicateAs normalizes new entry identifier");

            var defDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.DefinesOverride);
            var presets = ModStudioDocument.GetPresets(ModStudioCategory.DefinesOverride);
            Check(presets.Count >= 3 && defDoc.ApplyPreset("def_fast") && defDoc.GetField("ai_aggression") == "1.6",
                "DefinesOverride exposes and applies presets cleanly");
            defDoc.SaveToMod(tempRoot, writeLocalizationStubs: true);

            // Raw override round-trip + validation check
            var customRaw = "{\"custom_studio_tech\":{\"name\":\"Raw Tech\",\"category\":\"industry\",\"cost\":120,\"effects\":{\"output\":0.02}}}";
            techDoc.LoadRawOverride(customRaw);
            Check(techDoc.HasRawOverride && techDoc.Validate().Valid && techDoc.RenderJson().Contains("custom_studio_tech", StringComparison.Ordinal),
                "LoadRawOverride preserves valid raw JSON through ContentWorkbench.Validate");
            techDoc.ClearRawOverride();
            Check(!techDoc.HasRawOverride, "ClearRawOverride restores guided form rendering");

            var campaign = new Campaign(29, new Defines());
            var reloadedCount = ModStudioDocument.HotReloadCatalogs(tempRoot, Array.Empty<string>(), campaign, out var reloadErrors);
            Check(reloadedCount >= 3 && reloadErrors.Count == 0 && TechnologyCatalog.Of("studio_test_fusion") is not null &&
                  MathF.Abs(campaign.Defines.AiAggression - 1.6f) < 0.001f,
                $"HotReloadCatalogs merges workspace mod entries cleanly ({reloadedCount} files, {reloadErrors.Count} errors)");

            // CountryEditorDocument Prestige / Stability / WarSupport validation & round-trip
            var ceRoot = Path.Combine(tempRoot, "country_mod");
            var country = new CountryEditorDocument
            {
                Tag = "TST",
                Colour = "#336699",
                Names = { ["english"] = ("Test Republic", "Testian"), ["russian"] = ("Тестовая Республика", "тестовый") },
                Prestige = 425.5f,
                Stability = 0.72f,
                WarSupport = 0.45f
            };
            Check(country.Problems().Count == 0, "CountryEditorDocument accepts valid Prestige, Stability, and WarSupport");
            country.Write(ceRoot);
            var back = CountryEditorDocument.Read(ceRoot, "TST");
            Check(back is not null &&
                  MathF.Abs((back.Prestige ?? -1f) - 425.5f) < 0.01f &&
                  MathF.Abs((back.Stability ?? -1f) - 0.72f) < 0.001f &&
                  MathF.Abs((back.WarSupport ?? -1f) - 0.45f) < 0.001f,
                "CountryEditorDocument persists and reads back Prestige, Stability, and WarSupport");

            country.Prestige = 1500f;
            Check(country.Problems().Any(p => p.Contains("Prestige", StringComparison.OrdinalIgnoreCase)),
                "CountryEditorDocument rejects Prestige outside 0..1000");

            var invalidDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Event);
            invalidDoc.SetField("id", "BAD ID!");
            var threw = false;
            try { invalidDoc.SaveToMod(tempRoot); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "SaveToMod refuses invalid documents without writing corrupt files");
        }
        finally
        {
            ModStudioDocument.HotReloadCatalogs("", Array.Empty<string>(), null, out _);
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void TestScenarioDraftAudit()
    {
        var campaign = Campaign.CreateBootstrap(20261122);
        var kor = campaign.NationByTag("KOR") ?? campaign.Nations[0];
        var kr = campaign.States.First(s => s.NationMapIndex == kor.MapIndex);
        var topo = ProvinceTopology.Build(
            Enumerable.Repeat((ushort)kr.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)kor.MapIndex, 512).ToArray(),
            32, 16, _ => false);
        campaign.AttachProvinceTopology(topo);
        campaign.Armies.Clear();
        var scenarioJson = SaveSystem.ExportScenarioJson(campaign, "audit_test");
        var draft = new ScenarioMapDraft(scenarioJson, topo.ProvinceCount, "audit_test");

        var audit = ModStudioDocument.AuditDraft(draft, topo);
        Check(audit.Valid, "AuditDraft validates a well-formed scenario draft");
        Check(audit.TotalLandProvinces > 0 && audit.AssignedLandProvinces > 0,
            $"AuditDraft counts land provinces ({audit.AssignedLandProvinces}/{audit.TotalLandProvinces})");
        Check(audit.CountryCount == draft.Countries.Length && audit.RegionCount == draft.Regions.Length,
            $"AuditDraft reports accurate countries ({audit.CountryCount}) and regions ({audit.RegionCount})");

        var nullAudit = ModStudioDocument.AuditDraft(null, topo);
        Check(!nullAudit.Valid && nullAudit.Issues.Count > 0, "AuditDraft flags null draft cleanly");
    }

    private static void TestSourceAndLocalizationContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // Wave 6H wiring contracts
        var tickSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Simulation", "Campaign.TickStages.cs"));
        var saveSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Simulation", "SaveSystem.cs"));
        var fpSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Simulation", "CampaignFingerprint.World.cs"));
        var oppSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "OpportunityAtlasUI.cs"));

        Check(tickSrc.Contains("DiseasesAndDisasters.TickStages(this)", StringComparison.Ordinal),
            "Wave 6H: Campaign.TickStages yields DiseasesAndDisasters.TickStages(this)");
        Check(saveSrc.Contains("DiseasesAndDisasters.NormalizeAfterLoad(campaign)", StringComparison.Ordinal),
            "Wave 6H: SaveSystem calls DiseasesAndDisasters.NormalizeAfterLoad(campaign)");
        Check(fpSrc.Contains("DiseasesAndDisasters.WriteFingerprint(campaign, ref hash)", StringComparison.Ordinal),
            "Wave 6H: CampaignFingerprint.World calls DiseasesAndDisasters.WriteFingerprint");
        Check(oppSrc.Contains("DiseasesAndDisasters.ActiveHealthCrises", StringComparison.Ordinal) &&
              oppSrc.Contains("DiseasesAndDisasters.ActiveDisasters", StringComparison.Ordinal) &&
              oppSrc.Contains("new HealthResponseCommand", StringComparison.Ordinal) &&
              oppSrc.Contains("new DisasterReliefCommand", StringComparison.Ordinal) &&
              !oppSrc.Contains("new Color(", StringComparison.Ordinal),
            "Wave 6H: OpportunityAtlasUI wires ActiveHealthCrises, ActiveDisasters, HealthResponseCommand, DisasterReliefCommand with zero raw Color literals");

        // Wave 6I+ Studio Pro architecture & hygiene contracts
        var docPath = Path.Combine(repoRoot, "Scripts", "Simulation", "ModStudioDocument.cs");
        var studioUiPath = Path.Combine(repoRoot, "Scripts", "ui", "ModContentStudioUI.cs");
        var mapEdPath = Path.Combine(repoRoot, "Scripts", "ui", "ScenarioMapEditor.cs");
        var wbPath = Path.Combine(repoRoot, "Scripts", "ui", "ScenarioWorkbenchUI.cs");

        Check(File.Exists(docPath + ".uid") && File.Exists(studioUiPath + ".uid"),
            "ModStudio Pro: C# files have .cs.uid sidecars");

        var docSrc = File.ReadAllText(docPath);
        var studioUiSrc = File.ReadAllText(studioUiPath);
        var mapEdSrc = File.ReadAllText(mapEdPath);
        var wbSrc = File.ReadAllText(wbPath);

        Check(!docSrc.Contains("using Godot", StringComparison.Ordinal) && !docSrc.Contains(".GetHashCode(", StringComparison.Ordinal),
            "ModStudio Pro: ModStudioDocument.cs is pure C# with zero Godot refs and zero .GetHashCode()");
        Check(!studioUiSrc.Contains("new Color(", StringComparison.Ordinal) &&
              studioUiSrc.Contains("Ui.FocusInputIfDesktop(", StringComparison.Ordinal) &&
              studioUiSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              studioUiSrc.Contains("ModStudioCategoryFilter", StringComparison.Ordinal) &&
              studioUiSrc.Contains("ModStudioToolStrip", StringComparison.Ordinal) &&
              studioUiSrc.Contains("ModStudioSavedSelect", StringComparison.Ordinal) &&
              studioUiSrc.Contains("ModStudioSaveAndReload", StringComparison.Ordinal) &&
              studioUiSrc.Contains("ScriptedEffects.Vocabulary", StringComparison.Ordinal),
            "ModStudio Pro: ModContentStudioUI has Tool Switcher, Category Filter, Saved/Reload, Scripted Reference, Ui.Colour tokens, Ui.FocusInputIfDesktop, PresentationFonts.Tabular");
        Check(!mapEdSrc.Contains("new Color(", StringComparison.Ordinal) &&
              mapEdSrc.Contains("MapModeBanner", StringComparison.Ordinal) &&
              mapEdSrc.Contains("MapHoverReadout", StringComparison.Ordinal) &&
              mapEdSrc.Contains("ModStudioDocument.AuditDraft", StringComparison.Ordinal) &&
              mapEdSrc.Contains("Ui.FocusInputIfDesktop(", StringComparison.Ordinal) &&
              mapEdSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              mapEdSrc.Contains("MapClose", StringComparison.Ordinal) &&
              mapEdSrc.Contains("PlatformProfile.Current.IsTouchPrimary", StringComparison.Ordinal),
            "ModStudio Pro: ScenarioMapEditor provides zero-confusion banner, live hover, draft audit, desktop-only focus, tabular font, and touch-scaled MapClose");
        Check(wbSrc.Contains("OpenModContentStudio", StringComparison.Ordinal) &&
              wbSrc.Contains("WorkshopCloseButton", StringComparison.Ordinal) &&
              wbSrc.Contains("IsTouchPrimary", StringComparison.Ordinal),
            "ModStudio Pro: ScenarioWorkbenchUI exposes Studio entry and touch-scaled close/navigation controls");

        // EN + RU localization parity for 99_wave6_mod_studio.json
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_wave6_mod_studio.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_wave6_mod_studio.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_wave6_mod_studio.json exist");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 180 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_wave6_mod_studio.json have identical key counts across all 14 categories");
        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU key '{k}' exists and non-empty");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }
    }
}
