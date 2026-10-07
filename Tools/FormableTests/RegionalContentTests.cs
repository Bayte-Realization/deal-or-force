using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class RegionalContentTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS regional content: " + label); }
    public static void Run()
    {
        // The invented regional voices are a mod template now (the base world is the real one),
        // loaded here the way a mod is so the civic-movement lifecycle they exercise stays tested.
        var templateRoot = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), "..", "mods", "_templates", "regional_civic_voices"));
        using (var template = StructuredData.ReadDocument(Path.Combine(templateRoot, "common", "movements", "30_regional_voices.json")))
            MovementCatalog.Merge(template.RootElement, null);
        Check(!File.Exists(Path.Combine(GameDataLoader.DataFolder(), "common", "movements", "30_regional_voices.json")),
            "the base game ships no invented regional movement");
        var c = Campaign.CreateBootstrap(601);
        // Production startup founds movements after attaching geography, not at bootstrap.
        Movements.Found(c);
        var voices = MovementCatalog.All.Where(d => d.Id.EndsWith("_civic_voice")).ToArray();
        Check(voices.Length == 16, "sixteen authored civic organisations");
        Check(voices.Count(d => d.StartActive) == 8 && voices.Count(d => !d.StartActive) == 8, "eight starting and eight conditional movements");
        foreach (var d in voices)
        {
            Check(Movements.StateById(c, d.Headquarters) != null && d.Homeland.All(id => Movements.StateById(c, id) != null), "geography resolves " + d.Tag);
            Check(!d.CanStageUprising && d.Has("nonviolence"), "civic voice is not an automatic insurgency " + d.Tag);
            Check(d.StartActive == c.Nations.Any(n => n.Movement?.Id == d.Id), "start condition respected " + d.Tag);
        }
        var dormant = voices.First(d => d.Tag == "UDV");
        var region = Movements.StateById(c, dormant.Headquarters); region.Radicalism = 1;
        dormant.EmergenceThreshold = .01f; dormant.EmergenceChance = 1;
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        c.SetFeature("movement_lifecycle", false); Day(30); MovementLifecycle.Tick(c);
        Check(c.NationByTag("UDV") == null, "disabled lifecycle does not spawn hidden organisations");
        c.SetFeature("movement_lifecycle", true);
        for (var day = 60; day <= 360 && c.NationByTag("UDV") == null; day += 30) { Day(day); MovementLifecycle.Tick(c); }
        var born = c.NationByTag("UDV");
        Check(born is not null && born.Movement is { Exile: false, Dissolved: false }, "a civic organisation emerges without a fallen government");
        var idBefore = born.Id; MovementFall.Dissolve(c, born, "fixture");
        var countBefore = c.Nations.Count; Day(c.ElapsedDays + 30); MovementLifecycle.Tick(c);
        Check(born.Movement.Dissolved && c.Nations.Count == countBefore, "rebirth cooldown prevents immediate identity duplication");
        Day(c.ElapsedDays + 365); Movements.Activate(c, dormant);
        Check(c.NationByTag("UDV").Id == idBefore && !born.Movement.Dissolved && c.Nations.Count == countBefore,
            "reactivation reuses saved identity rather than allocating duplicate countries");
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog();
        Check(SpriteCatalog.Has("flag_formable_roman_restoration") && SpriteCatalog.Has("icon_laws") &&
              File.Exists(Path.Combine(templateRoot, "gfx", "flags", "flag_udv.png")),
            "new flags and governance icon resolve through the sprite registry; the voices' flags travel with their template");
        foreach (var d in FormableNations.All)
            foreach (var tag in d.RequiredTags)
                Check(ProvinceOwnership.ProvincesOf(tag).Length > 0, "formable territory resolves " + d.Id + ":" + tag);
        var italy = c.NationByTag("ITA"); var romania = c.NationByTag("ROU");
        Check(FormableNations.Eligible(italy, FormableNations.Find("roman_restoration")) &&
            FormableNations.Eligible(romania, FormableNations.Find("roman_restoration")), "both Italy and Romania have a Roman path");
        italy.FormedNationHistory.Add("roman_restoration");
        Check(FormableNations.Evaluate(c, italy, FormableNations.Find("roman_restoration")).Requirements.Any(r =>
            r.Label == Loc.Get("This proclamation has not already been made") && !r.Met), "past proclamation prevents repeat bonus farming");
        MovementCatalog.ResetCatalog();
        var sampleRoot = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), "..", "mods", "_templates", "yaml-data", "common"));
        var sampleErrors = new List<string>();
        var originalCourse = NationalCourseCatalog.Find("institutional_continuity");
        try
        {
            using (var doc = StructuredData.ReadDocument(Path.Combine(sampleRoot, "laws", "60_governance.yaml")))
                PoliticalPolicyCatalog.MergeLaws(doc.RootElement, sampleErrors);
            using (var doc = StructuredData.ReadDocument(Path.Combine(sampleRoot, "agendas", "60_governance.yaml")))
                PoliticalPolicyCatalog.MergeAgendas(doc.RootElement, sampleErrors);
            using (var doc = StructuredData.ReadDocument(Path.Combine(sampleRoot, "national_courses", "60_governance.yaml")))
                NationalCourseCatalog.Merge(doc.RootElement, sampleErrors);
            Check(sampleErrors.Count == 0, "authored governance YAML examples parse through production catalogs");
            Check(PoliticalPolicyCatalog.Find("fiscal_discipline").CooldownDays == 120 &&
                PoliticalPolicyCatalog.Find("fiscal_discipline").Modifiers.ContainsKey("tax_efficiency"),
                "YAML law overlay inherits omitted effects");
            Check(PoliticalPolicyCatalog.Find("example_community_services") is { Scope: "region" } &&
                PoliticalPolicyCatalog.Find("example_stability_pledge", true) is { DurationDays: 365 },
                "YAML adds a targeted law and sustained government promise");
            Check(NationalCourseCatalog.Find("institutional_continuity").Pressures == originalCourse.Pressures &&
                NationalCourseCatalog.Find("institutional_continuity").Modifiers == originalCourse.Modifiers,
                "YAML course description overlay preserves organic pressures and effects");
        }
        finally { PoliticalPolicyCatalog.ResetCatalog(); NationalCourseCatalog.ResetCatalog(); }
    }
}
