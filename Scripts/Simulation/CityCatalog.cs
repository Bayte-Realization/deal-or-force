using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One city on the map: a name and a place, and nothing else. No city is more of a city than
/// another in the data - which one is a capital is the countries' business (Nation.CapitalCityId)
/// and changes as they move.
/// </summary>
public sealed class CityDefinition
{
    public string Id = "";
    public string Name = "";
    public float Latitude;
    public float Longitude;

    /// <summary>What the map calls it: city.&lt;id&gt;.name when the language has it, else the name.</summary>
    public string DisplayName => Loc.Has($"city.{Id}.name") ? Loc.Get($"city.{Id}.name") : Name;
}

/// <summary>
/// The cities drawn on the map (item 91), from game/map_data/cities.
///
/// Nothing about a city is simulated: it is geography, like a river. It exists so the map reads
/// like a map - Kharkiv and Donetsk where the war is, Kunming and Chengdu across a Chinese
/// province - so a front has places to be named after, and so a government has somewhere to
/// sit: a country's capital is one of these cities (CapitalCore). The region and province a
/// city stands in come from the map, never from the file, so it changes hands with its ground.
///
/// Every file in the folder is read in name order and later files win key by key, and a mod's
/// own map_data/cities files are laid over the base game's the same way when a campaign is set
/// up (<see cref="Merge"/>, called by ModLoader): a mod adds a city under a key of its own, moves
/// or renames one by naming its key again, and removes one with "removed": true. A file written
/// for the older shape - "population", "tier", "capital" - still loads: those keys are not read
/// (a "tier" of -1 was how a city used to be removed, and still is).
///
/// The cities keep the order they are written in, and that order means one thing: the earlier
/// a city stands, the sooner it is chosen when only one can be - the map letters it first when
/// there is no room for every name, and a government, a militia or a base that needs "a city of
/// this region" takes the first that stands there.
/// </summary>
public static class CityCatalog
{
    public readonly record struct MapAnchor(int ProvinceId, float Latitude, float Longitude);
    private static List<CityDefinition> _all;
    private static Dictionary<string, int> _indexById;
    private static readonly object Gate = new();

    /// <summary>Every city, in the order the files give them.</summary>
    public static IReadOnlyList<CityDefinition> All
    {
        get
        {
            lock (Gate) return _all ??= Load();
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _all = null;
            _indexById = null;
            _provinces = null;
            _firstInProvince = null;
        }
    }

