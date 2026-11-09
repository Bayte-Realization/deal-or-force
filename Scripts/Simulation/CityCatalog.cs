using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One city on the map: a name and a dot, and how important a place it is.</summary>
public sealed class CityDefinition
{
    public string Id = "";
    public string Name = "";
    public float Latitude;
    public float Longitude;
    /// <summary>The urban area's people on the scenario's first day (the source's figure). What it has now is CityCatalog.People.</summary>
    public long Population;
    /// <summary>0 capitals and world cities, seen from far out; 3 towns, seen only close in.</summary>
    public int Tier = 3;
    /// <summary>"national", "seat", "regional" or empty.</summary>
    public string Capital = "";

    public bool IsNationalCapital => Capital == "national";

    /// <summary>What the map calls it: city.&lt;id&gt;.name when the language has it, else the name.</summary>
    public string DisplayName => Loc.Has($"city.{Id}.name") ? Loc.Get($"city.{Id}.name") : Name;
}

/// <summary>
/// The cities drawn on the map (item 91), from game/map_data/cities.
///
/// Nothing about a city is simulated: it is geography, like a river. It exists so the map reads
/// like a map - Kharkiv and Donetsk where the war is, Kunming and Chengdu across a Chinese
/// province - and so a front has places to be named after. The region and province a city
/// stands in come from the map, never from the file, so it changes hands with its ground.
///
/// Every file in the folder is read in name order and later files win key by key: a mod adds
/// a city under a key of its own, moves or renames one by naming its key again, and removes
/// one with "tier": -1.
/// </summary>
public static class CityCatalog
{
    public readonly record struct MapAnchor(int ProvinceId, float Latitude, float Longitude);
    private static List<CityDefinition> _all;
    private static readonly object Gate = new();

    /// <summary>Every city, most important first: by tier, then by population.</summary>
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
            _provinces = null;
        }
    }

    private static int[] _provinces;
    private static ProvinceTopology _provincesOf;

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
            }
            return _provinces;
        }
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

    public static List<CityDefinition> Parse(IEnumerable<JsonDocument> documents)
    {
        var byId = new Dictionary<string, CityDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var value = entry.Value;
                if (!byId.TryGetValue(entry.Name, out var city))
                    byId[entry.Name] = city = new CityDefinition { Id = entry.Name, Name = entry.Name };
                if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    city.Name = name.GetString() ?? city.Name;
                if (value.TryGetProperty("latitude", out var latitude) && latitude.TryGetSingle(out var lat))
                    city.Latitude = Math.Clamp(lat, -90f, 90f);
                if (value.TryGetProperty("longitude", out var longitude) && longitude.TryGetSingle(out var lon))
                    city.Longitude = lon;
                if (value.TryGetProperty("population", out var population) && population.TryGetInt64(out var people))
                    city.Population = Math.Max(0, people);
                if (value.TryGetProperty("tier", out var tier) && tier.TryGetInt32(out var rank))
                    city.Tier = rank;
                if (value.TryGetProperty("capital", out var capital) && capital.ValueKind == JsonValueKind.String)
                    city.Capital = capital.GetString() ?? "";
                // "country" is not read: a city belongs to the ground it stands on (RegionOf).
            }
        }
        var cities = new List<CityDefinition>(byId.Count);
        foreach (var city in byId.Values)
            if (city.Tier >= 0) cities.Add(city);
        cities.Sort((a, b) =>
        {
            var byTier = a.Tier.CompareTo(b.Tier);
            if (byTier != 0) return byTier;
            var byCapital = b.IsNationalCapital.CompareTo(a.IsNationalCapital);
            if (byCapital != 0) return byCapital;
            var byPeople = b.Population.CompareTo(a.Population);
            return byPeople != 0 ? byPeople : string.CompareOrdinal(a.Id, b.Id);
        });
        return cities;
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

    /// <summary>
    /// A city's people now: its first-day figure, moved as its region's urban population has
    /// moved since (CityLife) - a region that grows grows its cities, a besieged and emptied one
    /// empties them.
    /// </summary>
    public static long People(ScenarioState region, CityDefinition city)
    {
        if (city is null) return 0;
        if (region is null || region.UrbanBaseline <= 0) return city.Population;
        var urban = CityLife.Urban(region);
        var ratio = Math.Clamp(urban / (double)region.UrbanBaseline, 0d, 8d);
        return (long)(city.Population * ratio);
    }

    /// <summary>The cities of a region, largest first, with the people each has now.</summary>
    public static List<(CityDefinition City, long People)> CitiesOf(Campaign campaign, ScenarioState region)
    {
        var result = new List<(CityDefinition, long)>();
        var map = campaign?.ProvinceMap;
        if (map is null || region is null) return result;
        var cities = All;
        var provinces = ProvincesOf(map);
        for (var index = 0; index < cities.Count && index < provinces.Length; index++)
            if (provinces[index] > 0 && map.ProvinceToState[provinces[index]] == region.MapIndex)
                result.Add((cities[index], People(region, cities[index])));
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }
}

public sealed partial class ScenarioState
{
    /// <summary>
    /// The region's urban population when its cities' figures were true - its first week, or the
    /// day it was carved out - against which they grow and shrink (CityLife). Saved.
    /// </summary>
    public long UrbanBaseline;
}

/// <summary>
/// The cities' people follow their region's. A city's figure is the source's for the scenario's
/// first day; the region's urban population that day is remembered once (UrbanBaseline), and a
/// city now has its figure times how far its region's urban population has moved since. Nothing
/// is simulated twice: the people are the region's, the cities are where they live.
/// </summary>
public static class CityLife
{
    public static long Urban(ScenarioState region) =>
        region is null ? 0 : (long)(region.Population * (double)Math.Clamp(region.Urbanization, 0.02f, 1f));

    /// <summary>Weekly, at the day's start: a region without its baseline yet takes today's.</summary>
    public static void Note(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays % 7 != 0) return;
        foreach (var region in campaign.States)
            if (region.UrbanBaseline <= 0 && region.Population > 0) region.UrbanBaseline = Math.Max(1, Urban(region));
    }
}
