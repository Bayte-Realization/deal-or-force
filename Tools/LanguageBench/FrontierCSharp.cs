using System;
using System.Numerics;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.LanguageBench;

/// <summary>
/// Two C# rows beside the game's own <see cref="FrontierField.Build"/> (which is the reference
/// and is called as it is):
///
/// <c>Sequential</c> is the same text with every Parallel.For written as a plain loop. It is the
/// row a native port must be compared with first: the native ports are single-threaded line by
/// line ports, and comparing them with the threaded game code would be comparing threads, not
/// languages.
///
/// <c>Tuned</c> is what C# can do when somebody bothers: the same arithmetic, in the same order
/// per cell (so the bytes are identical), with scratch kept between calls, rows in blocks,
/// <see cref="Vector{T}"/> across x for the chamfer's row-above candidates and both Gaussian
/// passes. The chamfer's west/east dependency stays a serial sweep - that is the algorithm.
/// </summary>
internal static class FrontierCSharp
{
    private const float Cap = FrontierField.Cap;
    private const float Smoothing = FrontierField.Smoothing;

    // ---------------------------------------------------------------- sequential, line by line

    public static byte[] BuildSequential(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners)
    {
        var cells = width * height;
        var holders = new ushort[cells];
        for (var y = 0; y < height; y++)
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
        }

        var distance = DistancesSequential(holders, width, height);
        SmoothSequential(distance, width, height, Smoothing);

