using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class Program
{
    static void Check(bool pass, string name)
    {
        if (!pass) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    static void Main()
    {
        var campaign = Campaign.CreateBootstrap(20260906);
        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        Check(TerrainCatalog.All.Count == 12, "12 terrain types and source assets loaded");
        Check(MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
            Globality.EarthData.Width, Globality.EarthData.Height, out var map, out var message), message);
        campaign.AttachProvinceTopology(map);
        Check(map.TerrainByProvince.Length == map.ProvinceCount + 1 && map.TerrainByProvince.All(t => t is not null), "every province assigned");
        Check(map.TerrainSurfaceSlots.Length == map.Width * map.Height, "aligned surface layer");
        var names = map.TerrainByProvince.Select(t => t.Id).ToHashSet();
        Check(TerrainCatalog.All.All(t => names.Contains(t.Id)), "all types occur on the shipped map");
        Check(map.CloneForOverrides().TerrainAt(1) == map.TerrainAt(1), "political clone preserves geographical terrain");
        Check(TerrainCatalog.Get("mountains").MovementSpeed < TerrainCatalog.Get("plains").MovementSpeed &&
              TerrainCatalog.Get("mountains").Defense > 1 && TerrainCatalog.Get("mountains").CombatWidth < 1, "terrain changes march, defense and frontage");
        var rus = campaign.NationByTag("RUS"); var ukr = campaign.NationByTag("UKR");
        campaign.SetPlayer(rus.Id);
        var available = BorderFronts.Candidates(campaign, rus.Id, ukr.Id);
        Check(available.Count > 0, "Russian-Ukrainian shared frontier exists");
        var seed = available.OrderBy(p => p).First(); var point = map.CenterOf(seed);
        var armies = campaign.Armies.Where(a => a.NationId == rus.Id).Take(3).Select(a => a.Id).ToArray();
        new DrawFrontLineCommand { IssuerId = rus.Id, BorderNationId = ukr.Id, Kind = FrontLineKind.Defence,
            WholeBorder = true, StartLatitude = point.Latitude, StartLongitude = point.Longitude,
            EndLatitude = point.Latitude, EndLongitude = point.Longitude, Provinces = new[] { seed }, ArmyIds = armies }.Apply(campaign);
        var line = campaign.FrontLines.Last();
        Check(line.Provinces.Count > 1 && line.Provinces.All(available.Contains), "whole-front order only uses a shared border");
        var id = line.Id;
        new DrawFrontLineCommand { IssuerId = rus.Id, BorderNationId = ukr.Id, EditLineId = id,
            Kind = FrontLineKind.Defence, WholeBorder = false, StartLatitude = point.Latitude, StartLongitude = point.Longitude,
            EndLatitude = point.Latitude, EndLongitude = point.Longitude, Provinces = new[] { seed }, ArmyIds = armies }.Apply(campaign);
        line = campaign.FrontLines.Single(l => l.Id == id);
        Check(line.ArmyIds.Count == 3 && line.Provinces.Count == 1, "editing preserves line identity and assignments");
        campaign.SetProvinceController(seed, (ushort)ukr.MapIndex);
        BorderFronts.Refresh(campaign, line);
        Check(!line.Provinces.Contains(seed) && line.Provinces.All(p => campaign.ProvinceController[p] == rus.MapIndex), "front follows control changes rather than captured province IDs");
        var snapshot = SaveSystem.ExportNetworkSnapshot(campaign, "terrain-test");
        var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "terrain-test");
        Check(restored.Success && restored.Campaign.FrontLines.Any(l => l.Id == id && l.BorderNationId == ukr.Id), "border intent survives snapshot/save serialization");
        var mod = Path.GetFullPath("Tools/TerrainTests/fixtures/mod");
        var errors = new List<string>(); var original = TerrainCatalog.Get("forest");
        TerrainCatalog.ApplyDirectory(mod, errors);
        Check(errors.Count == 0 && TerrainCatalog.Get("forest").MovementSpeed == .66f &&
            TerrainCatalog.Get("forest").Texture == original.Texture, "partial mod overrides inherit other fields");
        Check(TerrainCatalog.Get("volcanic").Id == "volcanic", "new moddable type without enum/code changes");
        map.AttachTerrain();
        Check(map.TerrainAt(1).Id == "volcanic", "per-province history override wins over raster");
        TerrainCatalog.ResetCatalog();
        Check(TerrainCatalog.All.Count == 12 && TerrainCatalog.Get("forest").MovementSpeed == original.MovementSpeed, "catalog reset removes mod state");
        Console.WriteLine("Terrain/front regression suite complete.");
    }
}
