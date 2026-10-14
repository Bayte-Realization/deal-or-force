using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class IdentityResearchTests
{
    static readonly string[] Research = {
        "transparent_procurement", "regional_service_standards", "sovereign_risk_accounting", "fiscal_continuity_protocols",
        "public_data_reproducibility", "modular_water_treatment", "repairable_product_standards", "low_waste_reconstruction",
        "interoperable_infrastructure", "distributed_repair_reserves", "customs_interoperability", "cargo_rerouting_exercises",
        "maritime_maintenance_networks", "reserve_training_cycles", "combat_recovery_protocols", "field_protection_engineering",
        "dispersed_medical_logistics", "civil_military_reconstruction"
    };
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS identity and research: " + label); }

    static (Campaign c, Nation founder, Nation foreign) Fixture()
    {
        var c = new Campaign(420) { ScenarioId = "identity_fixture" };
        var founder = new Nation { Id = 0, Tag = "QFA", Name = "Founder", MapIndex = 501, Population = 300,
            Gdp = 100, Treasury = 100, Stability = .9f, Ideology = "socialism", Government = "provisional_government" };
        var foreign = new Nation { Id = 1, Tag = "QFB", Name = "Foreign", MapIndex = 502, Population = 1000000 };
        c.Nations.Add(founder); c.Nations.Add(foreign);
        var first = new ScenarioState { MapIndex = 3001, NationMapIndex = 501, Id = "faith_a", Name = "First", Population = 100 };
        first.PopulationGroups.Add(new PopulationGroup { Culture = "arab_iraqi", Religion = "sunni", Size = 60 });
        first.PopulationGroups.Add(new PopulationGroup { Culture = "arab_iraqi", Religion = "catholic", Size = 40 });
        var second = new ScenarioState { MapIndex = 3002, NationMapIndex = 501, Id = "faith_b", Name = "Second", Population = 200 };
        second.PopulationGroups.Add(new PopulationGroup { Culture = "arab_iraqi", Religion = "shia", Size = 120 });
        second.PopulationGroups.Add(new PopulationGroup { Culture = "arab_iraqi", Religion = "irreligion", Size = 80 });
        var other = new ScenarioState { MapIndex = 3003, NationMapIndex = 502, Id = "faith_c", Name = "Foreign", Population = 1000000 };
        other.PopulationGroups.Add(new PopulationGroup { Culture = "arab_iraqi", Religion = "sunni", Size = 1000000 });
        c.States.Add(first); c.States.Add(second); c.States.Add(other);
        var states = new ushort[48]; var owners = new ushort[48];
        for (var y = 0; y < 4; y++) for (var x = 0; x < 12; x++)
        { states[y * 12 + x] = (ushort)(3001 + x / 4); owners[y * 12 + x] = (ushort)(x < 8 ? 501 : 502); }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 12, 4, _ => false));
        c.ConfigureSinglePlayer(founder.Id);
        return (c, founder, foreign);
    }

    static FormableNation FaithDefinition(Campaign c, float minimum = .5f) => new("faith_test", "Faith test", "", new[] { "QFA" },
        Array.Empty<string>(), Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToNation[p] == 501).ToArray(),
        .5f, .01f, 0, 0, Array.Empty<string>(), Array.Empty<string>(), "")
        { RequiredReligionGroups = new[] { "muslim" }, MinimumReligionShare = minimum, PeacefulAccession = true };

    public static void Run()
    {
        DemographyCatalog.ResetCatalog(); FormableNations.ResetCatalog(); TechnologyCatalog.ResetCatalog(); Loc.ResetCatalog();
        var soviet = FormableNations.Find("soviet_union");
        var republics = new[] { "RUS", "BLR", "UKR", "KAZ", "UZB", "TKM", "TJK", "KGZ", "ARM", "AZE", "GEO", "MDA", "LVA", "LTU", "EST" };
        Check(soviet.EligibleTags.OrderBy(t => t).SequenceEqual(republics.OrderBy(t => t)), "all fifteen republics may lead the Soviet foundation, including Belarus");
        Check(soviet.RequiredTags.OrderBy(t => t).SequenceEqual(republics.OrderBy(t => t)) && soviet.RequiredIdeology.SequenceEqual(new[] { "communism", "socialism" }) && soviet.MinimumStability == .76f,
            "broader access does not relax land, ideology or stability conditions");
        var successor = new Nation { Tag = "NEW" }; successor.FoundedCultures.Add("belarusian");
        Check(FormableNations.Eligible(successor, soviet), "a Belarusian-founded successor keeps a cultural union route after its tag changes");
        successor.FormedNationId = "union_state";
        Check(FormableNations.CompatibleProclamation(successor, soviet), "a Union State may progress to the larger Soviet foundation");
        successor.FormedNationHistory.Add("soviet_union");
        Check(!FormableNations.CompatibleProclamation(successor, soviet), "proclamation history still blocks repeated rewards");
        var yugo = FormableNations.Find("yugoslavia");
        successor.FormedNationHistory.Clear(); successor.FoundedCultures.Clear(); successor.FoundedCultures.Add("serbian"); successor.FormedNationId = "serbia_montenegro";
        Check(FormableNations.Eligible(successor, yugo) && FormableNations.CompatibleProclamation(successor, yugo) && yugo.AiFoundation && yugo.PeacefulAccession,
            "Serbian successors and the Serbia-Montenegro union retain a negotiated Yugoslav path");
        var turan = FormableNations.Find("turkic_union"); successor.FoundedCultures.Clear(); successor.FoundedCultures.Add("karakalpak");
        Check(FormableNations.Eligible(successor, turan), "declared culture families unlock related foundations without tag hardcoding");
        successor.FoundedCultures.Clear(); successor.FoundedCultures.Add("french");
        Check(!FormableNations.Eligible(successor, turan), "unrelated peoples do not qualify through a generic group wildcard");
        foreach (var id in new[] { "soviet_union", "yugoslavia", "china_reunified", "syria_reunited", "sudan_reunited" })
            Check(FormableNations.Find(id).EligibleCultures.All(c => DemographyCatalog.Culture(c) is not null), "declared cultural successors resolve: " + id);

        var (c, founder, foreign) = Fixture(); var definition = FaithDefinition(c);
        var before = CampaignFingerprint.Of(c);
        Check(Math.Abs(FormableNations.ReligionShare(c, founder, definition) - .6f) < .00001f, "religious mandate uses resident counts, not a mean of regional percentages");
        Check(FormableNations.ReligionMandate(c, founder, definition) && !FormableNations.ReligionMandate(c, founder, FaithDefinition(c, .7f)), "a changed population threshold changes readiness");
        Check(FormableNations.ReligionShare(c, founder, definition with { RequiredReligions = new[] { "sunni" } }) == .6f,
            "an explicit religion plus its family never double-counts residents");
        Check(Math.Abs(FormableNations.ReligionShare(c, founder, definition with { RequiredReligionGroups = Array.Empty<string>(), RequiredReligions = new[] { "shia" } }) - .4f) < .00001f,
            "specific faith requirements remain narrower than their family");
        Check(FormableNations.Evaluate(c, founder, definition).CanForm && before == CampaignFingerprint.Of(c), "complete formation assessment is read-only and preserves the simulation random stream");
        Check(!FormableNations.Evaluate(c, founder, FaithDefinition(c, .7f)).CanForm, "religion readiness is an authoritative command prerequisite");
        var errors = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse("""{"faith_test":{"eligible_tags":["QFA"],"required_provinces":[1],"required_religions":["sunni"],"required_religion_groups":["muslim"],"minimum_religion_share":0.7,"peaceful_accession":true}}""");
            FormableNations.Merge(doc.RootElement, errors);
            Check(errors.Count == 0 && FormableNations.Find("faith_test").MinimumReligionShare == .7f, "modded religious mandate loads through the production overlay parser");
            var valid = FormableNations.Find("faith_test");
            foreach (var json in new[] {
                """{"faith_test":{"required_religions":["invented_typo"]}}""",
                """{"faith_test":{"required_religion_groups":["invented_typo"]}}""",
                """{"faith_test":{"minimum_religion_share":1.1}}""",
                """{"faith_test":{"minimum_religion_share":0}}""",
                """{"faith_test":{"minimum_religion_share":"NaN"}}""" })
            { using var bad = JsonDocument.Parse(json); FormableNations.Merge(bad.RootElement, errors); }
            Check(errors.Count == 5 && ReferenceEquals(valid, FormableNations.Find("faith_test")), "invalid religion overlays diagnose typos and preserve the previous valid definition");
            using var metadata = JsonDocument.Parse("""{"faith_test":{"description":"Edited description"}}""");
            FormableNations.Merge(metadata.RootElement, errors);
            Check(FormableNations.Find("faith_test").RequiredReligions.SequenceEqual(new[] { "sunni" }) && FormableNations.Find("faith_test").MinimumReligionShare == .7f,
                "metadata-only overlays retain the religion gate and threshold");
            before = CampaignFingerprint.Of(c);
            new FormNationCommand { IssuerId = founder.Id, FormableId = "faith_test" }.Apply(c);
            Check(before == CampaignFingerprint.Of(c), "a rejected religious proclamation neither spends money nor changes identity");
            Check(PeacefulFormation.Refusal(c, founder, foreign, FormableNations.Find("faith_test")) == "The founder lacks the required religious population mandate.",
                "accession talks explain a missing religious mandate before seeking a treaty");
            Check(!PeacefulFormation.Accept(c, founder, foreign, FormableNations.Find("faith_test")), "an old invitation cannot bypass a changed religious mandate");
            var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "identity"), "identity");
            Check(saved.Success && Math.Abs(FormableNations.ReligionShare(saved.Campaign, saved.Campaign.NationByTag(founder.Tag), definition) - .6f) < .00001f,
                "mandate is derived identically after the actual network/save roundtrip, without new saved fields");
        }
        finally { FormableNations.ResetCatalog(); }

        c.StatesOf(founder)[0].PopulationGroups.Add(new PopulationGroup { Religion = "sunni", Size = -900 });
        c.StatesOf(founder)[0].PopulationGroups.Add(new PopulationGroup { Religion = "sunni", Size = 0 });
        Check(Math.Abs(FormableNations.ReligionShare(c, founder, definition) - .6f) < .00001f, "zero and invalid negative headcounts cannot manufacture a mandate");
        var unprofiled = new ScenarioState { MapIndex = 3004, NationMapIndex = founder.MapIndex, Id = "unprofiled", Population = 300 };
        c.States.Add(unprofiled); c.InvalidateStateIndex();
        Check(Math.Abs(FormableNations.ReligionShare(c, founder, definition) - .3f) < .00001f && !FormableNations.ReligionMandate(c, founder, definition),
            "unprofiled residents remain in the denominator rather than silently supporting a religious mandate");
        c.States.Remove(unprofiled); c.InvalidateStateIndex();
        foreach (var state in c.StatesOf(founder)) foreach (var group in state.PopulationGroups) group.Religion = "";
        Check(!FormableNations.ReligionMandate(c, founder, definition), "missing faith data fails closed instead of guessing from the culture");
        Check(FormableNations.ReligionMandate(c, founder, definition with { RequiredReligionGroups = Array.Empty<string>() }), "secular foundations remain unconstrained");
        Check(!FormableNations.ReligionMandate(null, null, definition), "no world or residents cannot satisfy a religious foundation");

        var technologyErrors = new List<string>(); TechnologyCatalog.ResetCatalog(technologyErrors); SpriteCatalog.Load();
        Check(technologyErrors.Count == 0 && TechnologyCatalog.Problems().Count == 0, "expanded research catalogue uses live effects and an acyclic graph");
        Check(Research.Length == 18 && Research.All(id => TechnologyCatalog.Of(id) is not null), "eighteen connected institutional, industrial, transport and military projects exist");
        foreach (var id in Research)
        {
            var t = TechnologyCatalog.Of(id);
            Check(t.Requires.Count > 0 && t.Effects.Count > 0 && t.Effects.Keys.All(NationalModifiers.Knows) && SpriteCatalog.Has(t.Icon), "prerequisites, real modifier hooks and artwork resolve: " + id);
            Check(Loc.TextIn("english", t.Name) is not null && Loc.TextIn("russian", t.Name) is not null && Loc.TextIn("russian", t.Description) is not null,
                "research title and explanation localise: " + id);
        }
        founder.Technologies.Clear(); founder.ModifierTotals = null;
        Check(!ResearchSystem.Available(founder, TechnologyCatalog.Of("transparent_procurement")), "new research remains locked until its real precursor is learned");
        founder.Technologies.Add("digital_government");
        Check(ResearchSystem.Available(founder, TechnologyCatalog.Of("transparent_procurement")), "existing government research opens a connected new branch");
        founder.Technologies.Add("sovereign_risk_accounting"); NationalModifiers.Invalidate(founder);
        var withCapacity = SovereignFinance.Capacity(c, founder);
        founder.Technologies.Remove("sovereign_risk_accounting"); NationalModifiers.Invalidate(founder);
        Check(withCapacity > SovereignFinance.Capacity(c, founder), "sovereign accounting changes actual debt capacity, not just a research tooltip");
        founder.Technologies.Add("sovereign_risk_accounting"); founder.Technologies.Add("fiscal_continuity_protocols");
        Check(Math.Abs(ResearchSystem.Bonus(founder, "credit_capacity") - .06f) < .00001f && Math.Abs(ResearchSystem.Bonus(founder, "tax_efficiency") - .025f) < .00001f,
            "connected fiscal projects combine through the existing research modifier cache");
        var layout = TechnologyTreeLayout.Build(TechnologyCatalog.All);
        Check(layout.Length == TechnologyCatalog.All.Count && layout.Select(slot => (slot.Column, slot.Row)).Distinct().Count() == layout.Length,
            "the expanded cross-branch research graph lays out without overlapping cards");
        var byId = layout.ToDictionary(slot => slot.Id);
        Check(TechnologyCatalog.All.All(t => t.Requires.All(id => byId[id].Column < byId[t.Id].Column)), "all new cross-branch dependencies point forward in the research view");
        foreach (var id in new[] { "cornish", "garifuna", "miskito", "mixtec", "zapotec" })
            Check(DemographyCatalog.Culture(id) is { Religions.Count: 0 } && Loc.TextIn("russian", DemographyCatalog.Culture(id).Name) is not null,
                "regional identity vocabulary is translated without fabricated census or religion fractions: " + id);
        Check(DemographyCatalog.Religion("yazidi").Group != "muslim" && DemographyCatalog.Religion("alevi_bektashi") is not null,
            "the new living faith traditions are distinct catalogue entries rather than converted populations");
        var baseWorld = Campaign.CreateBootstrap(421);
        var addedCultures = new HashSet<string>(new[] { "cornish", "garifuna", "miskito", "mixtec", "zapotec" }, StringComparer.OrdinalIgnoreCase);
        Check(baseWorld.States.All(s => s.PopulationGroups.All(g => !addedCultures.Contains(g.Culture))),
            "adding identity vocabulary does not silently replace residents in the authored 2024 census");
        var religionAfterBootstrap = FormableNations.Find("caliphate");
        Check(religionAfterBootstrap.RequiredReligionGroups.SequenceEqual(new[] { "muslim" }) && religionAfterBootstrap.RequiredIdeology.Contains("theocratic_rule"),
            "the shipped religious foundation combines resident faith with its existing institutional gate");
        Check(Loc.Placeholders(Loc.TextIn("english", "Religious founding mandate ({0}): {1:P0} / {2:P0}")).SetEquals(
            Loc.Placeholders(Loc.TextIn("russian", "Religious founding mandate ({0}): {1:P0} / {2:P0}"))), "new localised requirement preserves every formatting argument");
    }
}
