using System;
using System.Collections.Generic;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace DealOrForce;

/// <summary>
/// The flat map is one sheet repeated side by side, so a course that crosses
/// longitude 180 jumps from one edge to the other. The jump is a break in the
/// drawing, not a second world: the cells on either side stay neighbours, and a
/// piece too short to be a triangle strip is left out rather than sent to the
/// renderer as two vertices.
/// </summary>
public static class DateLineSeam
{
    public readonly record struct Run(int Start, int Count);

    /// <summary>The same geographic point on the sheet nearest the reference.
    /// A change of sheet is a coordinate rebase, never twenty map units of travel.</summary>
    public static float NearestCopy(float coordinate, float reference, float width)
    {
        if (!float.IsFinite(coordinate) || !float.IsFinite(reference) ||
            !float.IsFinite(width) || width <= 0) return coordinate;
        var delta = (double)reference - coordinate;
        if (Math.Abs(delta) <= width * .5d) return coordinate;
        return (float)(coordinate + Math.Floor(delta / width + .5d) * width);
    }

    /// <summary>
    /// Splits a polyline where consecutive points leap more than half the sheet.
    /// Runs shorter than two points are omitted: one cross-section is two
    /// vertices, and a triangle strip needs three.
    /// </summary>
    public static void Split(ReadOnlySpan<float> x, float mapWidth, List<Run> into)
    {
        into.Clear();
        if (x.Length < 2 || !(mapWidth > 0f)) return;
        var limit = mapWidth * 0.5f;
        var start = 0;
        for (var i = 1; i <= x.Length; i++)
        {
            if (i < x.Length && MathF.Abs(x[i] - x[i - 1]) <= limit) continue;
            var count = i - start;
            if (count >= 2) into.Add(new Run(start, count));
            start = i;
        }
    }

    /// <summary>
    /// An ocean wave's east-west frequency, rounded to a whole number of cycles
    /// around the world. Longitude 0 and longitude 360 are then the same phase.
    /// The map shader uses this same rounding.
    /// </summary>
    public static float WrappedFrequency(float frequency, float width)
    {
        if (!(width >= 1f) || frequency == 0f) return frequency;
        var turn = MathF.PI * 2f;
        var cycles = MathF.Max(1f, MathF.Round(MathF.Abs(frequency) * width / turn));
        return MathF.Sign(frequency) * cycles * turn / width;
    }
}

/// <summary>
/// Shared Godot-free geometry, coordinate, tangent, and sanity guards for all globe and flat-map
/// line, ribbon, offensive/defensive plan, battle-arrow, supply, fleet, strait, trade, and support builders.
/// </summary>
public static class GlobeLineSanity
{
    public const float GlobeRadius = 5f;
    public const float MinGlobeVertexRadius = 4.0f;
    public const float MaxGlobeVertexRadius = 6.25f;
    public const float FlatMapWidth = 20f;
    public const float FlatMapHeight = 10f;
    public const float MaxFlatVertexXDelta = 28f;
    public const float MaxFlatVertexY = 6.0f;
    public const float MaxFlatVertexZ = 2.0f;
    public const float MaxGlobeSegmentLength = 3.5f;
    public const float MaxFlatSegmentLength = 4.0f;
    public const float MaxBattleArrowChordGlobe = GlobeRadius * 0.28f;
    public const float MaxBattleArrowChordFlat = FlatMapWidth * 0.08f;

    public static bool IsFinite(float value) => float.IsFinite(value);

