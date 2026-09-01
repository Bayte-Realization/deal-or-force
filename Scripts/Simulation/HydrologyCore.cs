using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ByDealOrForce.Simulation;

public readonly record struct HydroPoint(float Longitude, float Latitude);

public sealed record RiverFeature(string Name, int Rank, HydroPoint[][] Parts);
public sealed record LakeFeature(string Name, int Rank, HydroPoint[][] Rings);

/// <summary>
/// Two-channel geographic raster shared by the renderer and province graph.
/// R contains lake coverage; G contains river importance. Neither layer changes
/// political ownership, so hydrography cannot accidentally carve countries apart.
/// </summary>
public sealed record HydrologyRaster(int Width, int Height, byte[] Pixels, int RiverCount, int LakeCount, string Source = "GeoJSON vectors");

/// <summary>
/// Paradox-style, data-driven physical map catalogue. The shipped Natural Earth
/// GeoJSON can be replaced or extended by mods without recompiling the simulation.
/// </summary>
public static class HydrologyCatalog
{
    private static readonly List<RiverFeature> Rivers = new();
    private static readonly List<LakeFeature> Lakes = new();

    public static IReadOnlyList<RiverFeature> RiverFeatures => Rivers;
    public static IReadOnlyList<LakeFeature> LakeFeatures => Lakes;

