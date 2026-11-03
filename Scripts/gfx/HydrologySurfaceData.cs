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
        IReadOnlyList<RiverFeature> rivers = null, bool connectRiverDiagonals = true, float widthReach = 0f)
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
            // The two passes above walk a row only as far as its array ends. A shore
            // on the last column never reaches the first, so a lake or river that
            // crosses longitude 180 was pinched there. The sheet still wraps; this
            // only lets the distance already computed on one edge continue onto the other.
            StitchDateLine();
            for (var cell = 0; cell < count; cell++)
                result[cell * 3 + channel] = (byte)(128 +
                    (source.Pixels[cell * 2 + channel] > 0 ? -distance[cell] : distance[cell]));
        }
        for (var cell = 0; cell < count; cell++) result[cell * 3 + 2] = source.Pixels[cell * 2 + 1];
        if (connectRiverDiagonals) ConnectRiverDiagonals(source, result);
        if (profiles is not null && rivers is not null)
            foreach (var profile in profiles)
            foreach (var river in rivers)
            {
                if (!profile.FeatureNames.Contains(river.Name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var part in river.Parts)
                    for (var index = 1; index < part.Length; index++)
                        Stroke(part[index - 1], part[index], profile);
            }
        ScaleRiverWidth(source, result, widthReach);
        return result;

        void Relax(int cell, int other, int step)
        {
            var next = distance[other] + step;
            if (next < distance[cell]) distance[cell] = (byte)next;
        }

        void StitchDateLine()
        {
            var width = source.Width;
            var height = source.Height;
            if (width < 2) return;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var y = 0; y < height; y++)
                {
                    var row = y * width;
                    var last = row + width - 1;
                    Relax(row, last, 16);
                    Relax(last, row, 16);
                    for (var x = 1; x < width; x++) Relax(row + x, row + x - 1, 16);
                    for (var x = width - 2; x >= 0; x--) Relax(row + x, row + x + 1, 16);
                }
                for (var y = 1; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var cell = y * width + x;
                    var up = cell - width;
                    Relax(cell, up, 16);
                    Relax(cell, up + (x == 0 ? width - 1 : -1), 23);
                    Relax(cell, up + (x == width - 1 ? 1 - width : 1), 23);
                }
                for (var y = height - 2; y >= 0; y--)
                for (var x = 0; x < width; x++)
                {
                    var cell = y * width + x;
                    var down = cell + width;
                    Relax(cell, down, 16);
                    Relax(cell, down + (x == 0 ? width - 1 : -1), 23);
                    Relax(cell, down + (x == width - 1 ? 1 - width : 1), 23);
                }
            }
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

    /// <summary>
    /// How far each texel lies from the nearest river's real course, and how great that river is.
    ///
    /// The raster paints a river as a corridor one to five cells wide - seven to thirty-five
    /// kilometres - and a river drawn as its corridor is a sausage that swells as the camera comes
    /// down. The course itself is in the vectors. Each texel holds where the nearest river line
    /// is from its centre - R and G the offset in sixteenths of a texel (128 = none), up to eight
    /// texels - B that river's importance (the raster's own 225 - 22 per rank, 0 = no river
    /// near) and A the course's direction there (0..255 over half a turn): the four nearest
    /// texels' points alone left a dotted line, a texel apart; through each point runs the river. A distance field would not do: filtered, its V-shaped valley never reaches zero, so
    /// a line thinner than half a texel vanished. The shader finds the true distance to the
    /// course from the four nearest texels and draws a line as wide as it chooses along it.
    /// </summary>
    public static byte[] BuildCentreline(int width, int height, IReadOnlyList<RiverFeature> rivers, int maxRank = 6)
    {
        var result = new byte[checked(width * height * 4)];
        var best = new float[width * height];
        Array.Fill(best, float.MaxValue);
        for (var cell = 0; cell < width * height; cell++) { result[cell * 4] = 128; result[cell * 4 + 1] = 128; }
        if (rivers is null) return result;
        const float reach = 7.9f;
        foreach (var river in rivers)
        {
            if (river.Rank > maxRank) continue;
            var importance = (byte)Math.Clamp(225 - river.Rank * 22, 72, 225);
            foreach (var part in river.Parts)
                for (var index = 1; index < part.Length; index++)
                    Segment(part[index - 1], part[index], importance);
        }
        return result;

        void Segment(HydroPoint start, HydroPoint end, byte importance)
        {
            var ax = (start.Longitude + 180f) / 360f * width - .5f;
            var ay = (90f - start.Latitude) / 180f * height - .5f;
            var bx = (end.Longitude + 180f) / 360f * width - .5f;
            var by = (90f - end.Latitude) / 180f * height - .5f;
            if (MathF.Abs(bx - ax) > width * .5f)
            {
                if (ax < bx) ax += width;
                else bx += width;
            }
            var dx = bx - ax;
            var dy = by - ay;
            var lengthSquared = dx * dx + dy * dy;
            var angle = MathF.Atan2(dy, dx);
            if (angle < 0f) angle += MathF.PI;
            var heading = (byte)Math.Clamp((int)MathF.Round(angle / MathF.PI * 255f), 0, 255);
            var minX = (int)MathF.Floor(MathF.Min(ax, bx) - reach);
            var maxX = (int)MathF.Ceiling(MathF.Max(ax, bx) + reach);
            var minY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, by) - reach), 0, height - 1);
            var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, by) + reach), 0, height - 1);
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var t = lengthSquared > .00001f
                    ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0f, 1f) : 0f;
                var ox = x - (ax + dx * t);
                var oy = y - (ay + dy * t);
                var distance = MathF.Sqrt(ox * ox + oy * oy);
                if (distance > reach) continue;
                var cell = y * width + (x % width + width) % width;
                // The nearer river owns the texel; at equal distance the greater one.
                if (distance > best[cell] || distance == best[cell] && importance <= result[cell * 4 + 2]) continue;
                best[cell] = distance;
                result[cell * 4] = (byte)Math.Clamp((int)MathF.Round(128 - ox * 16f), 0, 255);
                result[cell * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(128 - oy * 16f), 0, 255);
                result[cell * 4 + 2] = importance;
                result[cell * 4 + 3] = heading;
            }
        }
    }

    /// <summary>
    /// A one-cell-wide diagonal raster river is a chain of squares touching at a point.
    /// Linear SDF filtering turns those contacts into narrow, regularly spaced necks.
    /// Round just those 8-connected contacts with short capsules, not a global dilation:
    /// a gap between non-neighbouring cells, a lake and a mod's authored route stay intact.
    /// </summary>
    private static void ConnectRiverDiagonals(HydrologyRaster source, byte[] surface)
    {
        var width = source.Width;
        var height = source.Height;
        if (width < 2 || height < 2) return;
        var pixels = source.Pixels;
        for (var y = 0; y < height - 1; y++)
        for (var x = 0; x < width; x++)
        {
            var east = (x + 1) % width;
            var northWest = pixels[(y * width + x) * 2 + 1];
            var northEast = pixels[(y * width + east) * 2 + 1];
            var southWest = pixels[((y + 1) * width + x) * 2 + 1];
            var southEast = pixels[((y + 1) * width + east) * 2 + 1];
            if (northWest > 0 && southEast > 0 && northEast == 0 && southWest == 0)
                Join(x, y, 1, Math.Max(northWest, southEast));
            else if (northEast > 0 && southWest > 0 && northWest == 0 && southEast == 0)
                Join(x + 1, y, -1, Math.Max(northEast, southWest));
        }

        void Join(int ax, int ay, int dx, byte importance)
        {
            // Sub-texel width compensates for bilinear sampling at the diagonal saddle.
            // This is display geometry in raster texels, never a crossing-strength value.
            const float radius = .65f;
            var left = Math.Min(ax, ax + dx) - 1;
            var right = Math.Max(ax, ax + dx) + 1;
            for (var py = Math.Max(0, ay - 1); py <= Math.Min(height - 1, ay + 2); py++)
            for (var px = left; px <= right; px++)
            {
                var t = Math.Clamp(((px - ax) * dx + py - ay) * .5f, 0f, 1f);
                var ox = px - (ax + dx * t);
                var oy = py - (ay + t);
                var signed = MathF.Sqrt(ox * ox + oy * oy) - radius;
                if (signed > 1.5f) continue;
                var wrapX = (px % width + width) % width;
                var write = (py * width + wrapX) * 3;
                var encoded = (byte)Math.Clamp((int)MathF.Round(128 + signed * 16), 1, 255);
                surface[write + 1] = Math.Min(surface[write + 1], encoded);
                surface[write + 2] = Math.Max(surface[write + 2], importance);
            }
        }
    }

    /// <summary>
    /// The plate stores three river classes in one cell (96, 160, 225) and the chamfer
    /// draws every class as a one-cell shore. This pushes that shore out on the display
    /// field. Crossing bytes, lakes and a named stroke (importance 255) stay as authored.
    /// <paramref name="reach"/> is the extra half-width, in texels, of class 225.
    /// Zero leaves every earlier caller byte-identical.
    /// </summary>
    private static void ScaleRiverWidth(HydrologyRaster source, byte[] surface, float reach)
    {
        if (reach <= 0f || float.IsNaN(reach)) return;
        reach = MathF.Min(reach, 2.5f);
        var width = source.Width;
        var height = source.Height;
        var margin = (int)MathF.Ceiling(reach + 2f);
        var pixels = source.Pixels;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var cell = y * width + x;
            var importance = pixels[cell * 2 + 1];
            if (importance < 160) continue;
            if (surface[cell * 3 + 2] == 255) continue;
            var extra = reach * Math.Clamp((importance - 96f) / (225f - 96f), 0f, 1f);
            if (extra < 0.02f) continue;
            var radius = 0.5f + extra;
            var limit = radius + 1.5f;
            var limitSq = limit * limit;
            var minY = Math.Max(0, y - margin);
            var maxY = Math.Min(height - 1, y + margin);
            for (var py = minY; py <= maxY; py++)
            {
                var dy = py - y;
                for (var px = x - margin; px <= x + margin; px++)
                {
                    var dx = px - x;
                    var distSq = dx * dx + dy * dy;
                    if (distSq > limitSq) continue;
                    var signed = MathF.Sqrt(distSq) - radius;
                    if (signed > 1.5f) continue;
                    var wrapX = px % width;
                    if (wrapX < 0) wrapX += width;
                    var target = py * width + wrapX;
                    if (surface[target * 3 + 2] == 255) continue;
                    var write = target * 3 + 1;
                    var encoded = (byte)Math.Clamp((int)MathF.Round(128f + signed * 16f), 1, 255);
                    if (encoded < surface[write]) surface[write] = encoded;
                }
            }
        }
    }
}