        var bytes = new byte[cells];
        const float scale = 255f / Cap;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = distance[row + x] * scale;
                bytes[row + x] = (byte)(value <= 0f ? 0 : value >= 255f ? 255 : (int)(value + 0.5f));
            }
        }
        return bytes;
    }

    private static float[] DistancesSequential(ushort[] holders, int width, int height)
    {
        var cells = width * height;
        var d = new float[cells];
        for (var y = 0; y < height; y++)
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
        }
        Chamfer(d, width, height);
        return d;
    }

    /// <summary>The game's two passes, verbatim (they are sequential in the game as well).</summary>
    private static void Chamfer(float[] d, int width, int height)
    {
        const float straight = 1f;
        const float diagonal = 1.41421356f;
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

    private static float[] Weights(float sigma, out int radius)
    {
        radius = (int)MathF.Ceiling(sigma * 3f);
        var weights = new float[radius * 2 + 1];
        var total = 0f;
        for (var i = -radius; i <= radius; i++) total += weights[i + radius] = MathF.Exp(-i * i / (2f * sigma * sigma));
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;
        return weights;
    }

    private static void SmoothSequential(float[] field, int width, int height, float sigma)
    {
        if (sigma <= 0f) return;
        var weights = Weights(sigma, out var radius);
        var across = new float[field.Length];
        for (var y = 0; y < height; y++)
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
        }
        for (var y = 0; y < height; y++)
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
        }
    }

    // ---------------------------------------------------------------- tuned

    /// <summary>Scratch a caller keeps between builds: the game would keep one per plate.</summary>
    public sealed class Scratch
    {
        public ushort[] Holders = Array.Empty<ushort>();
        public float[] Distance = Array.Empty<float>();
        public float[] Across = Array.Empty<float>();
        public float[][] RowCandidates = Array.Empty<float[]>();

        public void Ensure(int cells, int width)
        {
            if (Holders.Length < cells) Holders = new ushort[cells];
            if (Distance.Length < cells) Distance = new float[cells];
            if (Across.Length < cells) Across = new float[cells];
            if (RowCandidates.Length != 1 || RowCandidates[0].Length < width) RowCandidates = new[] { new float[width] };
        }
    }

    private const int RowBlock = 16;

    public static byte[] BuildTuned(ushort[] authoredHolders, ushort[] cellToProvince, int width, int height,
        ushort[] owners, ushort[] authoredOwners, Scratch scratch)
    {
        var cells = width * height;
        scratch.Ensure(cells, width);
        var holders = scratch.Holders;
        var d = scratch.Distance;
        var blocks = (height + RowBlock - 1) / RowBlock;

        Parallel.For(0, blocks, block =>
        {
            var y1 = Math.Min(height, (block + 1) * RowBlock);
            var ownersSpan = owners.AsSpan();
            for (var y = block * RowBlock; y < y1; y++)
            {
                var row = y * width;
                var authoredRow = authoredHolders.AsSpan(row, width);
                var provinceRow = cellToProvince.AsSpan(row, width);
                var holderRow = holders.AsSpan(row, width);
                for (var x = 0; x < authoredRow.Length; x++)
                {
                    var authored = authoredRow[x];
                    if (authored == MapBorders.Sea) { holderRow[x] = MapBorders.Sea; continue; }
                    int province = provinceRow[x];
                    if (province <= 0 || province >= ownersSpan.Length) { holderRow[x] = authored; continue; }
                    var owner = ownersSpan[province];
                    holderRow[x] = province >= authoredOwners.Length || authoredOwners[province] == owner
                        ? authored : owner == 0 ? MapBorders.Unowned : owner;
                }
            }
        });

        Parallel.For(0, blocks, block =>
        {
            var y1 = Math.Min(height, (block + 1) * RowBlock);
            for (var y = block * RowBlock; y < y1; y++)
            {
                var row = y * width;
                var here = holders.AsSpan(row, width);
                var dRow = d.AsSpan(row, width);
                ReadOnlySpan<ushort> above = y > 0 ? holders.AsSpan(row - width, width) : default;
                ReadOnlySpan<ushort> below = y + 1 < height ? holders.AsSpan(row + width, width) : default;
                for (var x = 0; x < width; x++)
                {
                    var h = here[x];
                    var frontier = here[x + 1 == width ? 0 : x + 1] != h || here[x == 0 ? width - 1 : x - 1] != h ||
                        (y > 0 && above[x] != h) || (y + 1 < height && below[x] != h);
                    dRow[x] = frontier ? 0.5f : Cap;
                }
            }
        });

        ChamferTuned(d, width, height, scratch.RowCandidates[0]);
        SmoothTuned(d, scratch.Across, width, height, Smoothing, blocks);

        var bytes = new byte[cells];
        const float scale = 255f / Cap;
        Parallel.For(0, blocks, block =>
        {
            var y1 = Math.Min(height, (block + 1) * RowBlock);
            for (var at = block * RowBlock * width; at < y1 * width; at++)
            {
                var value = d[at] * scale;
                bytes[at] = (byte)(value <= 0f ? 0 : value >= 255f ? 255 : (int)(value + 0.5f));
            }
        });
        return bytes;
    }

    /// <summary>
    /// The same two passes. A cell's best is the minimum of five candidates, each made with one
    /// addition; the minimum of exact values does not depend on the order it is taken in, so the
    /// row-above (or row-below) candidates are made for the whole row at once with vectors and
    /// only the west (east) chain is walked one cell at a time - from the values the game reads:
    /// the wrapped neighbour of the first cell is read before the sweep reaches it.
    /// </summary>
    private static void ChamferTuned(float[] d, int width, int height, float[] candidate)
    {
        const float straight = 1f;
        const float diagonal = 1.41421356f;
        var lanes = Vector<float>.Count;
        var vStraight = new Vector<float>(straight);
        var vDiagonal = new Vector<float>(diagonal);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var dRow = d.AsSpan(row, width);
            if (y > 0)
            {
                var up = d.AsSpan(row - width, width);
                candidate[0] = MathF.Min(MathF.Min(up[0] + straight, up[width - 1] + diagonal), up[1] + diagonal);
                var x = 1;
                for (; x + lanes < width; x += lanes)
                {
                    var straightUp = new Vector<float>(up.Slice(x)) + vStraight;
                    var left = new Vector<float>(up.Slice(x - 1)) + vDiagonal;
                    var right = new Vector<float>(up.Slice(x + 1)) + vDiagonal;
                    Vector.Min(Vector.Min(straightUp, left), right).CopyTo(candidate, x);
                }
                for (; x < width; x++)
                    candidate[x] = MathF.Min(MathF.Min(up[x] + straight, up[x - 1] + diagonal),
                        up[x + 1 == width ? 0 : x + 1] + diagonal);
                var west = dRow[width - 1];
                for (x = 0; x < width; x++)
                {
                    var best = dRow[x];
                    if (best <= 0.5f) { west = best; continue; }
                    best = MathF.Min(MathF.Min(best, west + straight), candidate[x]);
                    dRow[x] = best; west = best;
                }
            }
            else
            {
                var west = dRow[width - 1];
                for (var x = 0; x < width; x++)
                {
                    var best = dRow[x];
                    if (best <= 0.5f) { west = best; continue; }
                    best = MathF.Min(best, west + straight);
                    dRow[x] = best; west = best;
                }
            }
        }
        for (var y = height - 1; y >= 0; y--)
        {
            var row = y * width;
            var dRow = d.AsSpan(row, width);
            if (y + 1 < height)
            {
                var down = d.AsSpan(row + width, width);
                candidate[0] = MathF.Min(MathF.Min(down[0] + straight, down[width - 1] + diagonal), down[1] + diagonal);
                var x = 1;
                for (; x + lanes < width; x += lanes)
                {
                    var straightDown = new Vector<float>(down.Slice(x)) + vStraight;
                    var left = new Vector<float>(down.Slice(x - 1)) + vDiagonal;
                    var right = new Vector<float>(down.Slice(x + 1)) + vDiagonal;
                    Vector.Min(Vector.Min(straightDown, left), right).CopyTo(candidate, x);
                }
                for (; x < width; x++)
                    candidate[x] = MathF.Min(MathF.Min(down[x] + straight, down[x - 1] + diagonal),
                        down[x + 1 == width ? 0 : x + 1] + diagonal);
                var east = dRow[0];
                for (x = width - 1; x >= 0; x--)
                {
                    var best = dRow[x];
                    if (best <= 0.5f) { east = best; continue; }
                    best = MathF.Min(MathF.Min(MathF.Min(best, east + straight), candidate[x]), Cap);
                    dRow[x] = best; east = best;
                }
            }
            else
            {
                var east = dRow[0];
                for (var x = width - 1; x >= 0; x--)
                {
                    var best = dRow[x];
                    if (best <= 0.5f) { east = best; continue; }
                    best = MathF.Min(MathF.Min(best, east + straight), Cap);
                    dRow[x] = best; east = best;
                }
            }
        }
    }

    /// <summary>Both passes vectorised across x; each lane sums its taps in the game's order.</summary>
    private static void SmoothTuned(float[] field, float[] across, int width, int height, float sigma, int blocks)
    {
        var weights = Weights(sigma, out var radius);
        var lanes = Vector<float>.Count;
        Parallel.For(0, blocks, block =>
        {
            var y1 = Math.Min(height, (block + 1) * RowBlock);
            Span<Vector<float>> w = stackalloc Vector<float>[weights.Length];
            for (var i = 0; i < weights.Length; i++) w[i] = new Vector<float>(weights[i]);
            for (var y = block * RowBlock; y < y1; y++)
            {
                var row = y * width;
                var source = field.AsSpan(row, width);
                var target = across.AsSpan(row, width);
                var x = 0;
                for (; x < radius; x++) target[x] = HorizontalScalar(source, x, width, radius, weights);
                for (; x + lanes + radius <= width; x += lanes)
                {
                    var sum = Vector<float>.Zero;
                    for (var i = 0; i < w.Length; i++)
                        sum += new Vector<float>(source.Slice(x - radius + i)) * w[i];
                    sum.CopyTo(target.Slice(x));
                }
                for (; x < width; x++) target[x] = HorizontalScalar(source, x, width, radius, weights);
            }
        });
        Parallel.For(0, blocks, block =>
        {
            var y1 = Math.Min(height, (block + 1) * RowBlock);
            Span<Vector<float>> w = stackalloc Vector<float>[weights.Length];
            for (var i = 0; i < weights.Length; i++) w[i] = new Vector<float>(weights[i]);
            Span<int> lines = stackalloc int[weights.Length];
            for (var y = block * RowBlock; y < y1; y++)
            {
                for (var i = -radius; i <= radius; i++) lines[i + radius] = Math.Clamp(y + i, 0, height - 1) * width;
                var row = y * width;
                var x = 0;
                for (; x + lanes <= width; x += lanes)
                {
                    var sum = Vector<float>.Zero;
                    for (var i = 0; i < w.Length; i++)
                        sum += new Vector<float>(across, lines[i] + x) * w[i];
                    sum.CopyTo(field, row + x);
                }
                for (; x < width; x++)
                {
                    var sum = 0f;
                    for (var i = 0; i < weights.Length; i++) sum += across[lines[i] + x] * weights[i];
                    field[row + x] = sum;
                }
            }
        });
    }

    private static float HorizontalScalar(ReadOnlySpan<float> source, int x, int width, int radius, float[] weights)
    {
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var column = x + i;
            if (column < 0) column += width; else if (column >= width) column -= width;
            sum += source[column] * weights[i + radius];
        }
        return sum;
    }
}