    /// <summary>Where a city stands in <see cref="All"/>, or -1 when no city has that key.</summary>
    public static int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        var cities = All;
        lock (Gate)
        {
            if (_indexById is null)
            {
                _indexById = new Dictionary<string, int>(cities.Count, StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < cities.Count; index++) _indexById[cities[index].Id] = index;
            }
            return _indexById.TryGetValue(id, out var found) ? found : -1;
        }
    }

    /// <summary>The city of that key, or null.</summary>
    public static CityDefinition Find(string id)
    {
        var index = IndexOf(id);
        return index < 0 ? null : All[index];
    }

    /// <summary>
    /// How many of the first cities count as places an office or a base is put in: the leading
    /// third of the list. A long-range base and a movement's headquarters go to a city of some
    /// standing, not to the nearest hamlet, and with every city equal in the data the only
    /// standing there is is the order they are written in.
    /// </summary>
    public static int LeadingCount => (All.Count + 2) / 3;

    private static int[] _provinces;
    private static ProvinceTopology _provincesOf;
    private static Dictionary<int, int> _firstInProvince;

    /// <summary>
    /// The town a place on the map is known by: the nearest city of the same region to a
    /// province, no further off than a region's width usually is, or null. Four battles in the
    /// Kherson region read "Kherson · RUS" four times over; they are at Kherson, Nova Kakhovka,
    /// Beryslav and Henichesk.
    /// </summary>
    public static CityDefinition NearestTown(ProvinceTopology topology, int province, float withinDegrees = 1.2f)
    {
        if (topology is null || province <= 0 || province > topology.ProvinceCount) return null;
        var cities = All;
        var provinces = ProvincesOf(topology);
        var region = topology.ProvinceToState[province];
        var (latitude, longitude) = topology.CenterOf(province);
        var scale = MathF.Cos(latitude * MathF.PI / 180f);
        CityDefinition best = null;
        var bestDistance = withinDegrees * withinDegrees;
        for (var index = 0; index < cities.Count; index++)
        {
            var under = provinces[index];
            if (under <= 0 || topology.ProvinceToState[under] != region) continue;
            var city = cities[index];
            var east = MathF.Abs(city.Longitude - longitude);
            if (east > 180f) east = 360f - east;
            east *= scale;
            var north = city.Latitude - latitude;
            var distance = east * east + north * north;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = city;
        }
        return best;
    }

    /// <summary>
    /// The province each city of <see cref="All"/> stands in, index for index (0 for none),
    /// worked out once per map.
    /// </summary>
    public static int[] ProvincesOf(ProvinceTopology topology)
    {
        var cities = All;
        lock (Gate)
        {
            if (_provinces is null || !ReferenceEquals(_provincesOf, topology) || _provinces.Length != cities.Count)
            {
                _provinces = new int[cities.Count];
                for (var index = 0; index < cities.Count; index++)
                    _provinces[index] = AnchorOnLand(topology, cities[index].Latitude, cities[index].Longitude).ProvinceId;
                _provincesOf = topology;
                _firstInProvince = null;
            }
            return _provinces;
        }
    }

    /// <summary>The first city standing in a province, as an index into <see cref="All"/>, or -1.</summary>
    public static int IndexIn(ProvinceTopology topology, int province)
    {
        if (topology is null || province <= 0) return -1;
        var provinces = ProvincesOf(topology);
        lock (Gate)
        {
            if (_firstInProvince is null)
            {
                _firstInProvince = new Dictionary<int, int>();
                for (var index = 0; index < provinces.Length; index++)
                    if (provinces[index] > 0) _firstInProvince.TryAdd(provinces[index], index);
            }
            return _firstInProvince.TryGetValue(province, out var found) ? found : -1;
        }
    }

    /// <summary>The first city standing in a province, or null.</summary>
    public static CityDefinition CityIn(ProvinceTopology topology, int province)
    {
        var index = IndexIn(topology, province);
        return index < 0 ? null : All[index];
    }

    /// <summary>The province under a point of the globe, or 0 for none.</summary>
    public static int ProvinceUnder(ProvinceTopology topology, float latitude, float longitude)
    {
        if (topology is null || topology.Width <= 0 || topology.Height <= 0) return 0;
        var x = (int)MathF.Floor((longitude + 180f) / 360f * topology.Width);
        var y = (int)MathF.Floor((90f - latitude) / 180f * topology.Height);
        x = ((x % topology.Width) + topology.Width) % topology.Width;
        y = Math.Clamp(y, 0, topology.Height - 1);
        var province = topology.CellToProvince[y * topology.Width + x];
        return province > 0 && province <= topology.ProvinceCount && !topology.IsWater(province) ? province : 0;
    }

    /// <summary>Keep authored city coordinates unless raster rounding puts them in
    /// water. Coastal correction is bounded to six cells, never a distant capital or
    /// the centre of a large province. Ownership always comes from current geography.</summary>
    public static MapAnchor AnchorOnLand(ProvinceTopology topology, float latitude, float longitude)
    {
        var province = ProvinceUnder(topology, latitude, longitude);
        if (province > 0 || topology is null || topology.Width < 1 || topology.Height < 1)
            return new(province, latitude, longitude);
        var px = (longitude + 180f) / 360f * topology.Width;
        var py = (90f - latitude) / 180f * topology.Height;
        var x = (int)MathF.Floor(px); var y = (int)MathF.Floor(py);
        var best = float.MaxValue; var result = new MapAnchor(0, latitude, longitude);
        var scale = MathF.Max(.05f, MathF.Cos(latitude * MathF.PI / 180f));
        for (var dy = -6; dy <= 6; dy++)
        for (var dx = -6; dx <= 6; dx++)
        {
            var cy = y + dy;
            if (cy < 0 || cy >= topology.Height) continue;
            var cx = ((x + dx) % topology.Width + topology.Width) % topology.Width;
            var candidate = topology.CellToProvince[cy * topology.Width + cx];
            if (candidate <= 0 || candidate > topology.ProvinceCount || topology.IsWater(candidate)) continue;
            var north = cy + .5f - py;
            var east = (x + dx + .5f - px) * scale;
            var distance = north * north + east * east;
            if (distance >= best) continue;
            best = distance;
            result = new(candidate, 90f - (cy + .5f) / topology.Height * 180f,
                (cx + .5f) / topology.Width * 360f - 180f);
        }
        return result;
    }

    /// <summary>
    /// The nearest cell to a point, no further than <paramref name="reach"/> cells off, whose
    /// province the caller accepts - "the country's own ground at the edge of this city" - or
    /// province 0 when there is none.
    /// </summary>
    public static MapAnchor AnchorNear(ProvinceTopology topology, float latitude, float longitude, int reach,
        Func<int, bool> accept)
    {
        var none = new MapAnchor(0, latitude, longitude);
        if (topology is null || topology.Width < 1 || topology.Height < 1 || accept is null) return none;
        var px = (longitude + 180f) / 360f * topology.Width;
        var py = (90f - latitude) / 180f * topology.Height;
        var x = (int)MathF.Floor(px); var y = (int)MathF.Floor(py);
        var best = float.MaxValue; var result = none;
        var scale = MathF.Max(.05f, MathF.Cos(latitude * MathF.PI / 180f));
        for (var dy = -reach; dy <= reach; dy++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            var cy = y + dy;
            if (cy < 0 || cy >= topology.Height) continue;
            var cx = ((x + dx) % topology.Width + topology.Width) % topology.Width;
            var candidate = topology.CellToProvince[cy * topology.Width + cx];
            if (candidate <= 0 || candidate > topology.ProvinceCount || topology.IsWater(candidate)) continue;
            var north = cy + .5f - py;
            var east = (x + dx + .5f - px) * scale;
            var distance = north * north + east * east;
            if (distance >= best || !accept(candidate)) continue;
            best = distance;
            result = new(candidate, 90f - (cy + .5f) / topology.Height * 180f,
                (cx + .5f) / topology.Width * 360f - 180f);
        }
        return result;
    }

    public static List<CityDefinition> Parse(IEnumerable<JsonDocument> documents) => Parse(documents, null, null);

    /// <summary>
    /// Lay one more cities file over the cities already read: a mod's, after the base game's and
    /// after any mod before it. The same rules as a later file of the base folder. Until this was
    /// called by anybody, only game/map_data/cities was ever read: a mod's cities file was handed
    /// to the region overrides, which found nothing in it, and a country that named a mod's city
    /// as its capital named a key no city had.
    ///
    /// The result is a new list, and everything that holds an index into the old one finds out by
    /// that (the province cache here, the pins on the globe).
    /// </summary>
    public static void Merge(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors?.Add("A cities file is one object: each city under its key.");
            return;
        }
        lock (Gate)
        {
            _all = Parse(new[] { document }, _all ??= Load(), errors);
            _indexById = null;
            _provinces = null;
            _firstInProvince = null;
        }
    }

    private static List<CityDefinition> Parse(IEnumerable<JsonDocument> documents, IReadOnlyList<CityDefinition> onto,
        List<string> errors)
    {
        // In the order first written: a later file that names a key again changes the city where
        // it already stands, and a new key goes to the end.
        var order = new List<CityDefinition>();
        var byId = new Dictionary<string, CityDefinition>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<CityDefinition>();
        // Copies, so whoever still holds the list being laid over holds it as it was.
        if (onto is not null)
            foreach (var before in onto)
            {
                var copy = new CityDefinition { Id = before.Id, Name = before.Name, Latitude = before.Latitude, Longitude = before.Longitude };
                byId[copy.Id] = copy;
                order.Add(copy);
            }
        foreach (var document in documents)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var value = entry.Value;
                if (!byId.TryGetValue(entry.Name, out var city))
                {
                    // A key nobody has, with no place given, is not a new city at the equator:
                    // it is a change to a city that is not there - one another mod removed, or a
                    // key from an older list. It adds nothing.
                    if (!value.TryGetProperty("latitude", out _) && !value.TryGetProperty("longitude", out _))
                    {
                        // Taking off a city that is already off is no mistake worth a line.
                        var takesOff = GameDataLoader.IsRemoved(value, $"map_data/cities:{entry.Name}") ||
                                       value.TryGetProperty("tier", out var low) && low.TryGetInt32(out var below) && below < 0;
                        if (!takesOff)
                            errors?.Add($"City '{entry.Name}' is not on the map and the file gives it no latitude and longitude, so it was not added.");
                        continue;
                    }
                    byId[entry.Name] = city = new CityDefinition { Id = entry.Name, Name = entry.Name };
                    order.Add(city);
                }
                if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    city.Name = name.GetString() ?? city.Name;
                if (value.TryGetProperty("latitude", out var latitude) && latitude.TryGetSingle(out var lat))
                    city.Latitude = Math.Clamp(lat, -90f, 90f);
                if (value.TryGetProperty("longitude", out var longitude) && longitude.TryGetSingle(out var lon))
                    city.Longitude = lon;
                // Files written before cities were made equal carry "population", "capital" and
                // "tier". None of them is read, except that a tier below zero was how a mod took a
                // city off the map, and a mod that says so still means it.
                if (value.TryGetProperty("tier", out var tier) && tier.TryGetInt32(out var rank))
                {
                    if (rank < 0) removed.Add(city); else removed.Remove(city);
                }
                if (GameDataLoader.TryReadRemoveFlag(value, out var gone, $"map_data/cities:{entry.Name}"))
                {
                    if (gone) removed.Add(city); else removed.Remove(city);
                }
                // "country" is not read either: a city belongs to the ground it stands on (RegionOf).
            }
        }
        if (removed.Count > 0) order.RemoveAll(removed.Contains);
        return order;
    }

    private static List<CityDefinition> Load() => Parse(GameDataLoader.DataDocuments("map_data", "cities"));

    /// <summary>The region a city stands in now, or null off the map.</summary>
    public static ScenarioState RegionOf(Campaign campaign, int cityIndex)
    {
        var map = campaign?.ProvinceMap;
        if (map is null) return null;
        var provinces = ProvincesOf(map);
        if (cityIndex < 0 || cityIndex >= provinces.Length || provinces[cityIndex] <= 0) return null;
        var mapIndex = map.ProvinceToState[provinces[cityIndex]];
        foreach (var state in campaign.States)
            if (state.MapIndex == mapIndex) return state;
        return null;
    }

    /// <summary>The cities of a region, in the order of the list.</summary>
    public static List<CityDefinition> CitiesOf(Campaign campaign, ScenarioState region)
    {
        var result = new List<CityDefinition>();
        var map = campaign?.ProvinceMap;
        if (map is null || region is null) return result;
        var cities = All;
        var provinces = ProvincesOf(map);
        for (var index = 0; index < cities.Count && index < provinces.Length; index++)
            if (provinces[index] > 0 && map.ProvinceToState[provinces[index]] == region.MapIndex)
                result.Add(cities[index]);
        return result;
    }
}

public sealed partial class ScenarioState
{
    /// <summary>
    /// Not used any more. Cities had head counts of their own once, and this was the region's
    /// urban population on the day those were true. It stays so that a save written then still
    /// loads; nothing reads it and nothing sets it.
    /// </summary>
    public long UrbanBaseline;
}

/// <summary>
/// A region's townspeople. Cities carry no people of their own: the people are the region's,
/// and its cities are where they live.
/// </summary>
public static class CityLife
{
    public static long Urban(ScenarioState region) =>
        region is null ? 0 : (long)(region.Population * (double)Math.Clamp(region.Urbanization, 0.02f, 1f));
}