    static HydrologyCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Rivers.Clear();
        Lakes.Clear();
        LoadBase("rivers.json");
        LoadBase("lakes.json");
    }

    private static void LoadBase(string name)
    {
        var file = GameDataLoader.FindDataFile("map_data", "hydrography", name);
        if (file is null) return;
        try { LoadGeoJson(File.ReadAllText(file), null); }
        catch { /* Missing optional physical data must not prevent campaign boot. */ }
    }

    public static bool LooksLikeGeoJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.GetString() == "FeatureCollection";
        }
        catch { return false; }
    }

    public static void LoadGeoJson(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Hydrology GeoJSON has no feature collection.");

        foreach (var feature in features.EnumerateArray())
        {
            try
            {
                var properties = feature.GetProperty("properties");
                var geometry = feature.GetProperty("geometry");
                var type = geometry.GetProperty("type").GetString() ?? "";
                var name = properties.TryGetProperty("name_en", out var english) && english.ValueKind == JsonValueKind.String
                    ? english.GetString() ?? ""
                    : properties.TryGetProperty("name", out var local) && local.ValueKind == JsonValueKind.String
                        ? local.GetString() ?? "" : "";
                var rank = properties.TryGetProperty("scalerank", out var scale) && scale.TryGetInt32(out var number)
                    ? Math.Clamp(number, 0, 9) : 6;
                var coordinates = geometry.GetProperty("coordinates");
                if (type == "MultiLineString")
                {
                    var parts = ParseLines(coordinates);
                    if (parts.Length > 0) Rivers.Add(new RiverFeature(name, rank, parts));
                }
                else if (type == "LineString")
                {
                    var line = ParseLine(coordinates);
                    if (line.Length > 1) Rivers.Add(new RiverFeature(name, rank, new[] { line }));
                }
                else if (type == "Polygon")
                {
                    var rings = ParseLines(coordinates);
                    if (rings.Length > 0) Lakes.Add(new LakeFeature(name, rank, rings));
                }
                else if (type == "MultiPolygon")
                {
                    foreach (var polygon in coordinates.EnumerateArray())
                    {
                        var rings = ParseLines(polygon);
                        if (rings.Length > 0) Lakes.Add(new LakeFeature(name, rank, rings));
                    }
                }
            }
            catch (Exception exception) { errors?.Add($"Invalid hydrology feature: {exception.Message}"); }
        }
    }

    public static HydrologyRaster BuildRaster(int width, int height)
    {
        if (MapRasterCatalog.TryLoadHydrology(width, height, out var authored, out _)) return authored;
        return BuildVectorRaster(width, height);
    }

    public static HydrologyRaster BuildVectorRaster(int width, int height)
    {
        var pixels = new byte[width * height * 2];
        foreach (var lake in Lakes)
        {
            if (lake.Rank > 6 || lake.Rings.Length == 0) continue;
            FillLake(pixels, width, height, lake.Rings[0]);
        }
        foreach (var river in Rivers)
        {
            if (river.Rank > 6) continue;
            var intensity = (byte)Math.Clamp(225 - river.Rank * 22, 72, 225);
            var radius = river.Rank <= 1 ? 2 : river.Rank <= 3 ? 1 : 0;
            foreach (var line in river.Parts)
                for (var index = 1; index < line.Length; index++)
                    DrawRiver(pixels, width, height, line[index - 1], line[index], intensity, radius);
        }
        return new HydrologyRaster(width, height, pixels, Rivers.Count, Lakes.Count);
    }

    private static HydroPoint[][] ParseLines(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<HydroPoint[]>();
        var result = new List<HydroPoint[]>();
        foreach (var line in value.EnumerateArray())
        {
            var parsed = ParseLine(line);
            if (parsed.Length > 1) result.Add(parsed);
        }
        return result.ToArray();
    }

    private static HydroPoint[] ParseLine(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<HydroPoint>();
        var result = new List<HydroPoint>();
        foreach (var point in value.EnumerateArray())
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2 &&
                point[0].TryGetSingle(out var longitude) && point[1].TryGetSingle(out var latitude))
                result.Add(new HydroPoint(longitude, latitude));
        return result.ToArray();
    }

    private static void FillLake(byte[] pixels, int width, int height, IReadOnlyList<HydroPoint> ring)
    {
        if (ring.Count < 3) return;
        var minLon = float.MaxValue;
        var maxLon = float.MinValue;
        var minLat = float.MaxValue;
        var maxLat = float.MinValue;
        foreach (var point in ring)
        {
            minLon = Math.Min(minLon, point.Longitude);
            maxLon = Math.Max(maxLon, point.Longitude);
            minLat = Math.Min(minLat, point.Latitude);
            maxLat = Math.Max(maxLat, point.Latitude);
        }
        var minX = Math.Clamp((int)MathF.Floor((minLon + 180f) / 360f * width), 0, width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling((maxLon + 180f) / 360f * width), 0, width - 1);
        var minY = Math.Clamp((int)MathF.Floor((90f - maxLat) / 180f * height), 0, height - 1);
        var maxY = Math.Clamp((int)MathF.Ceiling((90f - minLat) / 180f * height), 0, height - 1);
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var longitude = -180f + (x + 0.5f) * 360f / width;
            var latitude = 90f - (y + 0.5f) * 180f / height;
            if (Contains(ring, longitude, latitude)) pixels[(y * width + x) * 2] = 255;
        }
    }

    private static bool Contains(IReadOnlyList<HydroPoint> polygon, float longitude, float latitude)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Latitude > latitude) == (b.Latitude > latitude)) continue;
            var crossing = (b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                (b.Latitude - a.Latitude) + a.Longitude;
            if (longitude < crossing) inside = !inside;
        }
        return inside;
    }

    private static void DrawRiver(byte[] pixels, int width, int height,
        HydroPoint start, HydroPoint end, byte intensity, int radius)
    {
        var x0 = (int)MathF.Round((start.Longitude + 180f) / 360f * (width - 1));
        var y0 = (int)MathF.Round((90f - start.Latitude) / 180f * (height - 1));
        var x1 = (int)MathF.Round((end.Longitude + 180f) / 360f * (width - 1));
        var y1 = (int)MathF.Round((90f - end.Latitude) / 180f * (height - 1));
        if (Math.Abs(x1 - x0) > width / 2)
        {
            if (x0 < x1) x0 += width;
            else x1 += width;
        }
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            for (var oy = -radius; oy <= radius; oy++)
            for (var ox = -radius; ox <= radius; ox++)
            {
                var x = ((x0 + ox) % width + width) % width;
                var y = Math.Clamp(y0 + oy, 0, height - 1);
                var write = (y * width + x) * 2 + 1;
                if (pixels[write] < intensity) pixels[write] = intensity;
            }
            if (x0 == x1 && y0 == y1) break;
            var doubled = error * 2;
            if (doubled >= dy) { error += dy; x0 += sx; }
            if (doubled <= dx) { error += dx; y0 += sy; }
        }
    }
}
