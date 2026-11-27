using System.Globalization;
using System.Text;
using DealOrForce.Simulation;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

string? positionalRoot = null;
string? mapPackId = null;
string? outDirArg = null;
int? cellsOverride = null;
int? disputedCellsOverride = null;
float? siberiaMultOverride = null;
float? polarMultOverride = null;
int? minCellsOverride = null;
bool writeMapJson = false;
string? nameEnArg = null;
string? nameRuArg = null;
string? descEnArg = null;
string? descRuArg = null;
string? authorArg = null;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    string? NextValue() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {arg}");
    switch (arg)
    {
        case "--map-pack": mapPackId = NextValue()?.Trim(); break;
        case "--out-dir": outDirArg = NextValue(); break;
        case "--cells": cellsOverride = int.Parse(NextValue()!, CultureInfo.InvariantCulture); break;
        case "--disputed-cells": disputedCellsOverride = int.Parse(NextValue()!, CultureInfo.InvariantCulture); break;
        case "--siberia-mult": siberiaMultOverride = float.Parse(NextValue()!, CultureInfo.InvariantCulture); break;
        case "--polar-mult": polarMultOverride = float.Parse(NextValue()!, CultureInfo.InvariantCulture); break;
        case "--min-cells": minCellsOverride = int.Parse(NextValue()!, CultureInfo.InvariantCulture); break;
        case "--write-map-json": writeMapJson = true; break;
        case "--name-en": nameEnArg = NextValue(); break;
        case "--name-ru": nameRuArg = NextValue(); break;
        case "--desc-en": descEnArg = NextValue(); break;
        case "--desc-ru": descRuArg = NextValue(); break;
        case "--author": authorArg = NextValue(); break;
        case "--root": positionalRoot = NextValue(); break;
        default:
            if (arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown option: {arg}");
            positionalRoot ??= arg;
            break;
    }
}

var projectRoot = !string.IsNullOrWhiteSpace(positionalRoot)
    ? Path.GetFullPath(positionalRoot)
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
Directory.SetCurrentDirectory(projectRoot);

var isPackCompile = !string.IsNullOrWhiteSpace(mapPackId);
var output = isPackCompile
    ? (!string.IsNullOrWhiteSpace(outDirArg)
        ? Path.GetFullPath(outDirArg, projectRoot)
        : Path.Combine(projectRoot, "game", "maps", mapPackId!))
    : (!string.IsNullOrWhiteSpace(outDirArg)
        ? Path.GetFullPath(outDirArg, projectRoot)
        : Path.Combine(projectRoot, "game", "map_data"));

var profile = isPackCompile ? MapCatalog.BuiltInProfileFor(mapPackId) : ProvinceBuildProfile.Standard;
if (cellsOverride.HasValue || disputedCellsOverride.HasValue || siberiaMultOverride.HasValue ||
    polarMultOverride.HasValue || minCellsOverride.HasValue)
{
    profile = profile with
    {
        CellsPerProvince = cellsOverride ?? profile.CellsPerProvince,
        DisputedCellsPerProvince = disputedCellsOverride ?? profile.DisputedCellsPerProvince,
        SiberiaMultiplier = siberiaMultOverride ?? profile.SiberiaMultiplier,
        PolarMultiplier = polarMultOverride ?? profile.PolarMultiplier,
        MinimumProvinceCells = minCellsOverride ?? profile.MinimumProvinceCells
    };
}

var campaign = Campaign.CreateBootstrap(20260830);
TerritorialTopology.ResetCatalog();
HydrologyCatalog.ResetCatalog();
var repairs = TerritorialTopology.Apply(campaign, SourceEarthData.Control, SourceEarthData.States, SourceEarthData.Width, SourceEarthData.Height);
var provinces = ProvinceTopology.Build(
    SourceEarthData.States,
    SourceEarthData.Control,
    SourceEarthData.Width,
    SourceEarthData.Height,
    cell => SourceEarthData.DisputeAt(cell) != 0,
    profile);
var grown = provinces.ProvinceCount;
provinces = provinces.AbsorbSlivers(profile.MinimumProvinceCells);
Console.WriteLine($"Folded {grown - provinces.ProvinceCount:N0} leftover pockets under {profile.MinimumProvinceCells} cells into their neighbours");
provinces = provinces.RelaxShapes(SourceEarthData.States, SourceEarthData.Control);

