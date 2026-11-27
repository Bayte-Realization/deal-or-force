using System;
using System.Numerics;

namespace DealOrForce.Simulation;

/// <summary>
/// Pure-C# camera-locked map overlay horizon culling, relayout trigger, and 2D redraw rules.
/// </summary>
public static class MapOverlaySyncRules
{
    public const float DefaultHorizonExtraMargin = 0.014f;
    public const float RapidSweepAngleRad = 0.065f;
    public const float RapidZoomRatioDelta = 0.07f;

    public static float GlobeHorizonCosine(float globeRadius, float cameraDistance, float extraMargin)
    {
        if (!float.IsFinite(globeRadius) || globeRadius <= 0f) return 0f;
        var safeDistance = float.IsFinite(cameraDistance)
            ? MathF.Max(globeRadius, cameraDistance)
            : globeRadius;
        var margin = float.IsFinite(extraMargin) ? extraMargin : 0f;
        return Math.Clamp(globeRadius / safeDistance + margin, 0f, 0.999f);
    }

    public static bool IsBeyondGlobeHorizon(Vector3 worldSurfaceNormal, Vector3 cameraNormal, float horizonCos)
    {
        if (!float.IsFinite(horizonCos)) return true;
        var lenSqA = worldSurfaceNormal.LengthSquared();
        var lenSqB = cameraNormal.LengthSquared();
        if (!float.IsFinite(lenSqA) || !float.IsFinite(lenSqB) || lenSqA < 1e-10f || lenSqB < 1e-10f)
            return true;
        var dot = Vector3.Dot(worldSurfaceNormal, cameraNormal) / MathF.Sqrt(lenSqA * lenSqB);
        return !float.IsFinite(dot) || dot < horizonCos;
    }

    public static bool IsBeyondGlobeHorizon(float dotProduct, float horizonCos) =>
        !float.IsFinite(dotProduct) || !float.IsFinite(horizonCos) || dotProduct < horizonCos;

    public static bool ShouldRelayoutCitiesNow(
        float angleDeltaRad,
        float zoomRatioDelta,
        double elapsedSec,
        double intervalSec,
        bool forceDirty)
    {
        if (forceDirty) return true;
        if (float.IsFinite(angleDeltaRad) && MathF.Abs(angleDeltaRad) >= RapidSweepAngleRad) return true;
        if (float.IsFinite(zoomRatioDelta) && MathF.Abs(zoomRatioDelta) >= RapidZoomRatioDelta) return true;
        return double.IsFinite(elapsedSec) && double.IsFinite(intervalSec) && elapsedSec >= intervalSec;
    }

    public static bool ShouldQueueOverlayRedraw(bool cameraMoved, bool animating, bool hasActive2DOverlays) =>
        animating || (cameraMoved && hasActive2DOverlays);

    /// <summary>Unit surface normal matching GlobePresentation.SurfacePoint spherical coordinates.</summary>
    public static Vector3 SurfaceNormalFromGeo(float latitudeDeg, float longitudeDeg)
    {
        var lat = latitudeDeg * (MathF.PI / 180f);
        var lon = (longitudeDeg + 180f) * (MathF.PI / 180f);
        var cosLat = MathF.Cos(lat);
        return new Vector3(cosLat * MathF.Sin(lon), MathF.Sin(lat), cosLat * MathF.Cos(lon));
    }
}
