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
    public const int MaxInstancesPerChunk = 96;
    public const int MaxTotalInstances = 2400;
    public const long MinimumUrbanPopulation = 120_000;
    private const float MinSeparationDeg = 0.22f;

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
                    // Godot ArrayMesh + cull_back winds front faces clockwise (CW), whereas Wavefront
                    // OBJ exports anticlockwise (CCW: dot(cross(v1-v0, v2-v0), vn) > 0). Swap corners
                    // whenever CCW relative to outward normal so 100% of triangles wind CW outward.
                    var (v0, n0) = ReadCorner(p[1], rawVerts, rawNorms);
                    var (v1, _) = ReadCorner(p[t], rawVerts, rawNorms);
                    var (v2, _) = ReadCorner(p[t + 1], rawVerts, rawNorms);
                    var e1x = v1.X - v0.X; var e1y = v1.Y - v0.Y; var e1z = v1.Z - v0.Z;
                    var e2x = v2.X - v0.X; var e2y = v2.Y - v0.Y; var e2z = v2.Z - v0.Z;
                    var cx = e1y * e2z - e1z * e2y;
                    var cy = e1z * e2x - e1x * e2z;
                    var cz = e1x * e2y - e1y * e2x;
                    var dot = cx * n0.X + cy * n0.Y + cz * n0.Z;
                    EmitCorner(p[1], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                    if (dot > 0f)
                    {
                        EmitCorner(p[t + 1], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                        EmitCorner(p[t], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                    }
                    else
                    {
                        EmitCorner(p[t], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                        EmitCorner(p[t + 1], rawVerts, rawNorms, positions, normals, colors, r, g, b, glow);
                    }
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

        var seenInState = new Dictionary<int, int>(citiesPerState.Count);
        var perChunkCandidates = new List<(long Score, Placement Item)>[ChunkCount];
        for (var c = 0; c < ChunkCount; c++) perChunkCandidates[c] = new List<(long, Placement)>(64);

        for (var i = 0; i < cities.Count; i++)
        {
            var anchor = anchors[i];
            var pid = anchor.ProvinceId;
            if (pid <= 0 || pid > map.ProvinceCount) continue;
            if (destroyedProvinces is not null && destroyedProvinces.Contains(pid)) continue;

            var stIdx = map.ProvinceToState[pid];
            var rankInState = seenInState.GetValueOrDefault(stIdx, 0);
            seenInState[stIdx] = rankInState + 1;

            var cap = isCapital is not null && i < isCapital.Length && isCapital[i];
            var sharing = Math.Max(1, citiesPerState.GetValueOrDefault(stIdx, 1));
            // CityCatalog orders cities by regional prominence: distribute regional urban population
            // by Zipf rank 1 / ((r + 1) * H_N) so primary cities are not diluted by small towns.
            var people = stateUrbanPop is not null && stateUrbanPop.TryGetValue(stIdx, out var urban)
                ? (long)(urban / ((rankInState + 1.0) * HarmonicSum(sharing)))
                : 0L;
            if (!cap && people < MinimumUrbanPopulation) continue;

            var seed = HashCity(cities[i].Id);
            var popFactor = Math.Clamp((float)Math.Log10(Math.Max(50_000, people)) - 4.7f, 0f, 2.6f);
            var footprintDeg = (cap ? 0.145f : 0.112f) + popFactor * 0.028f;
            var lonScale = Math.Max(0.14f, MathF.Cos(anchor.Latitude * (MathF.PI / 180f)));

            var placeLat = anchor.Latitude;
            var placeLon = anchor.Longitude;
            if (!FootprintOnLand(map, destroyedProvinces, placeLat, placeLon, footprintDeg, lonScale) &&
                !TryNudgeOntoShore(map, destroyedProvinces, pid, ref placeLat, ref placeLon, footprintDeg, lonScale))
                continue;

            var coastal = NearSeaCoast(map, anchor.Latitude, anchor.Longitude, lonScale, out var seaBearing) ||
                          NearSeaCoast(map, placeLat, placeLon, lonScale, out seaBearing);
            var kind = cap
                ? CityMiniatureKind.CapitalMonument
                : coastal
                    ? CityMiniatureKind.PortHarbor
                    : CityMiniatureKind.CityCluster;

            var baseHeight = (cap ? 0.0120f : 0.0088f) + popFactor * 0.0024f;
            var bearing = kind == CityMiniatureKind.PortHarbor
                ? seaBearing + MathF.PI * 0.5f
                : (seed & 1023u) * (MathF.Tau / 1024f);
            var shade = 0.88f + ((seed >> 10) & 15u) * 0.012f;
            var chunk = ChunkIndexOf(placeLat, placeLon);
            var score = (cap ? 1_000_000_000L : 0L) + Math.Clamp(people, 0L, 500_000_000L);

            perChunkCandidates[chunk].Add((score, new Placement(
                i, chunk, kind, placeLat, placeLon,
                footprintDeg, lonScale, baseHeight, bearing, shade)));
        }

        var selected = new List<(long Score, Placement Item)>(1024);
        for (var c = 0; c < ChunkCount; c++)
        {
            var bucket = perChunkCandidates[c];
            if (bucket.Count == 0) continue;
            bucket.Sort(static (a, b) =>
            {
                var cmp = b.Score.CompareTo(a.Score);
                return cmp != 0 ? cmp : a.Item.CityIndex.CompareTo(b.Item.CityIndex);
            });
            var accepted = new List<Placement>(Math.Min(bucket.Count, MaxInstancesPerChunk));
            for (var i = 0; i < bucket.Count && accepted.Count < MaxInstancesPerChunk; i++)
            {
                var cand = bucket[i].Item;
                var isCap = cand.Kind == CityMiniatureKind.CapitalMonument;
                if (!isCap && OverlapsAccepted(accepted, cand)) continue;
                accepted.Add(cand);
                selected.Add(bucket[i]);
            }
        }

        selected.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Item.CityIndex.CompareTo(b.Item.CityIndex);
        });

        var take = Math.Min(selected.Count, MaxTotalInstances);
        var result = new Placement[take];
        for (var i = 0; i < take; i++) result[i] = selected[i].Item;
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

    private static bool TryNudgeOntoShore(
        ProvinceTopology map, HashSet<int> destroyed, int expectedProvince,
        ref float lat, ref float lon, float footprintDeg, float lonScale)
    {
        for (var ring = 1; ring <= 3; ring++)
        {
            var step = footprintDeg * (0.38f * ring);
            for (var dir = 0; dir < 8; dir++)
            {
                var angle = dir * (MathF.PI * 0.25f);
                var nlat = Math.Clamp(lat + MathF.Sin(angle) * step, -89.7f, 89.7f);
                var nlon = WrapLon(lon + MathF.Cos(angle) * step / lonScale);
                if (CityCatalog.ProvinceUnder(map, nlat, nlon) != expectedProvince && ring == 1) continue;
                if (FootprintOnLand(map, destroyed, nlat, nlon, footprintDeg, lonScale))
                {
                    lat = nlat;
                    lon = nlon;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool NearSeaCoast(ProvinceTopology map, float lat, float lon, float lonScale, out float seaAngle)
    {
        foreach (var probeDeg in new[] { 0.18f, 0.30f })
        {
            for (var dir = 0; dir < 16; dir++)
            {
                var angle = dir * (MathF.Tau / 16f);
                var plat = Math.Clamp(lat + MathF.Sin(angle) * probeDeg, -89.8f, 89.8f);
                var plon = WrapLon(lon + MathF.Cos(angle) * probeDeg / lonScale);
                if (CityCatalog.ProvinceUnder(map, plat, plon) == 0)
                {
                    seaAngle = angle;
                    return true;
                }
            }
        }
        seaAngle = 0f;
        return false;
    }

    private static double HarmonicSum(int n)
    {
        var sum = 0.0;
        var count = Math.Max(1, n);
        for (var k = 1; k <= count; k++) sum += 1.0 / k;
        return sum;
    }

    private static bool OverlapsAccepted(List<Placement> accepted, in Placement candidate)
    {
        var minSq = MinSeparationDeg * MinSeparationDeg;
        for (var i = 0; i < accepted.Count; i++)
        {
            var prev = accepted[i];
            var dLat = candidate.Latitude - prev.Latitude;
            var dLon = (candidate.Longitude - prev.Longitude) * candidate.LongitudeScale;
            if (dLat * dLat + dLon * dLon < minSq) return true;
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

    /// <summary>
    /// Computes the three column vectors (Col0 = rotated East, Col1 = rotated North, Col2 = outward
    /// sphere Normal) and unit sphere normal for Godot's column-vector Basis(c0, c1, c2) constructor.
    /// </summary>
    public static ((float X, float Y, float Z) Col0, (float X, float Y, float Z) Col1, (float X, float Y, float Z) Col2, (float X, float Y, float Z) Normal)
        GlobeBasisColumns(float latitude, float longitude, float bearingRad, float footprintDeg, float globeRadius = 5f)
    {
        var latRad = latitude * (MathF.PI / 180f);
        var lonRad = (longitude + 180f) * (MathF.PI / 180f);
        var cosLat = MathF.Cos(latRad);
        var sinLat = MathF.Sin(latRad);
        var cosLon = MathF.Cos(lonRad);
        var sinLon = MathF.Sin(lonRad);

        var nx = cosLat * sinLon;
        var ny = sinLat;
        var nz = cosLat * cosLon;

        var ex = cosLon;
        var ey = 0f;
        var ez = -sinLon;

        var nox = -sinLat * sinLon;
        var noy = cosLat;
        var noz = -sinLat * cosLon;

        var cb = MathF.Cos(bearingRad);
        var sb = MathF.Sin(bearingRad);
        var w = footprintDeg * (globeRadius * MathF.PI / 180f) * 1.35f;
        var h = w * 1.15f;

        var c0 = ((ex * cb + nox * sb) * w, (ey * cb + noy * sb) * w, (ez * cb + noz * sb) * w);
        var c1 = ((-ex * sb + nox * cb) * w, (-ey * sb + noy * cb) * w, (-ez * sb + noz * cb) * w);
        var c2 = (nx * h, ny * h, nz * h);
        return (c0, c1, c2, (nx, ny, nz));
    }

    private static ((float X, float Y, float Z) V, (float X, float Y, float Z) N) ReadCorner(
        string spec,
        List<(float X, float Y, float Z)> rawVerts,
        List<(float X, float Y, float Z)> rawNorms)
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
        return (v, n);
    }

    private static void EmitCorner(
        string spec,
        List<(float X, float Y, float Z)> rawVerts,
        List<(float X, float Y, float Z)> rawNorms,
        List<float> positions,
        List<float> normals,
        List<float> colors,
        float r, float g, float b, float glow)
    {
        var (v, n) = ReadCorner(spec, rawVerts, rawNorms);
        positions.Add(v.X); positions.Add(v.Y); positions.Add(v.Z);
        normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
        colors.Add(r); colors.Add(g); colors.Add(b); colors.Add(glow);
    }
}