if (isPackCompile)
{
    campaign.MapId = mapPackId!;
    Directory.CreateDirectory(output);
    MapRasterCatalog.ExportPack(output, provinces, SourceEarthData.States);

    var (defaultEn, defaultRu, defaultDescEn, defaultDescRu) = mapPackId switch
    {
        MapCatalog.TacticalId => (
            "Tactical World",
            "Тактический мир",
            "Tactical province layout with coarsened Siberia, unified Eastern Ukraine, and streamlined poles.",
            "Тактическая нарезка провинций с укрупнённой Сибирью, без микрофрагментации Восточной Украины и сжатыми полюсами."
        ),
        MapCatalog.CompactId => (
            "Compact World",
            "Компактный мир",
            "Fast grand-strategy layout tuned for rapid pace and mobile play.",
            "Компактная карта для высокого темпа и мобильной игры."
        ),
        _ => (
            mapPackId!,
            mapPackId!,
            $"Custom compiled map pack '{mapPackId}'.",
            $"Пользовательский пакет карты «{mapPackId}»."
        )
    };

    if (writeMapJson || isPackCompile)
    {
        var manifest = MapCatalog.SerializeManifest(
            id: mapPackId!,
            nameEn: nameEnArg ?? defaultEn,
            nameRu: nameRuArg ?? defaultRu,
            descriptionEn: descEnArg ?? defaultDescEn,
            descriptionRu: descRuArg ?? defaultDescRu,
            author: authorArg ?? "Bayte Realization",
            profile: profile,
            inheritsBaseHydrology: true);
        File.WriteAllText(Path.Combine(output, MapCatalog.ManifestFile), manifest, new UTF8Encoding(false));
    }

    MapRasterCatalog.ResetCatalog();
    MapRasterCatalog.ApplyDirectory(output, null);
    if (!MapRasterCatalog.TryBuildProvinceTopology(SourceEarthData.States, SourceEarthData.Control,
            SourceEarthData.Width, SourceEarthData.Height, out var packRoundTrip, out var packError))
        throw new InvalidDataException($"Province pack round trip failed: {packError}");
    if (packRoundTrip.ProvinceCount != provinces.ProvinceCount || packRoundTrip.EdgeCount != provinces.EdgeCount ||
        !packRoundTrip.CellToProvince.AsSpan().SequenceEqual(provinces.CellToProvince))
        throw new InvalidDataException("Province pack PNG round trip changed IDs, cells or adjacency.");

    ProvinceOwnership.Clear();
    campaign.AttachProvinceTopology(provinces);
    var packOwnership = ProvinceOwnership.Export(campaign, provinces, out var packOwnedProvinces);
    File.WriteAllText(Path.Combine(output, "ownership.csv"), packOwnership, new UTF8Encoding(false));

    Console.WriteLine($"Wrote map pack '{mapPackId}' ({provinces.ProvinceCount:N0} provinces, {provinces.EdgeCount:N0} edges) to {output}");
    Console.WriteLine($"Wrote pack ownership: {packOwnedProvinces:N0} owned provinces");
    return;
}

// The compiler deliberately builds physical pixels from the vector sources. Existing
// PNGs are outputs, not inputs, so repeated compilation is deterministic.
var hydrology = HydrologyCatalog.BuildVectorRaster(SourceEarthData.Width, SourceEarthData.Height);
MapRasterCatalog.Export(output, provinces, hydrology, SourceEarthData.States);

if (writeMapJson)
{
    var manifest = MapCatalog.SerializeManifest(
        id: MapCatalog.StandardId,
        nameEn: nameEnArg ?? "Standard World",
        nameRu: nameRuArg ?? "Стандартный мир",
        descriptionEn: descEnArg ?? "Standard high-detail 34,138-province world map.",
        descriptionRu: descRuArg ?? "Стандартная детализированная карта мира.",
        author: authorArg ?? "Bayte Realization",
        profile: profile,
        inheritsBaseHydrology: true);
    File.WriteAllText(Path.Combine(output, MapCatalog.ManifestFile), manifest, new UTF8Encoding(false));
}

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
//
// The existing file is dropped first. Attaching a topology applies whatever ownership is
// on disk, and that file was written against the previous province numbering - so keeping
// it here meant laying the old world over the new provinces and then exporting the result
// as if it were the truth. The provinces the compiler just built already know who owns
// them, because they were grown out of the source control raster one owner at a time.
ProvinceOwnership.Clear();
campaign.AttachProvinceTopology(provinces);
var history = Path.Combine(projectRoot, "game", "history", "provinces");
Directory.CreateDirectory(history);
var ownership = ProvinceOwnership.Export(campaign, provinces, out var ownedProvinces);
File.WriteAllText(Path.Combine(history, "ownership.csv"), ownership);

var countryFolder = Path.Combine(projectRoot, "game", "common", "countries");
Directory.CreateDirectory(countryFolder);
var countries = CountryDefinitions.Export(campaign, out var writtenCountries);
File.WriteAllText(Path.Combine(countryFolder, "00_countries.json"), countries);

var stateFolder = Path.Combine(projectRoot, "game", "common", "states");
Directory.CreateDirectory(stateFolder);
var states = StateDefinitions.Export(campaign, out var writtenStates);
File.WriteAllText(Path.Combine(stateFolder, "00_states.json"), states);

var fleetFolder = Path.Combine(projectRoot, "game", "history", "units", "2024");
Directory.CreateDirectory(fleetFolder);
var fleets = FleetHistory.Export(campaign, out var writtenFleets);
File.WriteAllText(Path.Combine(fleetFolder, "01_fleets.json"), fleets);

Console.WriteLine($"Wrote {provinces.ProvinceCount:N0} provinces ({provinces.EdgeCount:N0} edges) to {output}");
Console.WriteLine($"Wrote the political map: {ownedProvinces:N0} owned provinces to {history}");
Console.WriteLine($"Wrote {writtenCountries:N0} country definitions to {countryFolder}");
Console.WriteLine($"Wrote {writtenStates:N0} region definitions to {stateFolder}");
Console.WriteLine($"Wrote {writtenFleets:N0} fleets to {fleetFolder}");
Console.WriteLine($"Hydrology: {HydrologyCatalog.RiverFeatures.Count:N0} rivers, {HydrologyCatalog.LakeFeatures.Count:N0} lakes");
Console.WriteLine($"PNG round trip: exact province IDs/adjacency, {rasterWetCells:N0} hydrology cells");
foreach (var repair in repairs.Where(item => item.ConnectedComponents > 0 || item.ClippedAway.Length > 0))
{
    Console.WriteLine($"{repair.SubjectTag}: {repair.ConnectedComponents} connected component(s)");
    if (repair.ClippedAway.Length > 0)
        Console.WriteLine($"  its boundary left out {repair.ClippedAway}");
}
