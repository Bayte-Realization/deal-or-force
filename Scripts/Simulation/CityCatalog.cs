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
    public long Population;
    /// <summary>0 capitals and world cities, seen from far out; 3 towns, seen only close in.</summary>
    public int Tier = 3;
    /// <summary>"national", "seat", "regional" or empty.</summary>
    public string Capital = "";
    /// <summary>The ISO code the source gives it - information, not ownership.</summary>
    public string Country = "";

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
        int[] provinces;
        lock (Gate)
        {
            if (_provinces is null || !ReferenceEquals(_provincesOf, topology) || _provinces.Length != cities.Count)
            {
                _provinces = new int[cities.Count];
                for (var index = 0; index < cities.Count; index++)
                    _provinces[index] = ProvinceUnder(topology, cities[index].Latitude, cities[index].Longitude);
                _provincesOf = topology;
            }
            provinces = _provinces;
        }
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

    /// <summary>The province under a point of the globe, or 0 for none.</summary>
    private static int ProvinceUnder(ProvinceTopology topology, float latitude, float longitude)
    {
        if (topology.Width <= 0 || topology.Height <= 0) return 0;
        var x = (int)MathF.Floor((longitude + 180f) / 360f * topology.Width);
        var y = (int)MathF.Floor((90f - latitude) / 180f * topology.Height);
        x = ((x % topology.Width) + topology.Width) % topology.Width;
        y = Math.Clamp(y, 0, topology.Height - 1);
        var province = topology.CellToProvince[y * topology.Width + x];
        return province > 0 && province <= topology.ProvinceCount && !topology.IsWater(province) ? province : 0;
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
                if (value.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.String)
                    city.Country = country.GetString() ?? "";
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
}
