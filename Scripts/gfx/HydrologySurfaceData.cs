using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Renderer-only shoreline distances. Never fed back into movement or ownership.</summary>
public static class HydrologySurfaceData
{
    // RGB: lake SDF, river SDF, authored river importance. 128 is shore, 16 steps/texel.
    // A bounded chamfer transform is linear in map area, built once on the map worker.
    public static byte[] Build(HydrologyRaster source, IReadOnlyList<RiverVisualProfile> profiles = null,
        IReadOnlyList<RiverFeature> rivers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width < 1 || source.Height < 1 ||
            source.Pixels.Length != checked(source.Width * source.Height * 2))
            throw new ArgumentException("Hydrology dimensions do not match coverage.", nameof(source));
        var count = source.Width * source.Height;
        var result = new byte[checked(count * 3)];
        var distance = new byte[count];
        for (var channel = 0; channel < 2; channel++)
        {
            Array.Fill(distance, (byte)127);
            // Cell boundaries begin halfway between opposing cell centres. Longitude
            // wraps, latitude does not; a polar shoreline must not sample the other pole.
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var cell = y * source.Width + x;
                var wet = source.Pixels[cell * 2 + channel] > 0;
                if (Different(y * source.Width + (x + 1) % source.Width) ||
                    Different(y * source.Width + (x - 1 + source.Width) % source.Width) ||
                    y > 0 && Different(cell - source.Width) ||
                    y + 1 < source.Height && Different(cell + source.Width)) distance[cell] = 8;
                bool Different(int other) => (source.Pixels[other * 2 + channel] > 0) != wet;
            }
            for (var cell = 0; cell < count; cell++)
            {
                var x = cell % source.Width;
                if (x > 0) Relax(cell, cell - 1, 16);
                if (cell >= source.Width)
                {
                    Relax(cell, cell - source.Width, 16);
                    Relax(cell, cell - source.Width + (x > 0 ? -1 : source.Width - 1), 23);
                    Relax(cell, cell - source.Width + (x + 1 < source.Width ? 1 : 1 - source.Width), 23);
                }
            }
            for (var cell = count - 1; cell >= 0; cell--)
            {
                var x = cell % source.Width;
                if (x + 1 < source.Width) Relax(cell, cell + 1, 16);
                if (cell + source.Width < count)
                {
                    Relax(cell, cell + source.Width, 16);
                    Relax(cell, cell + source.Width + (x > 0 ? -1 : source.Width - 1), 23);
                    Relax(cell, cell + source.Width + (x + 1 < source.Width ? 1 : 1 - source.Width), 23);
                }
            }
            for (var cell = 0; cell < count; cell++)
                result[cell * 3 + channel] = (byte)(128 +
                    (source.Pixels[cell * 2 + channel] > 0 ? -distance[cell] : distance[cell]));
        }
        for (var cell = 0; cell < count; cell++) result[cell * 3 + 2] = source.Pixels[cell * 2 + 1];
        if (profiles is not null && rivers is not null)
            foreach (var profile in profiles)
            foreach (var river in rivers)
            {
                if (!profile.FeatureNames.Contains(river.Name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var part in river.Parts)
                    for (var index = 1; index < part.Length; index++)
                        Stroke(part[index - 1], part[index], profile);
            }
        return result;

        void Relax(int cell, int other, int step)
        {
            var next = distance[other] + step;
            if (next < distance[cell]) distance[cell] = (byte)next;
        }

        // Analytic capsule distances retain sub-texel geography and join diagonal segments.
        // The original binary mask remains the simulation's authority; only RGB display
        // data is extended. Work is bounded to the named river's small segment rectangles.
        void Stroke(HydroPoint start, HydroPoint end, RiverVisualProfile profile)
        {
            var ax = (start.Longitude + 180f) / 360f * source.Width - .5f;
            var ay = (90f - start.Latitude) / 180f * source.Height - .5f;
            var bx = (end.Longitude + 180f) / 360f * source.Width - .5f;
            var by = (90f - end.Latitude) / 180f * source.Height - .5f;
            if (MathF.Abs(bx - ax) > source.Width * .5f)
            {
                if (ax < bx) ax += source.Width;
                else bx += source.Width;
            }
            var dx = bx - ax;
            var dy = by - ay;
            var lengthSquared = dx * dx + dy * dy;
            var margin = profile.Width * .5f + 1.5f;
            var minX = (int)MathF.Floor(MathF.Min(ax, bx) - margin);
            var maxX = (int)MathF.Ceiling(MathF.Max(ax, bx) + margin);
            var minY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, by) - margin), 0, source.Height - 1);
            var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, by) + margin), 0, source.Height - 1);
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var t = lengthSquared > .00001f
                    ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0f, 1f) : 0f;
                var latitude = start.Latitude + (end.Latitude - start.Latitude) * t;
                var downstream = Math.Clamp((profile.UpstreamLatitude - latitude) /
                    (profile.UpstreamLatitude - profile.FullWidthLatitude), 0f, 1f);
                var radius = (profile.UpstreamWidth + (profile.Width - profile.UpstreamWidth) * downstream) * .5f;
                var ox = x - (ax + dx * t);
                var oy = y - (ay + dy * t);
                var signed = MathF.Sqrt(ox * ox + oy * oy) - radius;
                if (signed > 1.5f) continue;
                var wrapX = (x % source.Width + source.Width) % source.Width;
                var write = (y * source.Width + wrapX) * 3;
                var encoded = (byte)Math.Clamp((int)MathF.Round(128 + signed * 16), 1, 255);
                result[write + 1] = Math.Min(result[write + 1], encoded);
                result[write + 2] = 255; // Reserved visual prominence, never crossing strength.
            }
        }
    }
}
