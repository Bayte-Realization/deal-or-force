using System;
using System.Threading.Tasks;
using DealOrForce.Simulation.Kernels;

namespace DealOrForce.Simulation;

/// <summary>
/// How far every cell of the map is from the nearest frontier, as a picture the map shader can
/// filter.
///
/// Close in, the map draws a border by rebuilding it from the block of country ids around each
/// pixel, which is exact and smooth once a cell of the raster is several pixels across. From
/// further away a cell is a pixel or less, and the same rebuilding is asked once per pixel at
/// one point: the border comes out as the raster's own staircase, a pixel high, and wide and
/// dark where the ink of two steps overlaps - the "pixelated, blurred, fat" frontier the owner
/// pointed at on the middle and far zooms.
///
/// This field is the other half. For each cell it holds the distance, in cells, from the cell's
/// centre to the nearest edge where the holder changes - another country, or the sea - capped,
/// and smoothed by a Gaussian of about a cell so the staircase is rounded off before it is
/// stored. Stored as one byte a cell and mipmapped, it is read through the GPU's filtering: a
/// pixel that covers several cells gets their average rather than one of them, and a line drawn
/// where the distance is under a pixel or so is a soft, even stroke at any zoom.
///
/// The holder of a cell is <see cref="MapBorders.CurrentHolder"/>, the rule the far plate and the
/// close-up shader both use, so the stroke drawn from this field lies on the boundary of the
/// colours drawn from the others. Built when the map is built and again whenever the plate is
/// repainted, on the same worker, from the same owners.
///
/// Pure arrays in and out: it runs on a worker thread and under the test stand.
/// </summary>
public static class FrontierField
{
    /// <summary>Distances are kept up to this many cells; everything further is "inland".</summary>
    public const float Cap = 20f;

    /// <summary>How much the Gaussian rounds the staircase off, in cells.</summary>
    public const float Smoothing = 1.0f;

    // Reusable LOH scratch buffers eliminate ~174 MiB of LOH allocations on every wartime repaint.
    // Protected by ScratchLock so concurrent calls stay 100% thread-safe and bit-identical.
    private static readonly object ScratchLock = new();
    private static ushort[] _poolHolders;
    private static float[] _poolDistance;
    private static float[] _poolAcross;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ushort[], ProvinceRasterBounds> BoundsCache = new();
    [ThreadStatic] private static System.Collections.Generic.List<RasterDirtyRect> _dirtyRectScratch;

    /// <summary>Exact dependency padding around a changed province (Cap + Gaussian radius + safety margin).</summary>
    public const int DirtyPadCells = 26;