    public static bool IsFinite(SysVec2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    public static bool IsFinite(SysVec3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    public static float ClampUnit(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;

    public static float SafeClamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public static float WrapDeltaDegrees(float delta)
    {
        if (!float.IsFinite(delta)) return 0f;
        if (MathF.Abs(delta) <= 180f) return delta;
        return (float)(delta + 180.0 - Math.Floor((delta + 180.0) / 360.0) * 360.0 - 180.0);
    }

    public static float WrapLongitude(float longitude)
    {
        if (!float.IsFinite(longitude)) return 0f;
        if (longitude > -180f && longitude <= 180f) return longitude;
        var wrapped = WrapDeltaDegrees(longitude);
        return wrapped <= -180f ? 180f : wrapped;
    }

    public static bool IsValidGeo(float latitude, float longitude) =>
        float.IsFinite(latitude) && float.IsFinite(longitude) &&
        latitude >= -90f && latitude <= 90f &&
        longitude >= -540f && longitude <= 540f;

    public static bool TryValidateProvinceId(int provinceId, int provinceCount, ReadOnlySpan<int> centerCells) =>
        provinceId > 0 && provinceId <= provinceCount && provinceId < centerCells.Length && centerCells[provinceId] > 0;

    public static bool TryResolveEntityGeo(
        float latitude,
        float longitude,
        int provinceId,
        int provinceCount,
        ReadOnlySpan<int> centerCells,
        Func<int, (float Latitude, float Longitude)> centerOf,
        out (float Latitude, float Longitude) geo)
    {
        var hasValidProvince = TryValidateProvinceId(provinceId, provinceCount, centerCells);
        if (IsValidGeo(latitude, longitude) && !(latitude == 0f && longitude == 0f))
        {
            geo = (latitude, longitude);
            return true;
        }
        if (hasValidProvince && centerOf is not null)
        {
            var center = centerOf(provinceId);
            if (IsValidGeo(center.Latitude, center.Longitude))
            {
                geo = center;
                return true;
            }
        }
        geo = default;
        return false;
    }

    public static (float Latitude, float Longitude) GeographicFromNormalSafe(SysVec3 normal)
    {
        if (!TrySafeNormalize(normal, out var unit)) return (0f, 0f);
        const float radToDeg = 180f / MathF.PI;
        var latitude = MathF.Asin(ClampUnit(unit.Y)) * radToDeg;
        var longitude = WrapLongitude(MathF.Atan2(unit.X, unit.Z) * radToDeg - 180f);
        return (latitude, longitude);
    }

    public static bool TrySafeNormalize(SysVec3 v, out SysVec3 unit)
    {
        if (!IsFinite(v))
        {
            unit = default;
            return false;
        }
        var lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq < 1e-12f)
        {
            unit = default;
            return false;
        }
        unit = v / MathF.Sqrt(lenSq);
        return IsFinite(unit);
    }

    public static bool TrySafeCrossAxis(SysVec3 normal, out SysVec3 axis)
    {
        if (!TrySafeNormalize(normal, out var unit))
        {
            axis = default;
            return false;
        }
        if (TrySafeNormalize(SysVec3.Cross(unit, SysVec3.UnitY), out axis)) return true;
        if (TrySafeNormalize(SysVec3.Cross(unit, SysVec3.UnitX), out axis)) return true;
        return TrySafeNormalize(SysVec3.Cross(unit, SysVec3.UnitZ), out axis);
    }

    public static bool TrySafeRibbonFrame(
        SysVec3 point,
        SysVec3 before,
        SysVec3 after,
        bool flatMap,
        bool flatPositiveZNormal,
        out SysVec3 normal,
        out SysVec3 tangent,
        out SysVec3 side)
    {
        normal = default;
        tangent = default;
        side = default;
        if (!IsFinite(point)) return false;

        if (flatMap)
        {
            normal = new SysVec3(0f, 0f, flatPositiveZNormal ? 1f : -1f);
        }
        else if (!TrySafeNormalize(point, out normal))
        {
            return false;
        }

        var raw = IsFinite(before) && IsFinite(after) ? after - before : SysVec3.Zero;
        if (!TryProjectAndNormalizeTangent(raw, normal, flatMap, out tangent))
        {
            var prevDelta = IsFinite(before) ? point - before : SysVec3.Zero;
            var nextDelta = IsFinite(after) ? after - point : SysVec3.Zero;
            if (!TryProjectAndNormalizeTangent(prevDelta, normal, flatMap, out tangent) &&
                !TryProjectAndNormalizeTangent(nextDelta, normal, flatMap, out tangent) &&
                !TrySafeCrossAxis(normal, out tangent))
            {
                return false;
            }
        }

        if (!TrySafeNormalize(SysVec3.Cross(normal, tangent), out side))
        {
            if (!TrySafeCrossAxis(normal, out side)) return false;
        }
        return true;
    }

    private static bool TryProjectAndNormalizeTangent(SysVec3 raw, SysVec3 normal, bool flatMap, out SysVec3 tangent)
    {
        if (!IsFinite(raw))
        {
            tangent = default;
            return false;
        }
        if (flatMap) raw.Z = 0f;
        else raw -= normal * SysVec3.Dot(raw, normal);
        return TrySafeNormalize(raw, out tangent);
    }

    public static bool TrySafeSphereQuadMidpoint(
        SysVec3 start,
        SysVec3 end,
        SysVec3 preferredNormal,
        float surfaceRadius,
        out SysVec3 midpoint)
    {
        midpoint = default;
        if (!IsFinite(start) || !IsFinite(end) || !float.IsFinite(surfaceRadius) || surfaceRadius <= 0.1f)
            return false;

        var sum = start + end;
        if (sum.LengthSquared() >= 1e-4f && TrySafeNormalize(sum, out var unit))
        {
            midpoint = unit * surfaceRadius;
            return IsFinite(midpoint);
        }

        var fallbackAxisSource = TrySafeNormalize(preferredNormal, out var pref) ? pref : start;
        if (!TrySafeCrossAxis(fallbackAxisSource, out var ortho)) return false;
        midpoint = ortho * surfaceRadius;
        return IsFinite(midpoint);
    }

    public static SysVec3 InterpolateSurfacePoint(SysVec3 start, SysVec3 end, float t, bool flatMap)
    {
        if (!IsFinite(start) || !IsFinite(end) || !float.IsFinite(t)) return SysVec3.Zero;
        t = Math.Clamp(t, 0f, 1f);
        if (flatMap) return SysVec3.Lerp(start, end, t);

        var rA = start.Length();
        var rB = end.Length();
        if (!float.IsFinite(rA) || !float.IsFinite(rB) || rA < 0.1f || rB < 0.1f) return SysVec3.Zero;
        var uA = start / rA;
        var uB = end / rB;
        var radius = rA + (rB - rA) * t;
        var dot = ClampUnit(SysVec3.Dot(uA, uB));
        if (dot > -0.9995f && TrySafeNormalize(SysVec3.Lerp(uA, uB, t), out var dir))
            return dir * radius;

        if (!TrySafeCrossAxis(uA, out var ortho)) return SysVec3.Zero;
        var angle = MathF.PI * t;
        return (uA * MathF.Cos(angle) + ortho * MathF.Sin(angle)) * radius;
    }

    public static bool IsValidEmittedVertex(SysVec3 v, bool flatMap, float refFlatX = 0f)
    {
        if (!IsFinite(v)) return false;
        if (flatMap)
        {
            var refX = float.IsFinite(refFlatX) ? refFlatX : 0f;
            return MathF.Abs(v.X - refX) <= MaxFlatVertexXDelta &&
                   MathF.Abs(v.Y) <= MaxFlatVertexY &&
                   MathF.Abs(v.Z) <= MaxFlatVertexZ;
        }
        var lenSq = v.LengthSquared();
        return lenSq >= MinGlobeVertexRadius * MinGlobeVertexRadius &&
               lenSq <= MaxGlobeVertexRadius * MaxGlobeVertexRadius;
    }

    public static bool IsValidEmittedEdge(
        SysVec3 a,
        SysVec3 b,
        bool flatMap,
        float refFlatX = 0f,
        float maxSegmentLength = 0f)
    {
        if (!IsValidEmittedVertex(a, flatMap, refFlatX) || !IsValidEmittedVertex(b, flatMap, refFlatX))
            return false;
        var cap = maxSegmentLength > 0f && float.IsFinite(maxSegmentLength)
            ? maxSegmentLength
            : (flatMap ? MaxFlatSegmentLength : MaxGlobeSegmentLength);
        return SysVec3.DistanceSquared(a, b) <= cap * cap;
    }

    public static void CollectLineRuns(
        ReadOnlySpan<SysVec3> points,
        bool flatMap,
        float mapWidth,
        float maxSegmentLength,
        List<DateLineSeam.Run> into,
        float refFlatX = 0f)
    {
        into.Clear();
        if (points.Length < 2) return;
        var wrapLimit = flatMap && float.IsFinite(mapWidth) && mapWidth > 0f ? mapWidth * 0.5f : float.PositiveInfinity;
        var segCap = maxSegmentLength > 0f && float.IsFinite(maxSegmentLength)
            ? maxSegmentLength
            : (flatMap ? MaxFlatSegmentLength : MaxGlobeSegmentLength);
        var segCapSq = segCap * segCap;

        var start = -1;
        for (var i = 0; i < points.Length; i++)
        {
            var valid = IsValidEmittedVertex(points[i], flatMap, refFlatX);
            if (!valid)
            {
                if (start >= 0 && i - start >= 2) into.Add(new DateLineSeam.Run(start, i - start));
                start = -1;
                continue;
            }
            if (start < 0)
            {
                start = i;
                continue;
            }
            var prev = points[i - 1];
            var curr = points[i];
            var connected = (!flatMap || MathF.Abs(curr.X - prev.X) <= wrapLimit) &&
                            SysVec3.DistanceSquared(prev, curr) <= segCapSq;
            if (connected) continue;
            if (i - start >= 2) into.Add(new DateLineSeam.Run(start, i - start));
            start = i;
        }
        if (start >= 0 && points.Length - start >= 2)
            into.Add(new DateLineSeam.Run(start, points.Length - start));
    }

    public static bool IsValidScreenRibbonSegment(
        SysVec2 screenA,
        SysVec2 screenB,
        float depthA,
        float depthB,
        float minDepth,
        float maxScreenSpan)
    {
        if (!IsFinite(screenA) || !IsFinite(screenB) ||
            !float.IsFinite(depthA) || !float.IsFinite(depthB) ||
            !float.IsFinite(minDepth) || minDepth <= 0f ||
            !float.IsFinite(maxScreenSpan) || maxScreenSpan <= 0f)
            return false;
        if (depthA < minDepth || depthB < minDepth) return false;
        if (MathF.Abs(screenA.X) > 16384f || MathF.Abs(screenA.Y) > 16384f ||
            MathF.Abs(screenB.X) > 16384f || MathF.Abs(screenB.Y) > 16384f)
            return false;
        var sq = SysVec2.DistanceSquared(screenA, screenB);
        return float.IsFinite(sq) && sq >= 0.01f && sq <= maxScreenSpan * maxScreenSpan;
    }
}
