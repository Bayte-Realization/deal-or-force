using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Works out how many people live in each region of the 2024 map, and writes it to
/// game/history/states/2024/20_population.json.
///
/// Most regions on this map are several real provinces merged into one, so no official table
/// fits them; the only way to a figure is from the map. A country keeps its national total,
/// and what changes is where its people are:
///
///   - its town-dwellers (the country's urbanisation) go where its towns are. Each place in
///     Natural Earth's populated places is spread over the land around it, on a radius that
///     grows with its size, because a metro is not a point - New York is half New Jersey,
///     and Moscow is half of Moscow oblast.
///   - everybody else goes to the land: farmland first, hills and forest less, steppe, desert
///     and tundra hardly at all, and the cold north less again. Ground of the same class
///     carries forty times the people in Bihar that it does on the Great Plains, and what
///     tells them apart is how settled the country around it is - so the land is weighted by
///     a wide, soft field of the towns' populations, raised to a power under one so a megacity
///     does not own its whole hinterland.
///
/// Regions whose population is already written down - the compiled region corrections, or
/// another history file - keep it, and the rest share what is left.
///
/// Usage, from the project root:
///   dotnet run --project Tools/RegionPopulation -c Release -- path/to/ne_10m_populated_places_simple.dbf [--write]
///
/// The places file is Natural Earth's (public domain): naturalearthdata.com, "Populated
/// Places", the simple variant. It is not shipped with the game; nothing reads it at runtime.
/// Without --write the tool only reports how close the model comes to regions whose real
/// population is known.
/// </summary>
public static class Program
{
    // Rural people per unit of land, by the class of ground, relative to farmland.
    private static readonly Dictionary<string, double> Ground = new(StringComparer.OrdinalIgnoreCase)
    {
        ["farmland"] = 1.0, ["urban"] = 1.0, ["hills"] = 0.4, ["jungle"] = 0.3, ["forest"] = 0.2,
        ["plains"] = 0.15, ["mountains"] = 0.15, ["steppe"] = 0.03, ["marsh"] = 0.03,
        ["desert"] = 0.002, ["tundra"] = 0.001, ["ice"] = 0.0
    };

