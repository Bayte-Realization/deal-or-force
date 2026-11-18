using System;
using System.Collections.Generic;
using System.Text;
using DealOrForce.Simulation;

namespace DealOrForce;

public readonly record struct RiverLabelAnchor(
    string Key,
    string DefaultName,
    int Rank,
    float Latitude,
    float Longitude,
    float AxisX,
    float AxisY,
    float SpanDegrees)
{
    public string DisplayName => Loc.Has($"river.{Key}") ? Loc.Get($"river.{Key}") : DefaultName;
}

/// <summary>
/// Worker-thread preparation of major named river label anchors from <see cref="HydrologyCatalog.RiverFeatures"/>.
/// Deduplicates multi-segment Natural Earth reaches under one canonical name and locates each river's
/// arc-length midpoint and upright reading tangent so map labels never duplicate or sit upside down.
/// </summary>
public static class RiverLabelLayout
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Rhein"] = "Rhine",
        ["Dnepre"] = "Dnieper",
        ["Dicle"] = "Tigris",
        ["Firat"] = "Euphrates",
        ["Al Furat"] = "Euphrates",
        ["Bénoué"] = "Benue",
        ["Ertis"] = "Irtysh",
        ["Selenge (Selenga)"] = "Selenga",
        ["Huang"] = "Yellow",
        ["Abay"] = "Blue Nile",
        ["Damqogkanbab"] = "Brahmaputra",
        ["Dihang"] = "Brahmaputra",
        ["Shiquan"] = "Indus",
        ["Tongtian"] = "Yangtze",
        ["Tuotuo"] = "Yangtze",
        ["Lualaba"] = "Congo",
        ["São  Francisco"] = "São Francisco"
    };

    private static readonly string[] SkipSubstrings =
    {
        "Branch", "Protoka", "Bratul", "Delta", "Canal", "Borcea"
    };

    public static string CanonicalName(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    public static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var normalized = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var underscore = false;
        foreach (var c in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                underscore = false;
            }
            else if (!underscore && builder.Length > 0)
            {
                builder.Append('_');
                underscore = true;
            }
        }
        return builder.ToString().Trim('_');
    }

    public static IReadOnlyList<RiverLabelAnchor> BuildAnchors(
        IReadOnlyList<RiverFeature> features, int maxRank = 4, float minSpanDegrees = 1.2f)
    {
        if (features is null || features.Count == 0) return Array.Empty<RiverLabelAnchor>();

        var best = new Dictionary<string, RiverLabelAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
        {
            if (feature.Rank > maxRank || string.IsNullOrWhiteSpace(feature.Name) || feature.Parts is null) continue;
            if (SkipDelta(feature.Name)) continue;

            var canonical = CanonicalName(feature.Name);
            var key = Slug(canonical);
            if (key.Length == 0) continue;

            foreach (var part in feature.Parts)
            {
                if (part is null || part.Length < 2) continue;
                var total = PolylineLengthDeg(part);
                if (total < minSpanDegrees) continue;
                var (lat, lon, axisX, axisY) = MidpointAndTangent(part, total);
                var candidate = new RiverLabelAnchor(key, canonical, feature.Rank, lat, lon, axisX, axisY, total);
                if (!best.TryGetValue(key, out var existing) ||
                    candidate.Rank < existing.Rank ||
                    (candidate.Rank == existing.Rank && candidate.SpanDegrees > existing.SpanDegrees))
                    best[key] = candidate;
            }
        }

        var result = new List<RiverLabelAnchor>(best.Values);
        result.Sort((a, b) =>
        {
            var rankCmp = a.Rank.CompareTo(b.Rank);
            if (rankCmp != 0) return rankCmp;
            var spanCmp = b.SpanDegrees.CompareTo(a.SpanDegrees);
            if (spanCmp != 0) return spanCmp;
            return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });
        return result;
    }

    private static bool SkipDelta(string name)
    {
        foreach (var token in SkipSubstrings)
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static float PolylineLengthDeg(HydroPoint[] part)
    {
        var total = 0f;
        for (var i = 1; i < part.Length; i++)
        {
            var dLon = DeltaLon(part[i - 1].Longitude, part[i].Longitude);
            var dLat = part[i].Latitude - part[i - 1].Latitude;
            total += MathF.Sqrt(dLon * dLon + dLat * dLat);
        }
        return total;
    }

    private static (float Lat, float Lon, float AxisX, float AxisY) MidpointAndTangent(HydroPoint[] part, float totalLength)
    {
        var target = totalLength * 0.5f;
        var acc = 0f;
        for (var i = 1; i < part.Length; i++)
        {
            var a = part[i - 1];
            var b = part[i];
            var dLon = DeltaLon(a.Longitude, b.Longitude);
            var dLat = b.Latitude - a.Latitude;
            var seg = MathF.Sqrt(dLon * dLon + dLat * dLat);
            if (acc + seg >= target || i == part.Length - 1)
            {
                var t = seg > 1e-5f ? Math.Clamp((target - acc) / seg, 0f, 1f) : 0.5f;
                var lon = WrapLon(a.Longitude + dLon * t);
                var lat = a.Latitude + dLat * t;

                var i0 = Math.Max(0, i - 2);
                var i1 = Math.Min(part.Length - 1, i + 1);
                var tx = DeltaLon(part[i0].Longitude, part[i1].Longitude);
                var ty = -(part[i1].Latitude - part[i0].Latitude);
                var len = MathF.Sqrt(tx * tx + ty * ty);
                if (len < 1e-4f) return (lat, lon, 1f, 0f);
                tx /= len;
                ty /= len;
                // Keep text left-to-right on the map so river names never flip upside down.
                if (tx < -0.05f) { tx = -tx; ty = -ty; }
                return (lat, lon, tx, ty);
            }
            acc += seg;
        }
        return (part[0].Latitude, part[0].Longitude, 1f, 0f);
    }

    private static float DeltaLon(float from, float to)
    {
        var d = to - from;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    private static float WrapLon(float lon)
    {
        if (lon > 180f) return lon - 360f;
        if (lon <= -180f) return lon + 360f;
        return lon;
    }
}
