using System.Text.Json.Nodes;
using DealOrForce.Simulation;
using Map = Globality.EarthData;

// Stage into a separate folder. Never overwrite the live map or renumber world provinces.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".fennara/artifacts/south_ossetia_border");
Directory.CreateDirectory(output);
var catalog = JsonNode.Parse(File.ReadAllText("game/map_data/state_regions/00_region_overrides.json"), documentOptions: GameDataLoader.ForgivingJson);
var oldRule = catalog["regions"].AsArray().First(n => n["map_index"].GetValue<int>() == 1712).DeepClone().AsObject();
oldRule.Remove("polygon"); oldRule.Remove("replace_existing"); oldRule.Remove("source_nation");
TerritorialTopology.LoadOverrides(new JsonObject { ["regions"] = new JsonArray(oldRule) }.ToJsonString(), new());
var campaign = Campaign.CreateBootstrap(20260830);
TerritorialTopology.Apply(campaign, Map.Control, Map.States, Map.Width, Map.Height);
if (!MapRasterCatalog.TryBuildProvinceTopology(Map.States, Map.Control, Map.Width, Map.Height, out var old, out var message)) throw new Exception(message);
var oldCells = (ushort[])old.CellToProvince.Clone();
var oldStates = (ushort[])Map.States.Clone(); var oldOwners = (ushort[])Map.Control.Clone();
TerritorialTopology.ResetCatalog();
TerritorialTopology.Apply(campaign, Map.Control, Map.States, Map.Width, Map.Height);
var repaired = StableProvinceBorders.Reconcile(old, Map.States, Map.Control);
if (repaired.ProvinceCount != old.ProvinceCount) throw new Exception("Province count changed.");
var changedCells = 0; var politicalCells = 0;
for (var i = 0; i < oldCells.Length; i++)
{
    if (oldCells[i] != repaired.CellToProvince[i]) changedCells++;
    if (oldStates[i] != Map.States[i] || oldOwners[i] != Map.Control[i]) politicalCells++;
    var p = repaired.CellToProvince[i];
    if (p > 0 && (repaired.ProvinceToState[p] != Map.States[i] || repaired.ProvinceToNation[p] != Map.Control[i])) throw new Exception("Pixel / province mismatch.");
    if (oldOwners[i] != campaign.NationByTag("GEO").MapIndex && oldOwners[i] != campaign.NationByTag("OST").MapIndex && oldCells[i] != repaired.CellToProvince[i]) throw new Exception("Changed unrelated territory.");
}
if (!MapRasterCatalog.TryLoadHydrology(Map.Width, Map.Height, out var hydro, out message)) throw new Exception(message);
MapRasterCatalog.Export(output, repaired, hydro, Map.States);
var history = new List<string> { "# Local South Ossetia contour repair; existing province IDs preserved.", "province;owner;controller" };
for (var p = 1; p <= old.ProvinceCount; p++)
    if (old.ProvinceToNation[p] != repaired.ProvinceToNation[p])
    {
        var tag = campaign.Nations.First(n => n.MapIndex == repaired.ProvinceToNation[p]).Tag;
        history.Add($"{p};{tag};{tag}");
    }
File.WriteAllLines(Path.Combine(output, "zz_south_ossetia_border.csv"), history);
// Diagnostic crop drawn from the same province IDs that clicks and armies use.
var west = 43.1; var east = 44.95; var north = 43.0; var south = 41.8;
const int w = 740, h = 480; var rgb = new byte[w * h * 3];
var ost = campaign.NationByTag("OST").MapIndex; var rus = campaign.NationByTag("RUS").MapIndex;
for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
{
    var sx = (int)((west + (east - west) * x / w + 180) / 360 * Map.Width);
    var sy = (int)((90 - north + (north - south) * y / h) / 180 * Map.Height);
    var p = repaired.CellToProvince[sy * Map.Width + sx]; var owner = repaired.ProvinceToNation[p];
    var color = owner == ost ? 0xC6AA57 : owner == rus ? 0x61766B : 0x324E63;
    if (x > 0 && repaired.CellToProvince[sy * Map.Width + sx - 1] != p) color = 0x142635;
    var i = (y * w + x) * 3; rgb[i] = (byte)(color >> 16); rgb[i + 1] = (byte)(color >> 8); rgb[i + 2] = (byte)color;
}
DataMapPng.SaveRgb(Path.Combine(output, "contour_review.png"), w, h, rgb);
Console.WriteLine($"Preserved {old.ProvinceCount} province IDs; changed {politicalCells} political pixels, {changedCells} province pixels, {history.Count - 2} owner records. Staged in {output}");
