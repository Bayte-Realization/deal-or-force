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
        Description = "Wave 6I Mod Content Studio & Zero-Confusion Scenario Editor + Wave 6H Diseases/Disasters wiring: all 7 studio templates validate and save cleanly, invalid drafts rejected, ScenarioMapDraft audit, deterministic FNV-1a hashing, Wave 6H sim/save/fingerprint/UI wiring, and EN/RU localization parity.")]
    public static void Run()
    {
        TestAllTemplatesValidateAndRoundTrip();
        TestValidationRejectsBrokenDrafts();
        TestSaveToTempModCreatesValidFilesAndLocStubs();
        TestScenarioDraftAudit();
        TestWave6HAnd6ISourceAndLocalizationContracts();
    }

    private static void TestAllTemplatesValidateAndRoundTrip()
    {
        var templates = ModStudioDocument.AllTemplates;
        Check(templates.Count == 7, $"ModStudioDocument exposes all 7 authoring templates (got {templates.Count})");
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

        Check(ModStudioDocument.NormalizeId("  My Cool Event!! 2026 ") == "my_cool_event_2026",
            "NormalizeId sanitizes arbitrary user input into safe snake_case");
    }

    private static void TestSaveToTempModCreatesValidFilesAndLocStubs()
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

            var invalidDoc = ModStudioDocument.CreateFromTemplate(ModStudioCategory.Event);
            invalidDoc.SetField("id", "BAD ID!");
            var threw = false;
            try { invalidDoc.SaveToMod(tempRoot); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "SaveToMod refuses invalid documents without writing corrupt files");
        }
        finally
        {
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

    private static void TestWave6HAnd6ISourceAndLocalizationContracts()
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

        // Wave 6I architecture & hygiene contracts
        var docPath = Path.Combine(repoRoot, "Scripts", "Simulation", "ModStudioDocument.cs");
        var studioUiPath = Path.Combine(repoRoot, "Scripts", "ui", "ModContentStudioUI.cs");
        var mapEdPath = Path.Combine(repoRoot, "Scripts", "ui", "ScenarioMapEditor.cs");
        var wbPath = Path.Combine(repoRoot, "Scripts", "ui", "ScenarioWorkbenchUI.cs");

        Check(File.Exists(docPath + ".uid") && File.Exists(studioUiPath + ".uid"),
            "Wave 6I: new C# files have .cs.uid sidecars");

        var docSrc = File.ReadAllText(docPath);
        var studioUiSrc = File.ReadAllText(studioUiPath);
        var mapEdSrc = File.ReadAllText(mapEdPath);
        var wbSrc = File.ReadAllText(wbPath);

        Check(!docSrc.Contains("using Godot", StringComparison.Ordinal) && !docSrc.Contains(".GetHashCode(", StringComparison.Ordinal),
            "Wave 6I: ModStudioDocument.cs is pure C# with zero Godot refs and zero string GetHashCode()");
        Check(!studioUiSrc.Contains("new Color(", StringComparison.Ordinal) &&
              studioUiSrc.Contains("Ui.FocusInputIfDesktop(", StringComparison.Ordinal) &&
              studioUiSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "Wave 6I: ModContentStudioUI uses Ui.Colour tokens, Ui.FocusInputIfDesktop, and PresentationFonts.Tabular()");
        Check(!mapEdSrc.Contains("new Color(", StringComparison.Ordinal) &&
              mapEdSrc.Contains("MapModeBanner", StringComparison.Ordinal) &&
              mapEdSrc.Contains("MapHoverReadout", StringComparison.Ordinal) &&
              mapEdSrc.Contains("ModStudioDocument.AuditDraft", StringComparison.Ordinal) &&
              mapEdSrc.Contains("Ui.FocusInputIfDesktop(", StringComparison.Ordinal),
            "Wave 6I: ScenarioMapEditor provides zero-confusion mode banner, live hover readout, live draft audit, and desktop-only focus");
        Check(wbSrc.Contains("OpenModContentStudio", StringComparison.Ordinal),
            "Wave 6I: ScenarioWorkbenchUI exposes Mod Content Studio entry button");

        // EN + RU localization parity for 99_wave6_mod_studio.json
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_wave6_mod_studio.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_wave6_mod_studio.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_wave6_mod_studio.json exist");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 60 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_wave6_mod_studio.json have identical key counts");
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
