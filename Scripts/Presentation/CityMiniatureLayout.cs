using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DealOrForce.Simulation;

namespace DealOrForce.Presentation;

public enum CityMiniatureKind : byte
{
    CapitalMonument = 0,
    CityCluster = 1,
    PortHarbor = 2
}

/// <summary>
/// Pure-C# loader and worker-thread 8x4 spatial chunk builder for the Blender 5.3 3D PBR
/// city, capital and port miniatures. Keeps zero Godot scene dependencies so headless tests
/// can validate triangle budgets, material roles, land-only footprints and chunking.
/// </summary>
public static class CityMiniatureLayout
{
    public const int LonBins = 8;
    public const int LatBins = 4;
    public const int ChunkCount = LonBins * LatBins;
    public const int MaxTotalInstances = 960;
    public const long MinimumUrbanPopulation = 120_000;

    public static readonly string[] ModelNames =
    {
        "capital_monument",
        "city_cluster",
        "port_harbor"
    };

    public readonly record struct ObjMeshData(
        string Name,
        float[] Positions,
        float[] Normals,
        float[] ColorsRgba,
        int TriangleCount,
        bool HasStone,
        bool HasRoof,
        bool HasWindowGlow);

    public readonly record struct Placement(
        int CityIndex,
        int ChunkIndex,
        CityMiniatureKind Kind,
        float Latitude,
        float Longitude,
        float FootprintDeg,
        float LongitudeScale,
        float BaseHeight,
        float BearingRad,
        float Shade);

    public static int ChunkIndexOf(float latitude, float longitude)
    {
        var wrappedLon = ((longitude + 180f) % 360f + 360f) % 360f;
        var lonBin = Math.Clamp((int)(wrappedLon / (360f / LonBins)), 0, LonBins - 1);
        var clampedLat = Math.Clamp(latitude + 90f, 0f, 179.999f);
        var latBin = Math.Clamp((int)(clampedLat / (180f / LatBins)), 0, LatBins - 1);
        return latBin * LonBins + lonBin;
    }

    public static (float CenterLat, float CenterLon) ChunkCenterDegrees(int chunkIndex)
    {
        var latBin = Math.Clamp(chunkIndex / LonBins, 0, LatBins - 1);
        var lonBin = Math.Clamp(chunkIndex % LonBins, 0, LonBins - 1);
        var lat = -90f + (latBin + 0.5f) * (180f / LatBins);
        var lon = -180f + (lonBin + 0.5f) * (360f / LonBins);
        return (lat, lon);
    }

