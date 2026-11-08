using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Render-only centre curves extracted from the authoritative river PNG.
/// Thin a private coverage copy, trace its graph, and round raster corners locally.
/// Crossings, navigability, picking and the author's PNG bytes are never rewritten.
/// No matching GeoJSON or per-river hardcoded coordinates are required by a mod.</summary>
public static class RasterRiverCourses
{
    private static readonly int[] Dx = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] Dy = { -1, -1, 0, 1, 1, 1, 0, -1 };

    public static IReadOnlyList<RiverFeature> Extract(HydrologyRaster source)
    {
        var width = source.Width; var height = source.Height;
        if (width < 1 || height < 1 || source.Pixels.Length != checked(width * height * 2))
            throw new ArgumentException("Invalid hydrology raster.", nameof(source));
        var mask = new byte[width * height]; var wet = new List<int>();
        for (var i = 0; i < mask.Length; i++)
            if (source.Pixels[i * 2 + 1] > 0) { mask[i] = 1; wet.Add(i); }
        var remove = new List<int>();
        int Neighbour(int cell, int direction)
        {
            var y = cell / width + Dy[direction];
            if (y < 0 || y >= height) return -1;
            return y * width + (cell % width + Dx[direction] + width) % width;
        }
        int On(int cell, int direction)
        { var next = Neighbour(cell, direction); return next < 0 ? 0 : mask[next]; }

        // Zhang–Suen topology-preserving thinning. Iterate only authored wet cells,
        // not the whole Earth per pass; one-pixel mod rivers need no thinning.
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                remove.Clear();
                foreach (var cell in wet)
                {
                    if (mask[cell] == 0) continue;
                    var neighbours = 0; var transitions = 0;
                    for (var d = 0; d < 8; d++)
                    { var current = On(cell, d); neighbours += current; if (current == 0 && On(cell, (d + 1) % 8) != 0) transitions++; }
                    if (neighbours < 2 || neighbours > 6 || transitions != 1) continue;
                    var n = On(cell, 0); var e = On(cell, 2); var s = On(cell, 4); var w = On(cell, 6);
                    if (pass == 0 ? n * e * s != 0 || e * s * w != 0 : n * e * w != 0 || n * s * w != 0) continue;
                    remove.Add(cell);
                }
                if (remove.Count > 0) changed = true;
                foreach (var cell in remove) mask[cell] = 0;
            }
        }
        var visited = new byte[mask.Length];
        bool Linked(int cell, int d)
        {
            if (On(cell, d) == 0) return false;
            // A raster corner has two orthogonal edges, not a triangular shortcut.
            return d % 2 == 0 || (On(cell, (d + 7) % 8) == 0 && On(cell, (d + 1) % 8) == 0);
        }
        int Degree(int cell)
        { var count = 0; for (var d = 0; d < 8; d++) if (Linked(cell, d)) count++; return count; }
        var result = new List<RiverFeature>();
        HydroPoint Point(int cell) => new((cell % width + .5f) / width * 360 - 180,
            90 - (cell / width + .5f) / height * 180);
        void Trace(int start, int direction)
        {
            var points = new List<HydroPoint> { Point(start) };
            var cell = start; var importance = source.Pixels[start * 2 + 1];
            while (true)
            {
                var next = Neighbour(cell, direction);
                visited[cell] |= (byte)(1 << direction); visited[next] |= (byte)(1 << ((direction + 4) % 8));
                var point = Point(next); var previous = points[^1];
                while (point.Longitude - previous.Longitude > 180) point = point with { Longitude = point.Longitude - 360 };
                while (point.Longitude - previous.Longitude < -180) point = point with { Longitude = point.Longitude + 360 };
                points.Add(point); importance = Math.Max(importance, source.Pixels[next * 2 + 1]);
                if (Degree(next) != 2) break;
                cell = next; direction = -1;
                for (var d = 0; d < 8; d++) if (Linked(cell, d) && (visited[cell] & (1 << d)) == 0) { direction = d; break; }
                if (direction < 0) break;
            }
            var smooth = new List<HydroPoint> { points[0] };
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                smooth.Add(new HydroPoint(a.Longitude * .75f + b.Longitude * .25f, a.Latitude * .75f + b.Latitude * .25f));
                smooth.Add(new HydroPoint(a.Longitude * .25f + b.Longitude * .75f, a.Latitude * .25f + b.Latitude * .75f));
            }
            smooth.Add(points[^1]);
            var rank = Math.Clamp((int)MathF.Round((225 - importance) / 22f), 0, 6);
            result.Add(new RiverFeature("PNG river", rank, new[] { smooth.ToArray() }));
        }
        // Begin at endpoints/junctions, then cover closed loops left by the first pass.
        for (var pass = 0; pass < 2; pass++)
            foreach (var cell in wet)
            {
                if (mask[cell] == 0 || pass == 0 && Degree(cell) == 2) continue;
                if (Degree(cell) == 0 && pass == 0)
                    result.Add(new RiverFeature("PNG river", 6, new[] { new[] { Point(cell), Point(cell) } }));
                for (var d = 0; d < 8; d++) if (Linked(cell, d) && (visited[cell] & (1 << d)) == 0) Trace(cell, d);
            }
        return result;
    }
}
