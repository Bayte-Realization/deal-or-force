using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Half-open raster rectangle <c>[X0..X1) x [Y0..Y1)</c> in pixel coordinates.</summary>
public readonly struct RasterDirtyRect
{
    public readonly int X0;
    public readonly int Y0;
    public readonly int X1;
    public readonly int Y1;

    public RasterDirtyRect(int x0, int y0, int x1, int y1)
    {
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
    }

    public int Width => Math.Max(0, X1 - X0);
    public int Height => Math.Max(0, Y1 - Y0);
    public long Area => (long)Width * Height;
}

/// <summary>
/// Precomputed per-province pixel bounding boxes on a raster (1920x960 plate or 5760x2880 frontier).
/// Built lazily once per map raster, allowing wartime map repaints to touch only the ~0.5-3% of
/// pixels around provinces whose ownership actually changed instead of repainting the whole planet.
/// Pure C# (no Godot types).
/// </summary>
public sealed class ProvinceRasterBounds
{
    public readonly int Width;
    public readonly int Height;
    public readonly int MaxProvinceId;
    private readonly ushort[] _minX;
    private readonly ushort[] _minY;
    private readonly ushort[] _maxX;
    private readonly ushort[] _maxY;
    private readonly bool[] _present;
    private readonly bool[] _wrapsDateLine;

    private ProvinceRasterBounds(
        int width, int height, int maxProvinceId,
        ushort[] minX, ushort[] minY, ushort[] maxX, ushort[] maxY,
        bool[] present, bool[] wrapsDateLine)
    {
        Width = width;
        Height = height;
        MaxProvinceId = maxProvinceId;
        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _maxY = maxY;
        _present = present;
        _wrapsDateLine = wrapsDateLine;
    }

    public static ProvinceRasterBounds Build(ushort[] cellToProvince, int width, int height)
    {
        if (cellToProvince is null) throw new ArgumentNullException(nameof(cellToProvince));
        if (width <= 0 || height <= 0) throw new ArgumentException("Dimensions must be positive.");
        var cells = checked(width * height);
        if (cellToProvince.Length < cells) throw new ArgumentException("Grid smaller than dimensions.");

        var maxP = 0;
        for (var i = 0; i < cells; i++)
        {
            var p = cellToProvince[i];
            if (p > maxP) maxP = p;
        }

        var len = maxP + 1;
        var minX = new ushort[len];
        var minY = new ushort[len];
        var maxX = new ushort[len];
        var maxY = new ushort[len];
        var present = new bool[len];
        var touchesWest = new bool[len];
        var touchesEast = new bool[len];
        Array.Fill(minX, ushort.MaxValue);
        Array.Fill(minY, ushort.MaxValue);

        var eastEdge = Math.Max(0, width - 8);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var uy = (ushort)y;
            for (var x = 0; x < width; x++)
            {
                var p = cellToProvince[row + x];
                if (p == 0) continue;
                var ux = (ushort)x;
                if (!present[p])
                {
                    present[p] = true;
                    minX[p] = ux;
                    maxX[p] = ux;
                    minY[p] = uy;
                    maxY[p] = uy;
                }
                else
                {
                    if (ux < minX[p]) minX[p] = ux;
                    if (ux > maxX[p]) maxX[p] = ux;
                    if (uy < minY[p]) minY[p] = uy;
                    if (uy > maxY[p]) maxY[p] = uy;
                }
                if (x < 8) touchesWest[p] = true;
                else if (x >= eastEdge) touchesEast[p] = true;
            }
        }

        var wraps = new bool[len];
        for (var p = 1; p < len; p++)
            wraps[p] = touchesWest[p] && touchesEast[p];

