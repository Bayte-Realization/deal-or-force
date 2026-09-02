using DealOrForce.Simulation;
using SourceEarthData = Globality.EarthData;

var projectRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var output = Path.Combine(projectRoot, "game", "map_data");
Directory.SetCurrentDirectory(projectRoot);

var campaign = Campaign.CreateBootstrap(20260830);
TerritorialTopology.ResetCatalog();
HydrologyCatalog.ResetCatalog();
var repairs = TerritorialTopology.Apply(campaign, SourceEarthData.Control, SourceEarthData.States, SourceEarthData.Width, SourceEarthData.Height);
var provinces = ProvinceTopology.Build(SourceEarthData.States, SourceEarthData.Control,
    SourceEarthData.Width, SourceEarthData.Height, cell => SourceEarthData.DisputeAt(cell) != 0);
provinces = provinces.RelaxShapes(SourceEarthData.States, SourceEarthData.Control);

// The compiler deliberately builds physical pixels from the vector sources. Existing
// PNGs are outputs, not inputs, so repeated compilation is deterministic.
var hydrology = HydrologyCatalog.BuildVectorRaster(SourceEarthData.Width, SourceEarthData.Height);
MapRasterCatalog.Export(output, provinces, hydrology);

MapRasterCatalog.ResetCatalog();
if (!MapRasterCatalog.TryBuildProvinceTopology(SourceEarthData.States, SourceEarthData.Control,
        SourceEarthData.Width, SourceEarthData.Height, out var roundTrip, out var provinceMessage))
    throw new InvalidDataException($"Province round trip failed: {provinceMessage}");
if (roundTrip.ProvinceCount != provinces.ProvinceCount || roundTrip.EdgeCount != provinces.EdgeCount ||
    !roundTrip.CellToProvince.AsSpan().SequenceEqual(provinces.CellToProvince))
    throw new InvalidDataException("Province PNG round trip changed IDs, cells or adjacency.");
if (!MapRasterCatalog.TryLoadHydrology(SourceEarthData.Width, SourceEarthData.Height, out var rasterHydrology, out var hydroMessage))
    throw new InvalidDataException($"Hydrology round trip failed: {hydroMessage}");
var sourceWetCells = 0;
var rasterWetCells = 0;
for (var cell = 0; cell < SourceEarthData.Width * SourceEarthData.Height; cell++)
{
    if (hydrology.Pixels[cell * 2] > 0 || hydrology.Pixels[cell * 2 + 1] > 0) sourceWetCells++;
    if (rasterHydrology.Pixels[cell * 2] > 0 || rasterHydrology.Pixels[cell * 2 + 1] > 0) rasterWetCells++;
}
if (sourceWetCells != rasterWetCells) throw new InvalidDataException("Hydrology PNG round trip changed its covered cells.");

// The political map is data too, and the file a modder edits has to be the one the game
// actually runs on rather than a hand-made approximation of it. It is written from the
// same topology that was just compiled, so the two can never describe different worlds.
campaign.AttachProvinceTopology(provinces);
var history = Path.Combine(projectRoot, "game", "history", "provinces");
Directory.CreateDirectory(history);
var ownership = ProvinceOwnership.Export(campaign, provinces, out var ownedProvinces);
File.WriteAllText(Path.Combine(history, "ownership.csv"), ownership);

Console.WriteLine($"Wrote {provinces.ProvinceCount:N0} provinces ({provinces.EdgeCount:N0} edges) to {output}");
Console.WriteLine($"Wrote the political map: {ownedProvinces:N0} owned provinces to {history}");
Console.WriteLine($"Hydrology: {HydrologyCatalog.RiverFeatures.Count:N0} rivers, {HydrologyCatalog.LakeFeatures.Count:N0} lakes");
Console.WriteLine($"PNG round trip: exact province IDs/adjacency, {rasterWetCells:N0} hydrology cells");
foreach (var repair in repairs.Where(item => item.ConnectedComponents > 0 || item.ClippedAway.Length > 0))
{
    Console.WriteLine($"{repair.SubjectTag}: {repair.ConnectedComponents} connected component(s)");
    if (repair.ClippedAway.Length > 0)
        Console.WriteLine($"  its boundary left out {repair.ClippedAway}");
}