    private const double MetroKmPerMillionRoot = 25;   // a million-strong town spreads over 25 km
    private const double MetroCapKm = 90;
    private const double SuburbKm = 15;                 // a smaller place this close is the same town
    private const double SettledFieldKm = 150;
    private const double SettledPower = 0.5;
    private const long Floor = 1_000;
    // Nowhere is all field or all pavement: small towns the places file does not list are
    // still towns, and even Moscow has its villages.
    private const double MinimumUrban = 0.05;
    private const double MaximumUrban = 0.97;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("Usage: RegionPopulation <ne_10m_populated_places_simple.dbf> [--write]");
            return 1;
        }
        var write = args.Contains("--write");
        var output = Path.Combine("game", "history", "states", "2024", "20_population.json");

        // The countries' own totals, not the ones this file produced last time: the deal
        // reads the file, so with it in place a country is already the sum of what was
        // written, and a mistake in the last run would be carried into the next. The file
        // stands aside while the world is built and comes back afterwards unless replaced.
        var aside = output + ".previous";
        if (File.Exists(output)) File.Move(output, aside, true);
        Campaign campaign;
        try { campaign = Campaign.CreateBootstrap(20260830); }
        finally { if (File.Exists(aside)) File.Move(aside, output, true); }
        // The regions as the deal sees them. Attaching the map below carves a few new regions
        // out of old ones (the occupied parts of a front) and hands some to the occupier, but
        // the deal runs before any of that: a carved region's people are its parent's at the
        // deal, and the carve takes them from the parent afterwards. Everything is counted
        // against these, not against the map as it ends up.
        var dealt = campaign.States.ToDictionary(state => state.MapIndex);
        var dealtOwner = campaign.States.ToDictionary(state => state.MapIndex, state => state.NationMapIndex);
        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
                Globality.EarthData.Width, Globality.EarthData.Height, out var map, out var message))
        {
            Console.WriteLine(message);
            return 1;
        }
        campaign.AttachProvinceTopology(map);
        int width = map.Width, height = map.Height;
        var cellKm = 40075.0 / width;
        var stateByMap = campaign.States.ToDictionary(state => state.MapIndex);
        // The region a map cell counted for at the deal: itself, or what it was carved from.
        int Dealt(int mapIndex)
        {
            if (dealt.ContainsKey(mapIndex)) return mapIndex;
            return stateByMap.TryGetValue(mapIndex, out var carved) && dealt.ContainsKey(carved.ParentMapIndex)
                ? carved.ParentMapIndex : 0;
        }
        int OwnerAtDeal(int mapIndex) => dealtOwner.TryGetValue(Dealt(mapIndex), out var owner) ? owner : -1;

        // ---- the towns
        var places = ReadPlaces(args[0]);
        int StateAt(double latitude, double longitude)
        {
            // A coastal town's point can land a cell out to sea; look a few cells inland.
            for (var ring = 0; ring <= 4; ring++)
            for (var dy = -ring; dy <= ring; dy++)
            for (var dx = -ring; dx <= ring; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                var x = (((int)Math.Floor((longitude + 180.0) / 360.0 * width) + dx) % width + width) % width;
                var y = (int)Math.Floor((90.0 - latitude) / 180.0 * height) + dy;
                if (y < 0 || y >= height) continue;
                var province = map.CellToProvince[y * width + x];
                if (province == 0 || map.WaterByProvince[province]) continue;
                var region = Dealt(map.ProvinceToState[province]);
                if (region != 0) return region;
            }
            return 0;
        }
        var towns = new List<(double Latitude, double Longitude, long People, int State)>();
        foreach (var place in places.OrderByDescending(place => place.People))
        {
            if (place.People <= 0) continue;
            var state = StateAt(place.Latitude, place.Longitude);
            if (state == 0) continue;
            if (towns.Any(town => Km(town.Latitude, town.Longitude, place.Latitude, place.Longitude) < SuburbKm)) continue;
            towns.Add((place.Latitude, place.Longitude, place.People, state));
        }
        Console.WriteLine($"{places.Count:N0} places read, {towns.Count:N0} towns kept");

        // Each town over the land around it, inside its own country.
        var townPeople = new Dictionary<int, double>();
        foreach (var town in towns)
        {
            var radius = Math.Clamp(MetroKmPerMillionRoot * Math.Sqrt(town.People / 1e6), 6, MetroCapKm);
            var home = OwnerAtDeal(town.State);
            var shares = new Dictionary<int, double>();
            Disc(town.Latitude, town.Longitude, radius, width, height, cellKm, (cell, km2) =>
            {
                var province = map.CellToProvince[cell];
                if (province == 0 || map.WaterByProvince[province]) return;
                var state = Dealt(map.ProvinceToState[province]);
                if (state == 0 || OwnerAtDeal(state) != home) return;
                shares[state] = shares.GetValueOrDefault(state) + Math.Exp(-3.0 * km2 / (radius * radius));
            });
            var spread = shares.Values.Sum();
            if (spread <= 0) { townPeople[town.State] = townPeople.GetValueOrDefault(town.State) + town.People; continue; }
            foreach (var (state, share) in shares) townPeople[state] = townPeople.GetValueOrDefault(state) + town.People * share / spread;
        }

        // ---- the land, weighted by how settled the country around it is
        var field = new float[width * height];
        foreach (var town in towns)
            Disc(town.Latitude, town.Longitude, SettledFieldKm, width, height, cellKm, (cell, km2) =>
                field[cell] += (float)(town.People * Math.Exp(-3.0 * km2 / (SettledFieldKm * SettledFieldKm))));
        var land = new Dictionary<int, double>();
        for (var y = 0; y < height; y++)
        {
            var latitude = 90.0 - (y + 0.5) * 180.0 / height;
            var area = Math.Cos(latitude * Math.PI / 180.0) * Cold(latitude);
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                var province = map.CellToProvince[cell];
                if (province == 0 || map.WaterByProvince[province]) continue;
                var state = Dealt(map.ProvinceToState[province]);
                if (state == 0) continue;
                var ground = Ground.TryGetValue(map.TerrainAt(province).Id, out var weight) ? weight : 0.3;
                land[state] = land.GetValueOrDefault(state) + area * ground * Math.Pow(field[cell] + 1000.0, SettledPower);
            }
        }

        // ---- what is already written down, and left alone
        var stated = new Dictionary<int, long>();
        var statedUrban = new HashSet<int>();
        var folder = Path.Combine("game", "history", "states", "2024");
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), Path.GetFileName(output), StringComparison.OrdinalIgnoreCase)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var region = campaign.States.FirstOrDefault(candidate =>
                    candidate.Id.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Iso.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (region is null) continue;
                if (entry.Value.TryGetProperty("population", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var people)) stated[region.MapIndex] = people;
                if (entry.Value.TryGetProperty("urbanization", out var share) && share.ValueKind == JsonValueKind.Number)
                    statedUrban.Add(region.MapIndex);
            }
        }

        // ---- the deal
        var result = new Dictionary<string, long>();
        var byNation = new SortedDictionary<string, List<(ScenarioState State, long People, float? Urban)>>(StringComparer.Ordinal);
        foreach (var nation in campaign.Nations)
        {
            var regions = campaign.States.Where(state => dealt.ContainsKey(state.MapIndex) &&
                dealtOwner[state.MapIndex] == nation.MapIndex && !LakeRegions.IsWaterState(state)).ToArray();
            if (regions.Length < 2 || nation.Population <= 0) continue;

            var everyone = (double)nation.Population;
            var inTowns = regions.Sum(state => townPeople.GetValueOrDefault(state.MapIndex));
            var townShare = inTowns > 0 ? EconomySystem.NationalUrbanisation(nation) * everyone : 0;
            var onLand = regions.Sum(state => land.GetValueOrDefault(state.MapIndex));
            double Towns(ScenarioState state) =>
                inTowns > 0 ? townShare * townPeople.GetValueOrDefault(state.MapIndex) / inTowns : 0;
            double Model(ScenarioState state) => Towns(state) +
                (onLand > 0 ? (everyone - townShare) * land.GetValueOrDefault(state.MapIndex) / onLand
                            : (everyone - townShare) / regions.Length);

            long written = 0;
            var open = new List<ScenarioState>();
            foreach (var state in regions)
                if (stated.TryGetValue(state.MapIndex, out var people) || TerritorialTopology.AuthoredPopulation(state.MapIndex, out people))
                    written += people;
                else open.Add(state);
            if (open.Count == 0) continue;

            var remaining = Math.Max(0L, nation.Population - written);
            var weight = open.Sum(Model);
            var exact = open.Select(state => weight > 0 ? remaining * Model(state) / weight : remaining / (double)open.Count).ToArray();
            // Nobody is left with nobody: lift the smallest to the floor and take it back from the rest.
            for (var pass = 0; pass < 4; pass++)
            {
                double lifted = 0, liftable = 0;
                for (var index = 0; index < exact.Length; index++)
                    if (exact[index] < Floor) { lifted += Floor - exact[index]; exact[index] = Floor; }
                    else liftable += exact[index];
                if (lifted <= 0 || liftable <= 0) break;
                for (var index = 0; index < exact.Length; index++)
                    if (exact[index] > Floor) exact[index] -= lifted * exact[index] / liftable;
            }
            // Whole people, summing to the country exactly.
            var whole = exact.Select(value => (long)Math.Floor(value)).ToArray();
            var shortBy = remaining - whole.Sum();
            foreach (var index in Enumerable.Range(0, exact.Length).OrderByDescending(index => exact[index] - whole[index])
                         .Take((int)Math.Max(0, shortBy)))
                whole[index]++;

            var list = new List<(ScenarioState, long, float?)>();
            for (var index = 0; index < open.Count; index++)
            {
                result[open[index].Id] = whole[index];
                // How much of the region lives in towns, by the same model that placed its people:
                // a city region is nearly all town, and the land behind it hardly any.
                var model = Model(open[index]);
                float? urban = statedUrban.Contains(open[index].MapIndex) || model <= 0
                    ? null
                    : (float)Math.Round(Math.Clamp(Towns(open[index]) / model, MinimumUrban, MaximumUrban), 2);
                list.Add((open[index], whole[index], urban));
            }
            byNation[nation.Tag] = list;
        }

        Check(campaign, result);

        if (!write) return 0;
        var text = new StringBuilder("{\n");
        text.Append(Header);
        var total = byNation.Values.Sum(list => list.Count);
        var done = 0;
        foreach (var (tag, list) in byNation)
        {
            var nation = campaign.NationByTag(tag);
            text.Append($"\n  // {tag} {nation.Name}: {nation.Population.ToString("N0", CultureInfo.InvariantCulture)}\n");
            foreach (var (state, people, urban) in list.OrderByDescending(row => row.People))
            {
                done++;
                var share = urban is { } value ? $", \"urbanization\": {value.ToString("0.00", CultureInfo.InvariantCulture)}" : "";
                text.Append($"  \"{state.Id}\": {{ \"population\": {people}{share} }}{(done < total ? "," : "")}   // {state.Name}\n");
            }
        }
        text.Append("}\n");
        File.WriteAllText(output, text.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"wrote {done:N0} regions in {byNation.Count:N0} countries to {output}");
        return 0;
    }

    /// <summary>Less ground to live on the further north or south it is, past fifty degrees.</summary>
    private static double Cold(double latitude)
    {
        var degrees = Math.Abs(latitude);
        return degrees <= 50 ? 1.0 : Math.Clamp((68 - degrees) / 18.0, 0.03, 1.0);
    }

    /// <summary>Every raster cell within a radius of a point, with its squared distance.</summary>
    private static void Disc(double latitude, double longitude, double radiusKm, int width, int height, double cellKm,
        Action<int, double> visit)
    {
        var cy = (int)Math.Floor((90.0 - latitude) / 180.0 * height);
        var cx = (int)Math.Floor((longitude + 180.0) / 360.0 * width);
        var squeeze = Math.Max(0.1, Math.Cos(latitude * Math.PI / 180));
        var reachY = (int)Math.Ceiling(radiusKm / cellKm) + 1;
        var reachX = (int)Math.Ceiling(radiusKm / (cellKm * squeeze)) + 1;
        for (var dy = -reachY; dy <= reachY; dy++)
        {
            var y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (var dx = -reachX; dx <= reachX; dx++)
            {
                var km2 = Math.Pow(dy * cellKm, 2) + Math.Pow(dx * cellKm * squeeze, 2);
                if (km2 > radiusKm * radiusKm) continue;
                visit(y * width + ((cx + dx) % width + width) % width, km2);
            }
        }
    }

    private static double Km(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var radians = Math.PI / 180;
        var a = Math.Sin((latitude2 - latitude1) * radians / 2);
        var b = Math.Sin((longitude2 - longitude1) * radians / 2);
        var h = a * a + Math.Cos(latitude1 * radians) * Math.Cos(latitude2 * radians) * b * b;
        return 12742 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Name, position and population of every place, from the shapefile's dBase table.</summary>
    private static List<(double Latitude, double Longitude, long People)> ReadPlaces(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = BitConverter.ToInt32(bytes, 4);
        var headerLength = BitConverter.ToInt16(bytes, 8);
        var recordLength = BitConverter.ToInt16(bytes, 10);
        var fields = new Dictionary<string, (int Offset, int Length)>(StringComparer.OrdinalIgnoreCase);
        var offset = 1;
        for (var at = 32; bytes[at] != 0x0D; at += 32)
        {
            var name = Encoding.ASCII.GetString(bytes, at, 11).TrimEnd('\0');
            fields[name] = (offset, bytes[at + 16]);
            offset += bytes[at + 16];
        }
        string Field(int record, string name) => Encoding.UTF8.GetString(bytes,
            headerLength + record * recordLength + fields[name].Offset, fields[name].Length).Trim();

        var places = new List<(double, double, long)>();
        for (var record = 0; record < count; record++)
        {
            if (bytes[headerLength + record * recordLength] == '*') continue;
            double.TryParse(Field(record, "latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude);
            double.TryParse(Field(record, "longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude);
            long.TryParse(Field(record, "pop_max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var people);
            places.Add((latitude, longitude, people));
        }
        return places;
    }

    /// <summary>
    /// How close the model comes to regions whose population is known, in millions: Russia's
    /// federal subjects (2021 census) and the United States (2020 census), by region id where
    /// the map merged subjects, by name otherwise. Approximate, and only a check - the model
    /// never reads them.
    /// </summary>
    private static void Check(Campaign campaign, Dictionary<string, long> result)
    {
        var known = new Dictionary<string, double>
        {
            ["ru_mow"] = 21.5, ["ru_len"] = 7.6, ["ru_kda"] = 6.3, ["ru_sve"] = 4.27, ["ru_ros"] = 4.2, ["ru_ba"] = 4.09,
            ["ru_ta"] = 4.0, ["ru_che"] = 3.43, ["ru_sam"] = 3.17, ["ru_da"] = 3.18, ["ru_niz"] = 3.12, ["ru_sta"] = 2.9,
            ["ru_kya"] = 2.86, ["ru_nvs"] = 2.8, ["ru_kem"] = 2.6, ["ru_per"] = 2.53, ["ru_vgg"] = 2.49, ["ru_sar"] = 2.44,
            ["ru_irk"] = 2.37, ["ru_vor"] = 2.3, ["ru_alt"] = 2.16, ["ru_ore"] = 1.86, ["ru_oms"] = 1.86, ["ru_pri"] = 1.85,
            ["ru_khm"] = 1.71, ["ru_tyu"] = 1.6, ["ru_bel"] = 1.54, ["ru_tul"] = 1.5, ["ru_ud"] = 1.45, ["ru_vla"] = 1.34,
            ["ru_kha"] = 1.29, ["ru_pnz"] = 1.27, ["ru_tve"] = 1.23, ["ru_uly"] = 1.2, ["ru_yar"] = 1.2, ["ru_cu"] = 1.19,
            ["ru_bry"] = 1.17, ["ru_kir"] = 1.15, ["ru_lip"] = 1.14, ["ru_vlg"] = 1.14, ["ru_rya"] = 1.1, ["ru_krs"] = 1.08,
            ["ru_klu"] = 1.07, ["ru_tom"] = 1.06, ["ru_kgd"] = 1.03, ["ru_ark"] = 1.03, ["ru_sa"] = 1.0, ["ru_zab"] = 1.0,
            ["ru_bu"] = 0.98, ["ru_tam"] = 0.98, ["ru_ast"] = 0.96, ["ru_iva"] = 0.93, ["ru_kb"] = 0.9, ["ru_smo"] = 0.9,
            ["ru_kgn"] = 0.78, ["ru_mo"] = 0.78, ["ru_amu"] = 0.77, ["ru_ko"] = 0.74, ["ru_orl"] = 0.71, ["ru_se"] = 0.69,
            ["ru_me"] = 0.68, ["ru_mur"] = 0.67, ["ru_psk"] = 0.6, ["ru_ngr"] = 0.58, ["ru_kos"] = 0.58, ["ru_kr"] = 0.53,
            ["ru_kk"] = 0.53, ["ru_yan"] = 0.51, ["ru_kc"] = 0.47, ["ru_sak"] = 0.47, ["ru_ty"] = 0.34, ["ru_kam"] = 0.29,
            ["ru_kl"] = 0.27, ["ru_al"] = 0.21, ["ru_yev"] = 0.15, ["ru_mag"] = 0.136, ["ru_chu"] = 0.048, ["ru_nen"] = 0.042,
            ["California"] = 39.5, ["Texas"] = 29.1, ["Florida"] = 21.5, ["New York"] = 20.2, ["Pennsylvania"] = 13.0,
            ["Illinois"] = 12.8, ["Ohio"] = 11.8, ["Georgia"] = 10.7, ["North Carolina"] = 10.4, ["Michigan"] = 10.1,
            ["New Jersey"] = 9.3, ["Virginia"] = 8.6, ["Washington"] = 7.7, ["Arizona"] = 7.2, ["Massachusetts"] = 7.0,
            ["Tennessee"] = 6.9, ["Indiana"] = 6.8, ["Maryland"] = 6.2, ["Missouri"] = 6.2, ["Wisconsin"] = 5.9,
            ["Colorado"] = 5.8, ["Minnesota"] = 5.7, ["South Carolina"] = 5.1, ["Alabama"] = 5.0, ["Louisiana"] = 4.7,
            ["Kentucky"] = 4.5, ["Oregon"] = 4.2, ["Oklahoma"] = 4.0, ["Connecticut"] = 3.6, ["Utah"] = 3.3, ["Iowa"] = 3.2,
            ["Nevada"] = 3.1, ["Arkansas"] = 3.0, ["Mississippi"] = 2.96, ["Kansas"] = 2.94, ["New Mexico"] = 2.12,
            ["Nebraska"] = 1.96, ["Idaho"] = 1.84, ["West Virginia"] = 1.79, ["Hawaii"] = 1.46, ["New Hampshire"] = 1.38,
            ["Maine"] = 1.36, ["Montana"] = 1.08, ["Rhode Island"] = 1.1, ["Delaware"] = 0.99, ["South Dakota"] = 0.89,
            ["North Dakota"] = 0.78, ["Alaska"] = 0.73, ["Vermont"] = 0.64, ["Wyoming"] = 0.58,
        };
        var errors = new List<double>();
        foreach (var (key, millions) in known)
        {
            var state = campaign.States.FirstOrDefault(candidate => candidate.Id == key) ??
                        campaign.States.FirstOrDefault(candidate => candidate.Name == key && candidate.Id.StartsWith("us_"));
            if (state is null || !result.TryGetValue(state.Id, out var people)) continue;
            errors.Add(Math.Log(people / (millions * 1e6)));
        }
        if (errors.Count == 0) return;
        var sizes = errors.Select(Math.Abs).OrderBy(error => error).ToList();
        errors.Sort();
        Console.WriteLine($"{errors.Count} known regions: typically within x{Math.Exp(sizes[sizes.Count / 2]):0.00}; " +
                          $"eight in ten between x{Math.Exp(errors[errors.Count / 10]):0.00} and x{Math.Exp(errors[errors.Count * 9 / 10]):0.00}");
    }

    private const string Header =
"""
  // How many people live in each region on 1 January 2024.
  //
  // Before this file, a country's people were dealt out almost evenly across its regions, so
  // Magadan had as many people as Moscow and Iowa as many as Florida. Everything that reads a
  // region's population inherited that: which region a country recruits from, where a lost
  // capital moves to, how much a region weighs in stability, what a peace is worth.
  //
  // These are estimates, not a census. Most regions on this map are several real provinces
  // merged together, so no official table fits them. The figures were worked out from the map
  // by Tools/RegionPopulation: a country's town-dwellers go where its towns are, spread over
  // the land around each town (Natural Earth populated places, public domain), and the rest
  // go to farmland first, hills and forest less, desert and tundra hardly at all, weighted by
  // how settled the country around them is. Against regions whose real population is known,
  // the typical figure is within a quarter of the truth.
  //
  // A region listed here has exactly the people written. A country's total is then the sum of
  // its regions, so raising one region raises the country. A region left out shares whatever
  // of the country's figure (history/countries, or the map's) the listed ones leave. Countries
  // with a single region are not listed: they have nowhere else to put anyone.
  //
  // "urbanization" is how much of the region lives in towns, from the same model: a city
  // region is nearly all town, the land behind it hardly any. It decides which trades the
  // region's people follow, and whether its share of the country's buildings is farms or
  // factories. A region without one is spread around its country's figure.
  //
  // A mod changes a region by listing it again in a later file of its own.

""";
}
