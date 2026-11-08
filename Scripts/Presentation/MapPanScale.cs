using System;

namespace DealOrForce;

/// <summary>Screen-space map movement. Distances are measured above the ground, not
/// from the planet's centre: otherwise a close tactical view races past provinces.
/// Kept engine-independent so zoom/resolution behaviour has numerical regression tests.</summary>
internal static class MapPanScale
{
    internal static float OrbitRadiansPerPixel(float distance, float radius, float verticalFov, float viewportHeight)
    {
        var altitude = Math.Max(.001f, distance - radius);
        var visibleGroundHeight = 2f * altitude * MathF.Tan(verticalFov * MathF.PI / 360f);
        return visibleGroundHeight / (Math.Max(.001f, radius) * Math.Max(1f, viewportHeight));
    }

    internal static float FlatUnitsPerPixel(float verticalExtent, float viewportHeight) =>
        Math.Max(.001f, verticalExtent) / Math.Max(1f, viewportHeight);
}