        return new ProvinceRasterBounds(width, height, maxP, minX, minY, maxX, maxY, present, wraps);
    }

    /// <summary>
    /// Computes up to <paramref name="maxClusters"/> tight padded dirty rectangles covering every
    /// province whose owner differs between <paramref name="prevOwners"/> and <paramref name="newOwners"/>.
    /// Sets <paramref name="needsFullRepaint"/> if a full repaint is cheaper or required (null previous
    /// owners, length mismatch, date-line wrap crossing, or dirty area exceeding 45% of the raster).
    /// </summary>
    public bool ComputeDirtyRects(
        ushort[] prevOwners,
        ushort[] newOwners,
        int padCells,
        List<RasterDirtyRect> output,
        out bool needsFullRepaint,
        bool disallowDateLineEdge = false,
        int maxClusters = 12)
    {
        output.Clear();
        needsFullRepaint = false;
        if (prevOwners is null || newOwners is null || prevOwners.Length != newOwners.Length)
        {
            needsFullRepaint = true;
            return true;
        }

        var limit = Math.Min(newOwners.Length - 1, MaxProvinceId);
        var pad = Math.Max(0, padCells);
        var anyChanged = false;

        for (var p = 1; p <= limit; p++)
        {
            if (prevOwners[p] == newOwners[p]) continue;
            anyChanged = true;
            if (!_present[p]) continue;
            if (_wrapsDateLine[p])
            {
                needsFullRepaint = true;
                output.Clear();
                return true;
            }

            var x0 = Math.Max(0, _minX[p] - pad);
            var y0 = Math.Max(0, _minY[p] - pad);
            var x1 = Math.Min(Width, _maxX[p] + 1 + pad);
            var y1 = Math.Min(Height, _maxY[p] + 1 + pad);

            if (disallowDateLineEdge && (x0 < pad || x1 + pad > Width))
            {
                needsFullRepaint = true;
                output.Clear();
                return true;
            }

            AddAndMergeRect(output, new RasterDirtyRect(x0, y0, x1, y1), maxClusters);
        }

        if (!anyChanged) return false;
        if (output.Count == 0) return false;

        // Final consolidation pass for any rects that now overlap after intermediate merges.
        ConsolidateOverlaps(output);

        long totalArea = 0;
        for (var i = 0; i < output.Count; i++) totalArea += output[i].Area;
        if (totalArea * 100L >= (long)Width * Height * 45L)
        {
            needsFullRepaint = true;
            output.Clear();
        }
        return true;
    }

    private static void AddAndMergeRect(List<RasterDirtyRect> rects, RasterDirtyRect incoming, int maxClusters)
    {
        // Try merging with an existing cluster if overlapping or nearly adjacent (union <= 1.35x sum of areas).
        for (var i = 0; i < rects.Count; i++)
        {
            var cur = rects[i];
            var ux0 = Math.Min(cur.X0, incoming.X0);
            var uy0 = Math.Min(cur.Y0, incoming.Y0);
            var ux1 = Math.Max(cur.X1, incoming.X1);
            var uy1 = Math.Max(cur.Y1, incoming.Y1);
            var unionArea = (long)(ux1 - ux0) * (uy1 - uy0);
            var sumArea = cur.Area + incoming.Area;
            var overlaps = cur.X0 <= incoming.X1 && incoming.X0 <= cur.X1 &&
                           cur.Y0 <= incoming.Y1 && incoming.Y0 <= cur.Y1;
            if (overlaps || unionArea * 100L <= sumArea * 135L)
            {
                rects[i] = new RasterDirtyRect(ux0, uy0, ux1, uy1);
                return;
            }
        }

        rects.Add(incoming);
        if (rects.Count <= maxClusters) return;

        // Merge the pair with minimum area growth to stay within maxClusters.
        var bestA = 0;
        var bestB = 1;
        var bestWaste = long.MaxValue;
        for (var a = 0; a < rects.Count; a++)
        {
            for (var b = a + 1; b < rects.Count; b++)
            {
                var ra = rects[a];
                var rb = rects[b];
                var uArea = (long)(Math.Max(ra.X1, rb.X1) - Math.Min(ra.X0, rb.X0)) *
                            (Math.Max(ra.Y1, rb.Y1) - Math.Min(ra.Y0, rb.Y0));
                var waste = uArea - (ra.Area + rb.Area);
                if (waste < bestWaste)
                {
                    bestWaste = waste;
                    bestA = a;
                    bestB = b;
                }
            }
        }
        var m0 = rects[bestA];
        var m1 = rects[bestB];
        rects[bestA] = new RasterDirtyRect(
            Math.Min(m0.X0, m1.X0), Math.Min(m0.Y0, m1.Y0),
            Math.Max(m0.X1, m1.X1), Math.Max(m0.Y1, m1.Y1));
        rects.RemoveAt(bestB);
    }

    private static void ConsolidateOverlaps(List<RasterDirtyRect> rects)
    {
        var merged = true;
        while (merged && rects.Count > 1)
        {
            merged = false;
            for (var a = 0; a < rects.Count && !merged; a++)
            {
                for (var b = a + 1; b < rects.Count; b++)
                {
                    var ra = rects[a];
                    var rb = rects[b];
                    if (ra.X0 <= rb.X1 && rb.X0 <= ra.X1 && ra.Y0 <= rb.Y1 && rb.Y0 <= ra.Y1)
                    {
                        rects[a] = new RasterDirtyRect(
                            Math.Min(ra.X0, rb.X0), Math.Min(ra.Y0, rb.Y0),
                            Math.Max(ra.X1, rb.X1), Math.Max(ra.Y1, rb.Y1));
                        rects.RemoveAt(b);
                        merged = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Managed C# box-filter mipmap updater matching Rust <c>mipmap_rgba8_rect</c> bit-for-bit.</summary>
    public static void MipmapRgba8RectManaged(int width, int height, int rectX0, int rectY0, int rectX1, int rectY1, byte[] mipBuffer)
    {
        var sx0 = Math.Clamp(rectX0, 0, width);
        var sx1 = Math.Clamp(rectX1, 0, width);
        var sy0 = Math.Clamp(rectY0, 0, height);
        var sy1 = Math.Clamp(rectY1, 0, height);
        if (sx0 >= sx1 || sy0 >= sy1) return;

        var pw = width;
        var ph = height;
        var prevOff = 0;
        while (pw > 1 || ph > 1)
        {
            var nw = Math.Max(1, pw >> 1);
            var nh = Math.Max(1, ph >> 1);
            var nextOff = prevOff + pw * ph * 4;
            var dx0 = sx0 >> 1;
            var dx1 = Math.Min(nw, (sx1 + 1) >> 1);
            var dy0 = sy0 >> 1;
            var dy1 = Math.Min(nh, (sy1 + 1) >> 1);

            for (var dy = dy0; dy < dy1; dy++)
            {
                var y0 = Math.Min(ph - 1, dy * 2);
                var y1 = Math.Min(ph - 1, y0 + 1);
                var srow0 = prevOff + y0 * pw * 4;
                var srow1 = prevOff + y1 * pw * 4;
                var drow = nextOff + dy * nw * 4;
                for (var dx = dx0; dx < dx1; dx++)
                {
                    var x0 = Math.Min(pw - 1, dx * 2);
                    var x1 = Math.Min(pw - 1, x0 + 1);
                    var p00 = srow0 + x0 * 4;
                    var p10 = srow0 + x1 * 4;
                    var p01 = srow1 + x0 * 4;
                    var p11 = srow1 + x1 * 4;
                    var doff = drow + dx * 4;
                    for (var ch = 0; ch < 4; ch++)
                    {
                        var sum = mipBuffer[p00 + ch] + mipBuffer[p10 + ch] + mipBuffer[p01 + ch] + mipBuffer[p11 + ch];
                        mipBuffer[doff + ch] = (byte)((sum + 2) >> 2);
                    }
                }
            }

            prevOff = nextOff;
            pw = nw;
            ph = nh;
            sx0 = dx0;
            sx1 = dx1;
            sy0 = dy0;
            sy1 = dy1;
        }
    }

    /// <summary>Managed C# box-filter mipmap updater matching Rust <c>mipmap_r8_rect</c> bit-for-bit.</summary>
    public static void MipmapR8RectManaged(int width, int height, int rectX0, int rectY0, int rectX1, int rectY1, byte[] mipBuffer)
    {
        var sx0 = Math.Clamp(rectX0, 0, width);
        var sx1 = Math.Clamp(rectX1, 0, width);
        var sy0 = Math.Clamp(rectY0, 0, height);
        var sy1 = Math.Clamp(rectY1, 0, height);
        if (sx0 >= sx1 || sy0 >= sy1) return;

        var pw = width;
        var ph = height;
        var prevOff = 0;
        while (pw > 1 || ph > 1)
        {
            var nw = Math.Max(1, pw >> 1);
            var nh = Math.Max(1, ph >> 1);
            var nextOff = prevOff + pw * ph;
            var dx0 = sx0 >> 1;
            var dx1 = Math.Min(nw, (sx1 + 1) >> 1);
            var dy0 = sy0 >> 1;
            var dy1 = Math.Min(nh, (sy1 + 1) >> 1);

            for (var dy = dy0; dy < dy1; dy++)
            {
                var y0 = Math.Min(ph - 1, dy * 2);
                var y1 = Math.Min(ph - 1, y0 + 1);
                var srow0 = prevOff + y0 * pw;
                var srow1 = prevOff + y1 * pw;
                var drow = nextOff + dy * nw;
                for (var dx = dx0; dx < dx1; dx++)
                {
                    var x0 = Math.Min(pw - 1, dx * 2);
                    var x1 = Math.Min(pw - 1, x0 + 1);
                    var sum = mipBuffer[srow0 + x0] + mipBuffer[srow0 + x1] + mipBuffer[srow1 + x0] + mipBuffer[srow1 + x1];
                    mipBuffer[drow + dx] = (byte)((sum + 2) >> 2);
                }
            }

            prevOff = nextOff;
            pw = nw;
            ph = nh;
            sx0 = dx0;
            sx1 = dx1;
            sy0 = dy0;
            sy1 = dy1;
        }
    }
}