    /// <summary>
    /// Incrementally updates a persistent mipmapped R8 frontier buffer (<c>level 0</c> + full mipmap chain)
    /// touching only the bounding boxes of provinces whose owners changed since <paramref name="prevOwners"/>.
    /// Produces bit-exact level-0 bytes matching full <see cref="Build"/>.
    /// </summary>
    public static byte[] BuildDirtyMipmappedInto(
        ushort[] authoredHolders,
        ushort[] cellToProvince,
        int width,
        int height,
        ushort[] prevOwners,
        ushort[] owners,
        ushort[] authoredOwners,
        bool forceFull,
        ref byte[] mipBuffer,
        out bool usedIncremental)
    {
        if (authoredHolders is null || cellToProvince is null) throw new ArgumentNullException(nameof(authoredHolders));
        if (width <= 0 || height <= 0) throw new ArgumentException("The field dimensions must be positive.");
        var cells = checked(width * height);
        if (authoredHolders.Length < cells || cellToProvince.Length < cells)
            throw new ArgumentException("The grids are smaller than the field asked for.");

        var mipBytes = NativeRasterBake.ComputeMipmapChainBytes(width, height, 1);
        var needFull = forceFull || mipBuffer is null || mipBuffer.Length != mipBytes || prevOwners is null;
        if (mipBuffer is null || mipBuffer.Length != mipBytes)
            mipBuffer = new byte[mipBytes];

        var rects = _dirtyRectScratch ??= new System.Collections.Generic.List<RasterDirtyRect>(16);
        rects.Clear();
        if (!needFull)
        {
            var bounds = BoundsCache.GetValue(cellToProvince, key => ProvinceRasterBounds.Build(key, width, height));
            var any = bounds.ComputeDirtyRects(prevOwners, owners, DirtyPadCells, rects, out needFull, disallowDateLineEdge: true);
            if (!any && !needFull)
            {
                usedIncremental = true;
                return mipBuffer;
            }
        }

        if (!needFull && rects.Count > 0)
        {
            var allNative = true;
            for (var i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                if (!NativeRasterBake.TryBakeFrontierRectInto(
                        authoredHolders, cellToProvince, width, height, owners, authoredOwners,
                        Cap, Smoothing, r.X0, r.Y0, r.X1, r.Y1, mipBuffer))
                {
                    allNative = false;
                    break;
                }
            }
            if (allNative)
            {
                for (var i = 0; i < rects.Count; i++)
                {
                    var r = rects[i];
                    if (!NativeRasterBake.TryMipmapR8Rect(width, height, r.X0, r.Y0, r.X1, r.Y1, mipBuffer))
                        ProvinceRasterBounds.MipmapR8RectManaged(width, height, r.X0, r.Y0, r.X1, r.Y1, mipBuffer);
                }
                usedIncremental = true;
                return mipBuffer;
            }
        }

        // Full rebuild fallback (first bake, date-line wrap, >45% dirty, or pure-C# mode).
        if (!NativeKernelsLoader.EnableRasterDefault ||
            !NativeRasterBake.TryBakeInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, Cap, Smoothing, mipBuffer))
        {
            BuildManagedInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, mipBuffer);
        }
        if (!NativeRasterBake.TryMipmapR8Rect(width, height, 0, 0, width, height, mipBuffer))
            ProvinceRasterBounds.MipmapR8RectManaged(width, height, 0, 0, width, height, mipBuffer);
        usedIncremental = false;
        return mipBuffer;
    }

    private static ParallelOptions BoundedParallel => new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
    };

    /// <summary>
    /// The field for these owners, <paramref name="width"/> by <paramref name="height"/> bytes,
    /// row after row; a byte is the distance times 255 / <see cref="Cap"/>. East and west wrap,
    /// because the world does; north and south stop.
    /// </summary>
    public static byte[] Build(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners)
    {
        byte[] bytes = null;
        return BuildInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, ref bytes);
    }

    /// <summary>
    /// Variant of <see cref="Build"/> that writes into a caller-supplied reusable byte array
    /// (allocated/resized on demand) so wartime frontier repaints avoid repeated ~16.6 MB LOH allocations.
    /// </summary>
    public static byte[] BuildInto(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners, ref byte[] reusableBytes)
    {
        if (authoredHolders is null || cellToProvince is null) throw new ArgumentNullException(nameof(authoredHolders));
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The field dimensions must be positive.");
        var cells = checked(width * height);
        if (authoredHolders.Length < cells || cellToProvince.Length < cells)
            throw new ArgumentException("The grids are smaller than the field asked for.");

        if (reusableBytes is null || reusableBytes.Length != cells)
            reusableBytes = new byte[cells];

        if (NativeKernelsLoader.EnableRasterDefault &&
            NativeRasterBake.TryBakeInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, Cap, Smoothing, reusableBytes))
            return reusableBytes;

        BuildManagedInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, reusableBytes);
        return reusableBytes;
    }

    internal static byte[] BuildManaged(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners)
    {
        if (authoredHolders is null || cellToProvince is null) throw new ArgumentNullException(nameof(authoredHolders));
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The field dimensions must be positive.");
        var cells = checked(width * height);
        var bytes = new byte[cells];
        BuildManagedInto(authoredHolders, cellToProvince, width, height, owners, authoredOwners, bytes);
        return bytes;
    }

    private static void BuildManagedInto(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners, byte[] bytes)
    {
        if (authoredHolders is null || cellToProvince is null) throw new ArgumentNullException(nameof(authoredHolders));
        var cells = width * height;
        if (authoredHolders.Length < cells || cellToProvince.Length < cells || bytes is null || bytes.Length < cells)
            throw new ArgumentException("The grids are smaller than the field asked for.");

        var opts = BoundedParallel;
        lock (ScratchLock)
        {
            if (_poolHolders is null || _poolHolders.Length < cells) _poolHolders = new ushort[cells];
            if (_poolDistance is null || _poolDistance.Length < cells) _poolDistance = new float[cells];
            if (_poolAcross is null || _poolAcross.Length < cells) _poolAcross = new float[cells];
            var holders = _poolHolders;
            var distance = _poolDistance;
            var across = _poolAcross;

            Parallel.For(0, height, opts, y =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var cell = row + x;
                    var authored = authoredHolders[cell];
                    holders[cell] = authored == MapBorders.Sea
                        ? MapBorders.Sea
                        : MapBorders.CurrentHolder(authored, cellToProvince[cell], owners, authoredOwners);
                }
            });

            ComputeDistancesInto(holders, width, height, distance, opts);
            SmoothInto(distance, across, width, height, Smoothing, opts);

            const float scale = 255f / Cap;
            Parallel.For(0, height, opts, y =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var value = distance[row + x] * scale;
                    bytes[row + x] = (byte)(value <= 0f ? 0 : value >= 255f ? 255 : (int)(value + 0.5f));
                }
            });
        }
    }

    /// <summary>
    /// Distance from each cell's centre to the nearest edge between two holders: half a cell for
    /// a cell on the frontier, then grown outwards by a two-pass chamfer (one cell straight, the
    /// root of two across a corner), capped at <see cref="Cap"/>.
    /// </summary>
    internal static float[] Distances(ushort[] holders, int width, int height)
    {
        var d = new float[width * height];
        ComputeDistancesInto(holders, width, height, d, BoundedParallel);
        return d;
    }

    private static void ComputeDistancesInto(ushort[] holders, int width, int height, float[] d, ParallelOptions opts)
    {
        Parallel.For(0, height, opts, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var cell = row + x;
                var here = holders[cell];
                var frontier =
                    holders[row + (x + 1 == width ? 0 : x + 1)] != here ||
                    holders[row + (x == 0 ? width - 1 : x - 1)] != here ||
                    (y > 0 && holders[cell - width] != here) ||
                    (y + 1 < height && holders[cell + width] != here);
                d[cell] = frontier ? 0.5f : Cap;
            }
        });

        const float straight = 1f;
        const float diagonal = 1.41421356f;
        // Forward: from the north-west. A row reads only the row above, already finished, and
        // its own west neighbour; the first cell of a row also takes the last of the same row,
        // because the world wraps.
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var cell = row + x;
                var best = d[cell];
                if (best <= 0.5f) continue;
                var west = row + (x == 0 ? width - 1 : x - 1);
                best = MathF.Min(best, d[west] + straight);
                if (y > 0)
                {
                    var up = cell - width;
                    best = MathF.Min(best, d[up] + straight);
                    best = MathF.Min(best, d[row - width + (x == 0 ? width - 1 : x - 1)] + diagonal);
                    best = MathF.Min(best, d[row - width + (x + 1 == width ? 0 : x + 1)] + diagonal);
                }
                d[cell] = best;
            }
        }
        // Backward: from the south-east.
        for (var y = height - 1; y >= 0; y--)
        {
            var row = y * width;
            for (var x = width - 1; x >= 0; x--)
            {
                var cell = row + x;
                var best = d[cell];
                if (best <= 0.5f) continue;
                var east = row + (x + 1 == width ? 0 : x + 1);
                best = MathF.Min(best, d[east] + straight);
                if (y + 1 < height)
                {
                    var down = cell + width;
                    best = MathF.Min(best, d[down] + straight);
                    best = MathF.Min(best, d[row + width + (x == 0 ? width - 1 : x - 1)] + diagonal);
                    best = MathF.Min(best, d[row + width + (x + 1 == width ? 0 : x + 1)] + diagonal);
                }
                d[cell] = MathF.Min(best, Cap);
            }
        }
    }

    /// <summary>A separable Gaussian, east-west wrapping, north-south clamped. In place.</summary>
    internal static void Smooth(float[] field, int width, int height, float sigma)
    {
        if (sigma <= 0f) return;
        SmoothInto(field, new float[field.Length], width, height, sigma, BoundedParallel);
    }

    private static void SmoothInto(float[] field, float[] across, int width, int height, float sigma, ParallelOptions opts)
    {
        if (sigma <= 0f) return;
        var radius = (int)MathF.Ceiling(sigma * 3f);
        var weights = new float[radius * 2 + 1];
        var total = 0f;
        for (var i = -radius; i <= radius; i++) total += weights[i + radius] = MathF.Exp(-i * i / (2f * sigma * sigma));
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;

        Parallel.For(0, height, opts, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                {
                    var column = x + i;
                    if (column < 0) column += width; else if (column >= width) column -= width;
                    sum += field[row + column] * weights[i + radius];
                }
                across[row + x] = sum;
            }
        });
        Parallel.For(0, height, opts, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                {
                    var line = Math.Clamp(y + i, 0, height - 1);
                    sum += across[line * width + x] * weights[i + radius];
                }
                field[row + x] = sum;
            }
        });
    }
}