    public static float Visibility(bool flat, float distance, float flatZoom)
    {
        var value = flat ? flatZoom : distance;
        if (!float.IsFinite(value)) return 0f;
        var t = Math.Clamp(flat ? (2.85f - value) / 1.45f : (8.80f - value) / 2.40f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static ObjMeshData ParseObj(string name, string objText)
    {
        var rawVerts = new List<(float X, float Y, float Z)>(256);
        var rawNorms = new List<(float X, float Y, float Z)>(256);
        var positions = new List<float>(768);
        var normals = new List<float>(768);
        var colors = new List<float>(1024);
        var currentMat = "stone_warm";
        bool hasStone = false, hasRoof = false, hasWindow = false;

        foreach (var rawLine in (objText ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                    rawVerts.Add((ParseF(p[1]), ParseF(p[2]), ParseF(p[3])));
            }
            else if (line.StartsWith("vn ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                    rawNorms.Add((ParseF(p[1]), ParseF(p[2]), ParseF(p[3])));
            }
            else if (line.StartsWith("usemtl ", StringComparison.Ordinal))
            {
                currentMat = line.Substring(7).Trim();
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                var (r, g, b, glow, isStone, isRoof) = MaterialColor(currentMat);
                hasStone |= isStone;
                hasRoof |= isRoof;
                hasWindow |= glow > 0.5f;
                for (var t = 2; t < p.Length - 1; t++)
                {
                    EmitCorner(p[1], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                    EmitCorner(p[t], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                    EmitCorner(p[t + 1], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                }
            }
        }

        return new ObjMeshData(
            name,
            positions.ToArray(),
            normals.ToArray(),
            colors.ToArray(),
            positions.Count / 9,
            hasStone,
            hasRoof,
            hasWindow);
    }

    public static string ModelsDirectory() =>
        Path.Combine(GameDataLoader.DataFolder(), "gfx", "models", "cities");

    public static Placement[] BuildPlacements(
        IReadOnlyList<CityDefinition> cities,
        CityCatalog.MapAnchor[] anchors,
        bool[] isCapital,
        Dictionary<int, long> stateUrbanPop,
        ProvinceTopology map,
        HashSet<int> destroyedProvinces)
    {
        if (cities is null || anchors is null || map is null || cities.Count == 0 || anchors.Length != cities.Count)
            return Array.Empty<Placement>();

        var citiesPerState = new Dictionary<int, int>(256);
        for (var i = 0; i < anchors.Length; i++)
        {
            var pid = anchors[i].ProvinceId;
            if (pid <= 0 || pid > map.ProvinceCount) continue;
            var st = map.ProvinceToState[pid];
            citiesPerState[st] = citiesPerState.GetValueOrDefault(st) + 1;
        }

        var candidates = new List<(long Score, Placement Item)>(512);
        for (var i = 0; i < cities.Count; i++)
        {
            var anchor = anchors[i];
            var pid = anchor.ProvinceId;
            if (pid <= 0 || pid > map.ProvinceCount) continue;
            if (destroyedProvinces is not null && destroyedProvinces.Contains(pid)) continue;

            var cap = isCapital is not null && i < isCapital.Length && isCapital[i];
            var stIdx = map.ProvinceToState[pid];
            var sharing = Math.Max(1, citiesPerState.GetValueOrDefault(stIdx, 1));
            var people = stateUrbanPop is not null && stateUrbanPop.TryGetValue(stIdx, out var urban)
                ? urban / sharing
                : 0L;
            if (!cap && people < MinimumUrbanPopulation) continue;

            var seed = HashCity(cities[i].Id);
            var popFactor = Math.Clamp((float)Math.Log10(Math.Max(50_000, people)) - 4.7f, 0f, 2.6f);
            var footprintDeg = (cap ? 0.135f : 0.095f) + popFactor * 0.026f;
            var lonScale = Math.Max(0.14f, MathF.Cos(anchor.Latitude * (MathF.PI / 180f)));

            if (!FootprintOnLand(map, destroyedProvinces, anchor.Latitude, anchor.Longitude, footprintDeg, lonScale))
                continue;

            var kind = cap
                ? CityMiniatureKind.CapitalMonument
                : NearSeaCoast(map, anchor.Latitude, anchor.Longitude, lonScale)
                    ? CityMiniatureKind.PortHarbor
                    : CityMiniatureKind.CityCluster;

            var baseHeight = (cap ? 0.0115f : 0.0078f) + popFactor * 0.0022f;
            var bearing = (seed & 1023u) * (MathF.Tau / 1024f);
            var shade = 0.88f + ((seed >> 10) & 15u) * 0.012f;
            var chunk = ChunkIndexOf(anchor.Latitude, anchor.Longitude);
            var score = (cap ? 1_000_000_000L : 0L) + Math.Clamp(people, 0L, 500_000_000L);

            candidates.Add((score, new Placement(
                i, chunk, kind, anchor.Latitude, anchor.Longitude,
                footprintDeg, lonScale, baseHeight, bearing, shade)));
        }

        candidates.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Item.CityIndex.CompareTo(b.Item.CityIndex);
        });

        var take = Math.Min(candidates.Count, MaxTotalInstances);
        var result = new Placement[take];
        for (var i = 0; i < take; i++) result[i] = candidates[i].Item;
        return result;
    }

    public static bool FootprintOnLand(
        ProvinceTopology map, HashSet<int> destroyed, float lat, float lon, float footprintDeg, float lonScale)
    {
        lon = WrapLon(lon);
        var centerProv = CityCatalog.ProvinceUnder(map, lat, lon);
        if (centerProv <= 0 || (destroyed is not null && destroyed.Contains(centerProv))) return false;
        var half = footprintDeg * 0.42f;
        for (var c = 0; c < 4; c++)
        {
            var clat = Math.Clamp(lat + ((c & 1) == 0 ? -half : half), -89.8f, 89.8f);
            var clon = WrapLon(lon + ((c & 2) == 0 ? -half : half) / lonScale);
            var cp = CityCatalog.ProvinceUnder(map, clat, clon);
            if (cp <= 0 || (destroyed is not null && destroyed.Contains(cp))) return false;
        }
        return true;
    }

    private static bool NearSeaCoast(ProvinceTopology map, float lat, float lon, float lonScale)
    {
        const float probeDeg = 0.22f;
        for (var dir = 0; dir < 8; dir++)
        {
            var angle = dir * (MathF.PI * 0.25f);
            var plat = Math.Clamp(lat + MathF.Sin(angle) * probeDeg, -89.8f, 89.8f);
            var plon = WrapLon(lon + MathF.Cos(angle) * probeDeg / lonScale);
            if (CityCatalog.ProvinceUnder(map, plat, plon) == 0) return true;
        }
        return false;
    }

    private static float WrapLon(float lon)
    {
        var w = (lon + 180f) % 360f;
        if (w < 0f) w += 360f;
        return w - 180f;
    }

    private static uint HashCity(string id)
    {
        var h = 2166136261u;
        if (id is null) return h;
        for (var i = 0; i < id.Length; i++) h = unchecked((h ^ id[i]) * 16777619u);
        return h;
    }

    private static float ParseF(string token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static (float R, float G, float B, float Glow, bool Stone, bool Roof) MaterialColor(string mat) =>
        mat switch
        {
            "stone_light" => (0.87f, 0.86f, 0.84f, 0f, true, false),
            "stone_warm" => (0.77f, 0.76f, 0.74f, 0f, true, false),
            "stone_cool" => (0.66f, 0.67f, 0.68f, 0f, true, false),
            "terracotta" => (0.68f, 0.38f, 0.30f, 0f, false, true),
            "slate_roof" => (0.30f, 0.34f, 0.39f, 0f, false, true),
            "brass_dome" => (0.58f, 0.60f, 0.61f, 0f, false, true),
            "window_amber" => (0.94f, 0.93f, 0.89f, 1f, false, false),
            _ => (0.78f, 0.77f, 0.75f, 0f, true, false)
        };

    private static void EmitCorner(
        string spec,
        List<(float X, float Y, float Z)> rawVerts,
        List<(float X, float Y, float Z)> rawNorms,
        List<float> positions,
        List<float> normals,
        List<float> colors,
        float r, float g, float b, float glow)
    {
        var slash = spec.IndexOf('/');
        var vIdxStr = slash >= 0 ? spec.Substring(0, slash) : spec;
        var nSlash = spec.LastIndexOf('/');
        var nIdxStr = nSlash > 0 && nSlash + 1 < spec.Length ? spec.Substring(nSlash + 1) : "";

        var vi = int.TryParse(vIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedV)
            ? Math.Clamp(parsedV - 1, 0, Math.Max(0, rawVerts.Count - 1))
            : 0;
        var ni = int.TryParse(nIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedN)
            ? Math.Clamp(parsedN - 1, 0, Math.Max(0, rawNorms.Count - 1))
            : -1;

        (float X, float Y, float Z) v = rawVerts.Count > 0 ? rawVerts[vi] : (0f, 0f, 0f);
        (float X, float Y, float Z) n = ni >= 0 && ni < rawNorms.Count ? rawNorms[ni] : (0f, 0f, 1f);
        positions.Add(v.X); positions.Add(v.Y); positions.Add(v.Z);
        normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
        colors.Add(r); colors.Add(g); colors.Add(b); colors.Add(glow);
    }
}
