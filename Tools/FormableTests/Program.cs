using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static void Main(string[] args)
    {
        if (args.Contains("--worker-fixture")) { WorkerBackendTests.Fixture(args.Last()); return; }
        if (args.Contains("--integrity-only")) { CampaignIntegrityTests.Run(); return; }
        if (args.Contains("--deals-only")) { DealTests.Run(); return; }
        if (args.Contains("--claims-only")) { TerritorialClaimsTests.Run(); return; }
        if (args.Contains("--organizations-only")) { OrganizationTests.Run(); return; }
        if (args.Contains("--households-only")) { HouseholdTests.Run(); return; }
        if (args.Contains("--release-only")) { DismantlementTests.Run(); return; }
        if (args.Contains("--deals-bench")) { DealTests.Benchmark(); return; }
        if (args.Contains("--social-only")) { SocialMemoryTests.Run(); return; }
        if (args.Contains("--crises-only")) { DomesticCrisesTests.Run(); return; }
        if (args.Contains("--ui-icons-only")) { InterfaceIconTests.Run(); return; }
        if (args.Contains("--worker-only")) { WorkerBackendTests.Run(); return; }
        if (args.Contains("--civic-only")) { CivicCampaignTests.Run(); return; }
        if (args.Contains("--alliances-only")) { DynamicAllianceTests.Run(); return; }
        if (args.Contains("--finance-only")) { SovereignFinanceTests.Run(); return; }
        if (args.Contains("--policies-only")) { PoliticalPolicyTests.Run(); return; }
        if (args.Contains("--regional-content-only")) { RegionalContentTests.Run(); return; }
        if (args.Contains("--courses-only")) { NationalCourseTests.Run(); return; }
        if (args.Contains("--flags-only")) { FlagModdingTests.Run(); return; }
        DynamicAllianceTests.Run();
        CampaignIntegrityTests.Run();
        TerritorialClaimsTests.Run();
        OrganizationTests.Run();
        HouseholdTests.Run();
        DismantlementTests.Run();
        DomesticCrisesTests.Run();
        SovereignFinanceTests.Run();
        PoliticalPolicyTests.Run();
        NationalCourseTests.Run();
        RegionalContentTests.Run();
        SocialMemoryTests.Run();
        CivicCampaignTests.Run();
        WorkerBackendTests.Run();
        if (args.Contains("--yaml-only")) { StructuredDataTests.Run(); return; }
        if (args.Contains("--army-localization-only")) { ArmyCommandLocalizationTests.Run(); return; }
        if (args.Contains("--fleet-localization-only")) { FleetNameLocalizationTests.Run(); return; }
        if (args.Contains("--icons-only")) { InterfaceIconTests.Run(); return; }
        if (args.Contains("--production-only")) { ProductionUnitsTests.Run(); return; }
        ArmyCommandLocalizationTests.Run();
        FleetNameLocalizationTests.Run();
        if (args.Contains("--events-only")) { EventCadenceTests.Run(); return; }
        if (args.Contains("--reload-only")) { PresentationReloadTests.Run(); return; }
        if (args.Contains("--ultimatum-only")) { UltimatumTests.Run(); return; }
        if (args.Contains("--feed-only")) { NewsFeedTests.Run(); return; }
        if (args.Contains("--presentation-diplomacy-only"))
        {
            PresentationReloadTests.Run();
            UltimatumTests.Run();
            NewsFeedTests.Run();
            return;
        }
        FlagModdingTests.Run();
        StructuredDataTests.Run();
        EventCadenceTests.Run();
        PresentationReloadTests.Run();
        UltimatumTests.Run();
        DealTests.Run();
        NewsFeedTests.Run();
        TerrainCatalog.ResetCatalog();
        var terrainErrors = new List<string>();
        TerrainCatalog.ApplyDirectory(GameDataLoader.DataFolder().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, terrainErrors);
        Check(terrainErrors.Count == 0 && TerrainCatalog.All.Count > 1 && File.Exists(TerrainCatalog.TerrainPath),
            "terrain assets resolve with a trailing directory separator");
        if (OperatingSystem.IsWindows())
        {
            var extendedRoot = Path.GetFullPath(GameDataLoader.DataFolder());
            if (!extendedRoot.StartsWith(@"\\?\")) extendedRoot = @"\\?\" + extendedRoot;
            terrainErrors.Clear();
            TerrainCatalog.ApplyDirectory(extendedRoot, terrainErrors);
            Check(terrainErrors.Count == 0 && TerrainCatalog.TerrainPath.StartsWith(@"\\?\") &&
                File.Exists(TerrainCatalog.TerrainPath), "terrain resolves Godot extended Windows paths");
            TerrainCatalog.ResetCatalog();
        }
        InterfaceSkin.ResetCatalog();
        LayoutWorkbenchTests.Run();
        OpportunityAtlasTests.Run();
        StrategicReportsTests.Run();
        SupplyStressTests.Run();
        TechnologyTreeTests.Run();
        ProductionUnitsTests.Run();
        InterfaceIconTests.Run();
        ScenarioMapDraftTests.Run();
        using var navigationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
            "gui", "20_campaign_structure.json")), GameDataLoader.ForgivingJson);
        var declaredNavigation = navigationDocument.RootElement.GetProperty("campaign").GetProperty("navigation")
            .EnumerateArray().Select(row => row.GetProperty("screen").GetString()).ToArray();
        Check(declaredNavigation.Length > 0 && CampaignUiCatalog.NavigationItems.Select(row => row.Screen).SequenceEqual(declaredNavigation),
            "navigation reads every declared destination from game/gui in order");
        Check(CampaignUiCatalog.For("Decisions").Tabs.Any(t => t.Id == "Formables"), "formables tab registered");
        Check(CampaignUiCatalog.For("Decisions").Links.Length == 0, "no spurious decision links");
        var errors = new List<string>();
        InterfaceSkin.Merge("{\"campaign\":{\"screens\":{\"Nation\":{\"links\":[]}}}}", "test", errors);
        Check(CampaignUiCatalog.For("Nation").Links.Length == 0 && CampaignUiCatalog.For("Nation").Tabs.Length == 3, "overlay replaces array and inherits omitted tabs");
        InterfaceSkin.Merge("{\"campaign\":{\"navigation\":[{\"screen\":\"DeleteEverything\"}]}}", "test", errors);
        Check(errors.Count > 0 && CampaignUiCatalog.NavigationItems.Count == 0, "unknown actions do not create dead buttons");
        InterfaceSkin.ResetCatalog();
        Check(!CampaignUiCatalog.LinkVisible(new("Govern", "government", "own", "Government", ""), "Government", true), "foreign government context excluded");
        FormableNations.ResetCatalog();
        Check(new[] { "united_korea", "central_asian_union", "benelux_union", "nordic_union", "iberian_union", "baltic_union" }
            .All(id => FormableNations.Find(id) is not null), "six base unions loaded alongside additional content");
        foreach (var d in FormableNations.All)
            foreach (var tag in d.RequiredTags) Check(ProvinceOwnership.ProvincesOf(tag).Length > 0, "territory resolves " + tag);
        var c = Campaign.CreateBootstrap(42);
        var n = c.NationByTag("KOR"); c.ConfigureSinglePlayer(n.Id);
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog();
        var originalGovernment = n.Government; var originalIdeology = n.Ideology;
        var fingerprint = CampaignFingerprint.Of(c);
        foreach (var ideology in IdeologyCatalog.All)
        {
            Check(SpriteCatalog.Has("icon_ideology_" + ideology.Id), "icon available " + ideology.Id);
            var preview = FlagCatalog.ResolvePreview(c, n, ideology: ideology.Id);
            n.Ideology = ideology.Id;
            Check(preview == FlagCatalog.Resolve(c, n), "flag preview equals adopted ideology " + ideology.Id);
            n.Ideology = originalIdeology;
        }
        foreach (var government in GovernmentCatalog.All)
        {
            var preview = FlagCatalog.ResolvePreview(c, n, government.Id);
            n.Government = government.Id;
            Check(preview == FlagCatalog.Resolve(c, n), "flag preview equals adopted government " + government.Id);
            n.Government = originalGovernment;
        }
        Check(fingerprint == CampaignFingerprint.Of(c), "flag previews leave campaign unchanged");
        var korea = FormableNations.Find("united_korea");
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "map not ready blocks formation");
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "divided Korea blocked");
        c.Wars.Clear();
        var provinces = korea.RequiredTags.SelectMany(ProvinceOwnership.ProvincesOf).ToArray();
        foreach (var p in provinces) c.ProvinceController[p] = (ushort)n.MapIndex;
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "occupation alone cannot form");
        foreach (var p in provinces) map.ProvinceToNation[p] = (ushort)n.MapIndex;
        n.Stability = .8f; n.Treasury = 10000;
        Check(FormableNations.Evaluate(c, n, korea).CanForm, "ownership control stability peace and treasury allow formation");
        n.Stability = .2f;
        new FormNationCommand { IssuerId = n.Id, FormableId = korea.Id }.Apply(c);
        Check(n.FormedNationId == "", "command rechecks stale UI conditions");
        n.Stability = .8f; n.Treasury = 0;
        Check(!FormableNations.Evaluate(c, n, korea).CanForm, "insufficient treasury blocks");
        n.Treasury = 10000;
        var treasury = n.Treasury; var cost = FormableNations.Evaluate(c, n, korea).Cost;
        var ownerBefore = (ushort[])map.ProvinceToNation.Clone(); var id = n.Id; var tagBefore = n.Tag;
        var command = new FormNationCommand { IssuerId = n.Id, FormableId = korea.Id };
        var formationPreview = FlagCatalog.ResolveFormationPreview(c, n, korea.Id);
        Check(NetworkCommandCodec.TryEncode(command, out var kind, out var json) && NetworkCommandCodec.TryDecode(kind, json, out _), "network command round trip");
        command.Apply(c);
        Check(formationPreview == FlagCatalog.Resolve(c, n), "formation preview equals resulting flag");
        Check(n.FormedNationId == korea.Id && n.Name == korea.Name && n.Id == id && n.Tag == tagBefore, "identity changes without breaking stable identifiers");
        Check(Math.Abs(n.Treasury - (treasury - cost)) < .001f && ownerBefore.SequenceEqual(map.ProvinceToNation), "cost deducted and borders unchanged");
        var after = n.Treasury; command.Apply(c);
        Check(n.Treasury == after, "duplicate proclamation cannot charge or reward twice");
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "tests");
        var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "tests");
        Check(restored.Success && restored.Campaign.NationById(id).FormedNationId == korea.Id && restored.Campaign.NationById(id).Name == korea.Name, "campaign snapshot preserves identity");
        // Native save uses the same payload, but exercise disk save/load independently as well.
        var temp = Path.Combine(Path.GetTempPath(), "dof-formable-test-" + Guid.NewGuid().ToString("N"));
        SaveSystem.Save(temp, "formation", c, "tests");
        var saved = SaveSystem.Load(temp, "formation", "tests");
        Check(saved.Success && saved.Campaign.NationById(id).FormedNationId == korea.Id, "disk save/load preserves proclamation");
        Console.WriteLine("Test saves retained: " + temp);
        TestScenarios(c, map, temp);
    }

    static void TestScenarios(Campaign c, ProvinceTopology map, string temp)
    {
        var before = CampaignFingerprint.Of(c);
        var world = SaveSystem.ExportScenarioJson(c, "test_world");
        Check(before == CampaignFingerprint.Of(c), "scenario export is read-only");
        var restored = SaveSystem.ImportScenarioJson(world, "test_world", map.ProvinceCount);
        restored.Date = new GameDate(1836, 1, 1);
        restored.Player.Treasury = 7.25f;
        restored.Defines.OrderSettleDays = 77;
        restored.Wars.Clear();
        restored.Armies.Clear();
        restored.AttachProvinceTopology(map);
        Check(restored.ScenarioId == "test_world" && restored.Wars.Count == 0 && restored.Armies.Count == 0,
            "scenario import/attach does not seed contemporary wars or armies");
        var independent = SaveSystem.ExportScenarioJson(restored, "alternate_world");
        var second = SaveSystem.ImportScenarioJson(independent, "alternate_world", map.ProvinceCount);
        Check(second.Date.Year == 1836 && second.Player.Treasury == 7.25f && second.Defines.OrderSettleDays == 77,
            "scenario date treasury and rules isolated from 2024");
        second.AttachProvinceTopology(map);
        Check(second.ScenarioTerritories.Count > 0 && second.ProvinceController.SequenceEqual(restored.ProvinceController), "scenario political layer preserved");
        SaveSystem.Save(temp, "scenario", second, "tests");
        var loaded = SaveSystem.Load(temp, "scenario", "tests").Campaign;
        Check(loaded.ScenarioId == "alternate_world" && loaded.Defines.OrderSettleDays == 77 && loaded.Wars.Count == 0,
            "save roundtrip preserves independent scenario rules");
        var rejected = false;
        try { SaveSystem.ImportScenarioJson(independent, "bad", map.ProvinceCount + 1); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "mismatched map rejected before adoption");
        var root = Path.Combine(temp, "editor");
        ContentWorkbench.EnsureMod(root);
        var path = ContentWorkbench.Write(root, "gui/test.json", "{\"value\":1}");
        ContentWorkbench.Write(root, "gui/test.json", "{\"value\":2}");
        Check(File.ReadAllText(path).Contains("2") && File.ReadAllText(path + ".bak").Contains("1"), "editor atomic overwrite keeps backup");
        rejected = false;
        try { ContentWorkbench.Write(root, "gui/../../outside.json", "{}"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(temp, "outside.json")), "editor path traversal rejected");
        Check(ContentWorkbench.SetValue("{\"a/b\":[1]}", "/a~1b/0", "42").Contains("42"), "property editor supports escaped paths and arrays");
        ContentWorkbench.Write(root, "scenarios/test/world.json", independent);
        ContentWorkbench.Write(root, "scenarios/test/scenario.json", "{\"id\":\"test\",\"name\":\"Test world\",\"world\":\"world.json\"}");
        var entries = ScenarioLibrary.Discover(Array.Empty<string>(), Path.Combine(root, "scenarios"));
        Check(entries.Any(e => e.Id == "2024") && entries.Any(e => e.Id == "test"), "library discovers base and independent scenarios");
        var scenario = ScenarioLibrary.Create(entries.Single(e => e.Id == "test"), 123, map.ProvinceCount);
        Check(scenario.Wars.Count == 0 && scenario.Date.Year == 1836, "library loads complete scenario without bootstrap");
        Check(scenario.Seed == 123 && scenario.Random.State == new SimRandom(123).State &&
              scenario.Player.Treasury == 7.25f,
            "chosen seed reseeds an authored scenario without replacing its historical data");
        Loc.ResetCatalog(); Loc.Language = "russian";
        var rus = new Nation { Tag = "RUS", Name = "Russia" };
        Check(rus.DisplayName == "Россия" && rus.MapName == "Россия", "Russian identity and map names use stable keys");
        Loc.Language = "english";
        Check(rus.DisplayName == "Russia", "missing localized stable key falls back to country name");
    }
}
