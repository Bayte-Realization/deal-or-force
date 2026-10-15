using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Deterministically subdivides administrative states into the same tactical map tier
/// used by the source project. Province geometry is derived from moddable state/control
/// rasters, never from presentation colours, so rendering and selection cannot disagree.
/// </summary>
public sealed class ProvinceTopology
{
    // Roughly forty thousand tactical provinces at the shipped 5760x2880 source
    // resolution. This stays below the 16-bit map-id contract while giving fronts,
    // manoeuvre and occupations a Paradox-like operational scale.
    public const int TargetProvinceCount = 40_000;
    public const int CellsPerProvince = 162;
    public const int DisputedCellsPerProvince = 54;

    /// <summary>
    /// A province grown from fewer cells than this, beside a province of the same region and
    /// owner, is a leftover of the growth rather than a place - see <see cref="AbsorbSlivers"/>.
    /// </summary>
    public const int MinimumProvinceCells = 24;

    /// <summary>
    /// Which province each cell of the source raster belongs to.
    ///
    /// Sixteen and a half million cells, so the element type is the whole cost: as an int
    /// this array was sixty-six megabytes of the game's heap, which on a phone is most of
    /// the budget for everything. There are thirty-seven thousand provinces and the
    /// largest id will not pass sixty-five thousand for as long as the map is a raster of
    /// this size, so a ushort holds it and the array is thirty-three.
    /// </summary>
    public ushort[] CellToProvince { get; }
    public ushort[] ProvinceToState { get; }
    public ushort[] ProvinceToNation { get; }

    /// <summary>
    /// The borders as the scenario drew them, once somebody has asked to remember them.
    ///
    /// A topology costs seconds and a great deal of memory to build, so it is built once a
    /// session and handed to whichever campaign is running. The simulation then writes its
    /// own borders straight into <see cref="ProvinceToNation"/> and
    /// <see cref="ProvinceToState"/> - an annexation moves provinces between countries, a
    /// split moves them between regions - so by the time a player is back at the menu the
    /// map is not the authored map, it is whatever their campaign did to it. A second
    /// campaign handed that opens with the first one's conquests already made and its
    /// regions already cut the way the first one's wars cut them.
    /// </summary>
    private ushort[] _authoredNation;

    private ushort[] _authoredState;

    public int[] ProvinceCenterCell { get; }
    public int[][] Neighbors { get; }
    public int Width { get; }
    public int Height { get; }
    public int EdgeCount { get; }
    public int ProvinceCount => ProvinceToState.Length - 1;
    public byte[] RiverIntensityByProvince { get; private set; }
    public bool[] LakeAccessByProvince { get; private set; }

    /// <summary>
    /// Whether a province is water rather than ground.
    ///
    /// A lake is a region like any other and its provinces are drawn and owned like any
    /// other - but nothing marches through them. The flag lives here because this is where
    /// movement is answered, and a rule that has to be remembered at every call site is a
    /// rule that will be forgotten at one of them.
    /// </summary>
    public bool[] WaterByProvince { get; private set; }
    public int RiverCrossingCount => _riverCrossings.Count;
    public TerrainDefinition[] TerrainByProvince { get; private set; } = Array.Empty<TerrainDefinition>();
    public byte[] TerrainSurfaceSlots { get; internal set; } = Array.Empty<byte>();
    public TerrainDefinition TerrainAt(int province) => province > 0 && province < TerrainByProvince.Length
        ? TerrainByProvince[province] : TerrainCatalog.Get("plains");
    public void AttachTerrain() => TerrainByProvince = TerrainCatalog.Bind(this);
    private readonly Dictionary<long, byte> _riverCrossings = new();

    private ProvinceTopology(
        ushort[] cellToProvince,
        ushort[] provinceToState,
        ushort[] provinceToNation,
        int[] provinceCenterCell,
        int[][] neighbors,
        int width,
        int height)
    {
        CellToProvince = cellToProvince;
        ProvinceToState = provinceToState;
        ProvinceToNation = provinceToNation;
        ProvinceCenterCell = provinceCenterCell;
        Neighbors = neighbors;
        Width = width;
        Height = height;
        var directedEdges = 0;
        foreach (var list in neighbors) directedEdges += list.Length;
        EdgeCount = directedEdges / 2;
        RiverIntensityByProvince = new byte[ProvinceCount + 1];
        LakeAccessByProvince = new bool[ProvinceCount + 1];
        WaterByProvince = new bool[ProvinceCount + 1];
        for (var province = 1; province <= ProvinceCount; province++)
            WaterByProvince[province] = LakeRegions.IsLakeRegion(ProvinceToState[province]);
    }

    public static ProvinceTopology Build(
        ushort[] states,
        ushort[] control,
        int width,
        int height,
        Func<int, bool> isDisputed)
    {
        if (states.Length != width * height || control.Length != states.Length)
            throw new ArgumentException("Political rasters must match the declared dimensions.");

        var disputedStates = new HashSet<ushort>();
        for (var cell = 0; cell < states.Length; cell++)
            if (control[cell] != 0 && states[cell] != 0 && isDisputed(cell))
                disputedStates.Add(states[cell]);

        var cellToProvince = new int[states.Length];
        Array.Fill(cellToProvince, -1);
        var provinceToState = new List<ushort> { 0 };
        var provinceToNation = new List<ushort> { 0 };
        var frontier = new PriorityQueue<int, int>();
        var stride = states.Length / 2 + 1;
        if ((stride & 1) == 0) stride++;
        while (GreatestCommonDivisor(stride, states.Length) != 1) stride += 2;
        var cursor = 0;

        // A full-cycle modular walk scatters province seeds over the whole planet.
        // This removes the visible raster-scan bands while remaining deterministic.
        for (var visited = 0; visited < states.Length; visited++, cursor = (cursor + stride) % states.Length)
        {
            var start = cursor;
            var state = states[start];
            // A lake has a region and no owner (LakeRegions), and it still has provinces: the
            // game reads them from provinces.png, and a compile that grew none left every lake
            // uncoloured there and failed its own round trip.
            if (state == 0 || control[start] == 0 && !LakeRegions.IsLakeRegion(state) || cellToProvince[start] >= 0)
                continue;

            var province = provinceToState.Count;
            provinceToState.Add(state);
            provinceToNation.Add(control[start]);
            var baseline = disputedStates.Contains(state) ? DisputedCellsPerProvince : CellsPerProvince;
            // Keep neighbouring provinces recognisably different without turning
            // their borders into tendrils.  The resulting cells are a relaxed,
            // irregular Voronoi-like layer: mostly convex, occasionally elongated,
            // and deterministic so province IDs remain stable in save games.
            // Deliberately use a broad area distribution. A narrow +/-25 % band made
            // the 40k layer read as a repeating field of fish-scales at strategic
            // zoom even though every border was technically irregular.
            var minimumCap = Math.Max(18, (int)MathF.Round(baseline * 0.43f));
            var maximumCap = Math.Max(minimumCap + 8, (int)MathF.Round(baseline * 1.72f));
            var cap = minimumCap + (int)(Hash((uint)province) % (uint)(maximumCap - minimumCap + 1));
            var seedX = start % width;
            var seedY = start / width;
            var owner = control[start];
            var shapeHash = Hash((uint)(province * 747796405));
            var angle = (shapeHash & 4095u) / 4095f * MathF.PI;
            var axisRatio = 0.44f + ((shapeHash >> 12) & 1023u) / 1023f * 1.92f;
            var skew = (((shapeHash >> 22) & 255u) / 255f - 0.5f) * 0.68f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var count = 0;
            frontier.Clear();
            frontier.Enqueue(start, 0);

            while (frontier.Count > 0 && count < cap)
            {
                var cell = frontier.Dequeue();
                if (cellToProvince[cell] >= 0 || states[cell] != state || control[cell] != owner)
                    continue;
                cellToProvince[cell] = province;
                count++;
                var x = cell % width;
                var y = cell / width;
                for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || y + oy < 0 || y + oy >= height) continue;
                    var nextX = (x + ox + width) % width;
                    var next = (y + oy) * width + nextX;
                    if (cellToProvince[next] >= 0 || states[next] != state || control[next] != owner) continue;

                    var dx = Math.Abs(nextX - seedX);
                    dx = Math.Min(dx, width - dx);
                    var dy = y + oy - seedY;
                    var localX = dx * MathF.Sign(nextX - seedX == 0 ? 1 : nextX - seedX);
                    if (Math.Abs(nextX - seedX) > width / 2) localX = -localX;
                    var localY = dy;
                    var major = localX * cos + localY * sin;
                    var minor = -localX * sin + localY * cos + major * skew;
                    var distance = major * major / axisRatio + minor * minor * axisRatio;

                    // Low-amplitude coherent noise prevents repeated geometric cells.
                    // It is deliberately weaker than the elliptical distance field,
                    // keeping borders readable for front lines and selection.
                    var coarseKey = (uint)((nextX / 3) * 73856093 ^ ((y + oy) / 3) * 19349663 ^ province * 83492791);
                    var coarseNoise = ((int)(Hash(coarseKey) & 2047u) - 1024) * 2;
                    var fineNoise = (int)(Hash((uint)(next ^ province * 16777619)) & 2047u) - 1024;
                    var diagonalPenalty = ox != 0 && oy != 0 ? (int)((shapeHash >> 8) & 85u) : 0;
                    frontier.Enqueue(next, (int)(distance * 256f) + coarseNoise + fineNoise / 2 + diagonalPenalty);
                }
            }
        }

        BuildGraph(cellToProvince, provinceToState.Count, width, height, out var centers, out var neighbors);
        return new ProvinceTopology(
            Pack(cellToProvince),
            provinceToState.ToArray(),
            provinceToNation.ToArray(),
            centers,
            neighbors,
            width,
            height);
    }

    public static ProvinceTopology FromColorRaster(
        ushort[] states,
        ushort[] control,
        int width,
        int height,
        RgbDataMap raster,
        IReadOnlyDictionary<int, int> colorToProvince)
    {
        if (states.Length != width * height || control.Length != states.Length)
            throw new ArgumentException("Political rasters must match the declared dimensions.");
        if (raster.Width != width || raster.Height != height || raster.Pixels.Length != width * height * 3)
            throw new InvalidDataException($"provinces.png must be exactly {width}x{height} RGB pixels.");

        var cellToProvince = new int[states.Length];
        Array.Fill(cellToProvince, -1);
        var maxProvince = colorToProvince.Count == 0 ? 0 : colorToProvince.Values.Max();
        if (maxProvince <= 0 || maxProvince > ushort.MaxValue)
            throw new InvalidDataException("Province IDs must be in the range 1..65535.");
        var provinceToState = new ushort[maxProvince + 1];
        var provinceToNation = new ushort[maxProvince + 1];
        var seen = new bool[maxProvince + 1];
        for (var cell = 0; cell < states.Length; cell++)
        {
            var read = cell * 3;
            var color = (raster.Pixels[read] << 16) | (raster.Pixels[read + 1] << 8) | raster.Pixels[read + 2];
            // Mapped ground is a cell with a region under it and an owner over it - or a lake,
            // which has a region and, since lakes stopped belonging to whoever held the longest
            // shore, no owner. Without the second half every lake province read as ocean.
            var land = states[cell] != 0 && (control[cell] != 0 || LakeRegions.IsLakeRegion(states[cell]));
            if (!land)
            {
                if (color != 0) throw new InvalidDataException($"provinces.png colours ocean pixel {cell}.");
                continue;
            }
            if (color == 0 || !colorToProvince.TryGetValue(color, out var province))
                throw new InvalidDataException($"Land pixel {cell} uses unknown province colour #{color:X6}.");
            cellToProvince[cell] = province;
            if (!seen[province])
            {
                seen[province] = true;
                provinceToState[province] = states[cell];
                provinceToNation[province] = control[cell];
            }
            else if (provinceToState[province] != states[cell] || provinceToNation[province] != control[cell])
                throw new InvalidDataException($"Province {province} crosses an administrative or national border at pixel {cell}.");
        }
        for (var province = 1; province <= maxProvince; province++)
            if (!seen[province]) throw new InvalidDataException($"Province {province} from definition.csv has no pixels.");

        BuildGraph(cellToProvince, maxProvince + 1, width, height, out var centers, out var neighbors);
        return new ProvinceTopology(Pack(cellToProvince), provinceToState, provinceToNation,
            centers, neighbors, width, height);
    }

    /// <summary>
    /// Turns the seed-growth fallback into a restrained anisotropic Voronoi-like map
    /// before it is baked to provinces.png. IDs, political/state borders and one seed
    /// pixel per province remain fixed; only internal tactical borders are relaxed.
    /// </summary>
    /// <summary>
    /// A copy that shares the expensive parts and owns its own political arrays. Used to
    /// try a political override without touching the map the campaign is running on.
    /// </summary>
    public ProvinceTopology CloneForOverrides() =>
        new(CellToProvince, (ushort[])ProvinceToState.Clone(), (ushort[])ProvinceToNation.Clone(),
            ProvinceCenterCell, Neighbors, Width, Height)
        { TerrainByProvince = (TerrainDefinition[])TerrainByProvince.Clone(), TerrainSurfaceSlots = TerrainSurfaceSlots };

    internal ProvinceTopology WithGeometry(int[] cells, ushort[] regions, ushort[] nations)
    {
        BuildGraph(cells, ProvinceCount + 1, Width, Height, out var centres, out var neighbours);
        if (centres.Skip(1).Any(c => c < 0)) throw new InvalidDataException("Border repair lost a province ID.");
        return new(Pack(cells), regions, nations, centres, neighbours, Width, Height);
    }

    /// <summary>
    /// The growth and relaxation algorithms need a sentinel for "not assigned yet" and use
    /// -1 for it, so they work in int and the result is packed on the way out. The wide
    /// array exists only while the map is being compiled; what the game keeps is the
    /// narrow one.
    /// </summary>
    private static ushort[] Pack(int[] cells)
    {
        var packed = new ushort[cells.Length];
        for (var cell = 0; cell < cells.Length; cell++)
        {
            var province = cells[cell];
            packed[cell] = province <= 0 || province > ushort.MaxValue ? (ushort)0 : (ushort)province;
        }
        return packed;
    }

    private static int[] Unpack(ushort[] cells)
    {
        var wide = new int[cells.Length];
        for (var cell = 0; cell < cells.Length; cell++) wide[cell] = cells[cell];
        return wide;
    }

    /// <summary>
    /// Fold the leftovers of growth into their neighbours.
    ///
    /// Seeds are scattered over the planet and each province grows until it reaches its size or
    /// runs out of its region; whatever pocket of a region the growth walks around is seeded
    /// later and becomes a province of its own. One in thirteen provinces came out under twenty
    /// cells and sixteen hundred under five - a single cell south of Gaza was a province with a
    /// name and an owner. Each such pocket joins the neighbour of the same region and owner it
    /// shares the longest border with, smallest first, and the provinces are numbered again in
    /// the order they had. A pocket with no such neighbour - an islet, an exclave - stays.
    /// </summary>
    public ProvinceTopology AbsorbSlivers(int minimumCells = MinimumProvinceCells)
    {
        if (minimumCells <= 1) return this;
        var cells = Unpack(CellToProvince);
        var count = ProvinceCount;
        var members = new List<int>[count + 1];
        for (var province = 0; province <= count; province++) members[province] = new List<int>();
        for (var cell = 0; cell < cells.Length; cell++)
            if (cells[cell] > 0) members[cells[cell]].Add(cell);
        var parent = new int[count + 1];
        for (var province = 0; province <= count; province++) parent[province] = province;
        int Find(int province)
        {
            while (parent[province] != province)
            {
                parent[province] = parent[parent[province]];
                province = parent[province];
            }
            return province;
        }

        var order = new List<int>(count);
        for (var province = 1; province <= count; province++) order.Add(province);
        order.Sort((left, right) =>
        {
            var bySize = members[left].Count.CompareTo(members[right].Count);
            return bySize != 0 ? bySize : left.CompareTo(right);
        });

        var shared = new Dictionary<int, int>();
        var absorbed = 0;
        foreach (var province in order)
        {
            var root = Find(province);
            if (root != province || members[root].Count >= minimumCells) continue;
            shared.Clear();
            foreach (var cell in members[root])
            {
                var x = cell % Width;
                var y = cell / Width;
                for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || y + oy < 0 || y + oy >= Height) continue;
                    var next = (y + oy) * Width + (x + ox + Width) % Width;
                    var owner = cells[next];
                    if (owner <= 0) continue;
                    var other = Find(owner);
                    if (other == root || ProvinceToState[other] != ProvinceToState[root] ||
                        ProvinceToNation[other] != ProvinceToNation[root]) continue;
                    shared[other] = shared.GetValueOrDefault(other) + 1;
                }
            }
            var best = -1;
            var bestShared = 0;
            foreach (var (other, border) in shared)
            {
                if (border > bestShared ||
                    border == bestShared && best > 0 && (members[other].Count > members[best].Count ||
                                                         members[other].Count == members[best].Count && other < best))
                {
                    best = other;
                    bestShared = border;
                }
            }
            if (best < 0) continue;
            parent[root] = best;
            members[best].AddRange(members[root]);
            members[root].Clear();
            absorbed++;
        }
        if (absorbed == 0) return this;

        // Numbered again in the order the survivors had, so the map keeps its shape of ids.
        var renumber = new int[count + 1];
        var states = new List<ushort> { 0 };
        var nations = new List<ushort> { 0 };
        for (var province = 1; province <= count; province++)
        {
            if (Find(province) != province) continue;
            renumber[province] = states.Count;
            states.Add(ProvinceToState[province]);
            nations.Add(ProvinceToNation[province]);
        }
        for (var cell = 0; cell < cells.Length; cell++)
            if (cells[cell] > 0) cells[cell] = renumber[Find(cells[cell])];
        BuildGraph(cells, states.Count, Width, Height, out var centers, out var neighbors);
        return new ProvinceTopology(Pack(cells), states.ToArray(), nations.ToArray(), centers, neighbors, Width, Height);
    }

    public ProvinceTopology RelaxShapes(ushort[] states, ushort[] control, int iterations = 3)
    {
        if (states.Length != CellToProvince.Length || control.Length != states.Length)
            throw new ArgumentException("Political rasters must match province topology.");
        iterations = Math.Clamp(iterations, 0, 16);
        if (iterations == 0) return this;

        var current = Unpack(CellToProvince);
        var next = new int[current.Length];
        var cosines = new float[ProvinceCount + 1];
        var sines = new float[ProvinceCount + 1];
        var ratios = new float[ProvinceCount + 1];
        var skews = new float[ProvinceCount + 1];
        for (var province = 1; province <= ProvinceCount; province++)
        {
            var shape = Hash((uint)(province * 747796405));
            var angle = (shape & 4095u) / 4095f * MathF.PI;
            cosines[province] = MathF.Cos(angle);
            sines[province] = MathF.Sin(angle);
            ratios[province] = 0.50f + ((shape >> 12) & 1023u) / 1023f * 1.38f;
            skews[province] = (((shape >> 22) & 255u) / 255f - 0.5f) * 0.42f;
        }

        Span<int> candidates = stackalloc int[9];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Copy(current, next, current.Length);
            for (var cell = 0; cell < current.Length; cell++)
            {
                var province = current[cell];
                if (province <= 0 || ProvinceCenterCell[province] == cell) continue;
                var x = cell % Width;
                var y = cell / Width;
                var count = 1;
                candidates[0] = province;
                var boundary = false;
                for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    if ((ox == 0 && oy == 0) || y + oy < 0 || y + oy >= Height) continue;
                    var nx = (x + ox + Width) % Width;
                    var candidate = current[(y + oy) * Width + nx];
                    if (candidate <= 0 || candidate == province) continue;
                    boundary = true;
                    if (ProvinceToState[candidate] != states[cell] || ProvinceToNation[candidate] != control[cell]) continue;
                    var duplicate = false;
                    for (var index = 0; index < count; index++)
                        if (candidates[index] == candidate) { duplicate = true; break; }
                    if (!duplicate) candidates[count++] = candidate;
                }
                if (!boundary || count == 1) continue;

                var best = province;
                var bestCost = ShapeCost(province, x, y, cosines, sines, ratios, skews);
                for (var index = 1; index < count; index++)
                {
                    var candidate = candidates[index];
                    var cost = ShapeCost(candidate, x, y, cosines, sines, ratios, skews);
                    if (cost < bestCost || Math.Abs(cost - bestCost) < 0.001f && candidate < best)
                    {
                        best = candidate;
                        bestCost = cost;
                    }
                }
                next[cell] = best;
            }
            (current, next) = (next, current);
        }

        RepairDetachedShapeIslands(current);

        BuildGraph(current, ProvinceCount + 1, Width, Height, out var centers, out var neighbors);
        for (var province = 1; province <= ProvinceCount; province++)
            if (centers[province] < 0) throw new InvalidDataException($"Province {province} vanished during shape relaxation.");
        return new ProvinceTopology(Pack(current), (ushort[])ProvinceToState.Clone(), (ushort[])ProvinceToNation.Clone(),
            centers, neighbors, Width, Height);
    }

    private float ShapeCost(int province, int x, int y, float[] cosines, float[] sines, float[] ratios, float[] skews)
    {
        var seed = ProvinceCenterCell[province];
        var seedX = seed % Width;
        var seedY = seed / Width;
        var dx = x - seedX;
        if (dx > Width / 2) dx -= Width;
        else if (dx < -Width / 2) dx += Width;
        var dy = y - seedY;
        var major = dx * cosines[province] + dy * sines[province];
        var minor = -dx * sines[province] + dy * cosines[province] + major * skews[province];
        var ratio = ratios[province];
        var distance = major * major / ratio + minor * minor * ratio;
        var noiseKey = (uint)((x / 5) * 73856093 ^ (y / 5) * 19349663 ^ province * 83492791);
        var noise = ((Hash(noiseKey) & 1023u) / 1023f - 0.5f) * 2.4f;
        return distance + noise;
    }

    private void RepairDetachedShapeIslands(int[] cells)
    {
        var visited = new bool[cells.Length];
        var queue = new Queue<int>();
        var component = new List<int>();
        var neighbors = new Dictionary<int, int>();
        for (var start = 0; start < cells.Length; start++)
        {
            var province = cells[start];
            if (province <= 0 || visited[start]) continue;
            var containsSeed = false;
            component.Clear();
            neighbors.Clear();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                component.Add(cell);
                if (cell == ProvinceCenterCell[province]) containsSeed = true;
                var x = cell % Width;
                var y = cell / Width;
                Visit(y * Width + (x == 0 ? Width - 1 : x - 1));
                Visit(y * Width + (x + 1 == Width ? 0 : x + 1));
                if (y > 0) Visit(cell - Width);
                if (y + 1 < Height) Visit(cell + Width);
            }
            if (containsSeed) continue;
            var replacement = -1;
            var bestContact = -1;
            foreach (var pair in neighbors)
                if (pair.Value > bestContact || pair.Value == bestContact && pair.Key < replacement)
                {
                    replacement = pair.Key;
                    bestContact = pair.Value;
                }
            // A component with no same-state land contact is a legitimate offshore
            // islet/archipelago fragment. It cannot be reassigned without changing
            // political geography, so preserve it under the parent coastal province.
            if (replacement <= 0) continue;
            foreach (var cell in component) cells[cell] = replacement;

            void Visit(int next)
            {
                var candidate = cells[next];
                if (candidate == province)
                {
                    if (visited[next]) return;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
                else if (candidate > 0 && ProvinceToState[candidate] == ProvinceToState[province] &&
                         ProvinceToNation[candidate] == ProvinceToNation[province])
                    neighbors[candidate] = neighbors.TryGetValue(candidate, out var count) ? count + 1 : 1;
            }
        }
    }

    /// <summary>
    /// Take a copy of the borders as they stand, to be able to start a campaign in them
    /// again. Called once, when the map is first built and nothing has played on it yet.
    /// Calling it twice would remember a campaign rather than the scenario, so it does not.
    /// </summary>
    public void RememberAuthoredBorders()
    {
        if (_authoredNation is not null) return;
        _authoredNation = (ushort[])ProvinceToNation.Clone();
        _authoredState = (ushort[])ProvinceToState.Clone();
    }

    /// <summary>
    /// Put the authored borders back, for a campaign that is starting rather than
    /// continuing. Returns false when there is nothing remembered to put back.
    /// </summary>
    public bool RestoreAuthoredBorders()
    {
        if (_authoredNation is null || _authoredState is null) return false;
        if (_authoredNation.Length != ProvinceToNation.Length ||
            _authoredState.Length != ProvinceToState.Length) return false;
        Array.Copy(_authoredNation, ProvinceToNation, _authoredNation.Length);
        Array.Copy(_authoredState, ProvinceToState, _authoredState.Length);
        return true;
    }

    public (float Latitude, float Longitude) CenterOf(int provinceId)
    {
        if (provinceId <= 0 || provinceId >= ProvinceCenterCell.Length) return (0f, 0f);
        var cell = ProvinceCenterCell[provinceId];
        var x = cell % Width;
        var y = cell / Width;
        return (
            90f - (y + 0.5f) * 180f / Height,
            -180f + (x + 0.5f) * 360f / Width);
    }

    public void AttachHydrology(HydrologyRaster hydrology)
    {
        if (hydrology is null || hydrology.Width != Width || hydrology.Height != Height ||
            hydrology.Pixels.Length != Width * Height * 2)
            throw new ArgumentException("Hydrology raster must match province topology dimensions.");
        Array.Clear(RiverIntensityByProvince);
        Array.Clear(LakeAccessByProvince);
        _riverCrossings.Clear();

        Span<int> local = stackalloc int[9];
        for (var cell = 0; cell < CellToProvince.Length; cell++)
        {
            var read = cell * 2;
            var lake = hydrology.Pixels[read];
            var river = hydrology.Pixels[read + 1];
            if (lake == 0 && river == 0) continue;
            var x = cell % Width;
            var y = cell / Width;
            var count = 0;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                if (y + oy < 0 || y + oy >= Height) continue;
                var nx = (x + ox + Width) % Width;
                var province = CellToProvince[(y + oy) * Width + nx];
                if (province <= 0) continue;
                var duplicate = false;
                for (var index = 0; index < count; index++)
                    if (local[index] == province) { duplicate = true; break; }
                if (!duplicate) local[count++] = province;
            }
            for (var index = 0; index < count; index++)
            {
                var province = local[index];
                if (lake > 0) LakeAccessByProvince[province] = true;
                if (river > RiverIntensityByProvince[province]) RiverIntensityByProvince[province] = river;
            }
            if (river == 0 || count < 2) continue;
            for (var a = 0; a < count; a++)
            for (var b = a + 1; b < count; b++)
            {
                var first = local[a];
                var second = local[b];
                if (!AreNeighbors(first, second)) continue;
                var key = EdgeKey(first, second);
                if (!_riverCrossings.TryGetValue(key, out var old) || old < river)
                    _riverCrossings[key] = river;
            }
        }
    }

    public bool IsRiverCrossing(int firstProvince, int secondProvince) =>
        _riverCrossings.ContainsKey(EdgeKey(firstProvince, secondProvince));

    public float RiverCrossingStrength(int firstProvince, int secondProvince) =>
        _riverCrossings.TryGetValue(EdgeKey(firstProvince, secondProvince), out var value) ? value / 255f : 0f;

    public bool HasRiver(int provinceId) => provinceId > 0 && provinceId < RiverIntensityByProvince.Length &&
        RiverIntensityByProvince[provinceId] > 0;

    public bool HasLakeAccess(int provinceId) => provinceId > 0 && provinceId < LakeAccessByProvince.Length &&
        LakeAccessByProvince[provinceId];

    /// <summary>Whether this province is water. Nothing on foot may enter or cross one.</summary>
    public bool IsWater(int provinceId) => provinceId > 0 && WaterByProvince is not null &&
        provinceId < WaterByProvince.Length && WaterByProvince[provinceId];

    private bool AreNeighbors(int first, int second)
    {
        if (first <= 0 || second <= 0 || first >= Neighbors.Length || second >= Neighbors.Length) return false;
        foreach (var candidate in Neighbors[first]) if (candidate == second) return true;
        return false;
    }

    private static long EdgeKey(int first, int second)
    {
        if (first > second) (first, second) = (second, first);
        return ((long)first << 32) | (uint)second;
    }

    /// <summary>Shortest land route constrained to one political controller.</summary>
    // Route scratch. A breadth-first search over the province graph needs one entry per
    // province to remember where it came from - and every search was allocating that array
    // fresh, a hundred and fifty kilobytes of it, and then filling all thirty-eight
    // thousand entries with -1 before touching a few dozen. Front reassignment issues one
    // search per formation it re-tasks, so on a day when fifty wars redrew their fronts
    // this alone was the better part of a gigabyte over a campaign.
    //
    // The buffer is kept, and a generation stamp marks which entries belong to the search
    // running now, so nothing has to be cleared and nothing has to be allocated. Per
    // topology rather than static, because a second campaign has its own map.
    private int[] _routePrevious = Array.Empty<int>();
    private int[] _routeStamp = Array.Empty<int>();
    private int _routeGeneration;
    private readonly Queue<int> _routeFrontier = new();
    private readonly List<int> _routeBuffer = new();

    private int BeginRouteSearch()
    {
        if (_routePrevious.Length < Neighbors.Length)
        {
            _routePrevious = new int[Neighbors.Length];
            _routeStamp = new int[Neighbors.Length];
            _routeGeneration = 0;
        }
        _routeFrontier.Clear();
        return ++_routeGeneration;
    }

    public int[] FindRoute(int startProvince, int targetProvince, ushort nationMapIndex)
        => FindRoute(startProvince, targetProvince, nationMapIndex, ProvinceToNation);

    /// <summary>Shortest route through the supplied live controller layer.</summary>
    public int[] FindRoute(int startProvince, int targetProvince, ushort nationMapIndex, ushort[] controllers,
        bool allowUncontrolledStart = false)
    {
        if (startProvince <= 0 || targetProvince <= 0 ||
            startProvince >= Neighbors.Length || targetProvince >= Neighbors.Length ||
            controllers is null || controllers.Length < Neighbors.Length ||
            (!allowUncontrolledStart && controllers[startProvince] != nationMapIndex) ||
            controllers[targetProvince] != nationMapIndex)
            return Array.Empty<int>();
        if (startProvince == targetProvince) return Array.Empty<int>();
        // Nowhere to march to, and nothing to march through.
        if (IsWater(startProvince) || IsWater(targetProvince)) return Array.Empty<int>();

        var generation = BeginRouteSearch();
        var previous = _routePrevious;
        var stamp = _routeStamp;
        var queue = _routeFrontier;
        stamp[startProvince] = generation;
        previous[startProvince] = startProvince;
        queue.Enqueue(startProvince);

        while (queue.Count > 0 && stamp[targetProvince] != generation)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbors[current])
            {
                if (stamp[next] == generation || controllers[next] != nationMapIndex || IsWater(next)) continue;
                stamp[next] = generation;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        if (stamp[targetProvince] != generation) return Array.Empty<int>();
        var route = _routeBuffer;
        route.Clear();
        for (var cursor = targetProvince; cursor != startProvince; cursor = previous[cursor])
            route.Add(cursor);
        route.Reverse();
        return route.ToArray();
    }

    /// <summary>
    /// Shortest land corridor for an offensive order. Only the two belligerents' ground
    /// may be crossed, so a drawn arrow cannot cut through an unrelated neutral country.
    /// Unlike <see cref="FindRoute(int,int,ushort,ushort[],bool)"/>, hostile provinces are
    /// deliberately legal: this is the route the formation intends to fight through.
    /// </summary>
    public int[] FindOperationalRoute(int startProvince, int targetProvince, ushort friendlyMapIndex,
        ushort hostileMapIndex, ushort[] controllers)
        => FindOperationalRoute(startProvince, targetProvince, friendlyMapIndex, hostileMapIndex, null, controllers);

    /// <summary>Coalition corridor through an explicit live set of belligerent controllers.</summary>
    public int[] FindOperationalRoute(int startProvince, int targetProvince,
        IReadOnlySet<ushort> allowedControllers, ushort[] controllers)
        => allowedControllers is null ? Array.Empty<int>()
            : FindOperationalRoute(startProvince, targetProvince, 0, 0, allowedControllers, controllers);

    private int[] FindOperationalRoute(int startProvince, int targetProvince, ushort friendlyMapIndex,
        ushort hostileMapIndex, IReadOnlySet<ushort> allowedControllers, ushort[] controllers)
    {
        if (startProvince <= 0 || targetProvince <= 0 ||
            startProvince >= Neighbors.Length || targetProvince >= Neighbors.Length ||
            controllers is null || controllers.Length < Neighbors.Length ||
            IsWater(startProvince) || IsWater(targetProvince))
            return Array.Empty<int>();
        if (startProvince == targetProvince) return Array.Empty<int>();

        static bool Allowed(ushort controller, ushort friendly, ushort hostile, IReadOnlySet<ushort> coalition) =>
            coalition is null ? controller == friendly || controller == hostile : coalition.Contains(controller);
        if (!Allowed(controllers[startProvince], friendlyMapIndex, hostileMapIndex, allowedControllers) ||
            !Allowed(controllers[targetProvince], friendlyMapIndex, hostileMapIndex, allowedControllers))
            return Array.Empty<int>();

        var generation = BeginRouteSearch();
        var previous = _routePrevious;
        var stamp = _routeStamp;
        var queue = _routeFrontier;
        stamp[startProvince] = generation;
        previous[startProvince] = startProvince;
        queue.Enqueue(startProvince);

        while (queue.Count > 0 && stamp[targetProvince] != generation)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbors[current])
            {
                if (stamp[next] == generation || IsWater(next) ||
                    !Allowed(controllers[next], friendlyMapIndex, hostileMapIndex, allowedControllers)) continue;
                stamp[next] = generation;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        if (stamp[targetProvince] != generation) return Array.Empty<int>();
        var route = _routeBuffer;
        route.Clear();
        for (var cursor = targetProvince; cursor != startProvince; cursor = previous[cursor])
            route.Add(cursor);
        route.Reverse();
        return route.ToArray();
    }

    public int[] FindRouteToAny(int startProvince, IReadOnlySet<int> targets, ushort nationMapIndex)
        => FindRouteToAny(startProvince, targets, nationMapIndex, ProvinceToNation);

    public int[] FindRouteToAny(
        int startProvince,
        IReadOnlySet<int> targets,
        ushort nationMapIndex,
        ushort[] controllers,
        bool allowUncontrolledStart = false)
    {
        if (startProvince <= 0 || startProvince >= Neighbors.Length || targets.Count == 0 ||
            controllers is null || controllers.Length < Neighbors.Length ||
            (!allowUncontrolledStart && controllers[startProvince] != nationMapIndex))
            return Array.Empty<int>();
        if (IsWater(startProvince)) return Array.Empty<int>();
        if (targets.Contains(startProvince)) return Array.Empty<int>();

        var generation = BeginRouteSearch();
        var previous = _routePrevious;
        var stamp = _routeStamp;
        var queue = _routeFrontier;
        stamp[startProvince] = generation;
        previous[startProvince] = startProvince;
        queue.Enqueue(startProvince);
        var found = -1;

        while (queue.Count > 0 && found < 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbors[current])
            {
                if (stamp[next] == generation || controllers[next] != nationMapIndex || IsWater(next)) continue;
                stamp[next] = generation;
                previous[next] = current;
                if (targets.Contains(next))
                {
                    found = next;
                    break;
                }
                queue.Enqueue(next);
            }
        }
        if (found < 0) return Array.Empty<int>();
        var route = _routeBuffer;
        route.Clear();
        for (var cursor = found; cursor != startProvince; cursor = previous[cursor]) route.Add(cursor);
        route.Reverse();
        return route.ToArray();
    }

    private static void BuildGraph(
        int[] cellToProvince,
        int provinceCount,
        int width,
        int height,
        out int[] centers,
        out int[][] neighbors)
    {
        var firstCell = new int[provinceCount];
        var counts = new int[provinceCount];
        var sumX = new long[provinceCount];
        var sumY = new long[provinceCount];
        Array.Fill(firstCell, -1);

        for (var cell = 0; cell < cellToProvince.Length; cell++)
        {
            var province = cellToProvince[cell];
            if (province <= 0) continue;
            if (firstCell[province] < 0) firstCell[province] = cell;
            var originX = firstCell[province] % width;
            var x = cell % width;
            var dx = x - originX;
            if (dx > width / 2) dx -= width;
            else if (dx < -width / 2) dx += width;
            sumX[province] += originX + dx;
            sumY[province] += cell / width;
            counts[province]++;
        }

        centers = new int[provinceCount];
        Array.Fill(centers, -1);
        var best = new long[provinceCount];
        Array.Fill(best, long.MaxValue);
        for (var cell = 0; cell < cellToProvince.Length; cell++)
        {
            var province = cellToProvince[cell];
            if (province <= 0 || counts[province] == 0) continue;
            var targetX = (int)(sumX[province] / counts[province]);
            targetX = ((targetX % width) + width) % width;
            var targetY = (int)(sumY[province] / counts[province]);
            var x = cell % width;
            var dx = Math.Abs(x - targetX);
            dx = Math.Min(dx, width - dx);
            var dy = cell / width - targetY;
            var score = (long)dx * dx + (long)dy * dy;
            if (score >= best[province]) continue;
            best[province] = score;
            centers[province] = cell;
        }

        var edges = new HashSet<long>();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var cell = y * width + x;
            var province = cellToProvince[cell];
            if (province <= 0) continue;
            AddEdge(province, cellToProvince[y * width + ((x + 1) % width)]);
            if (y + 1 < height) AddEdge(province, cellToProvince[cell + width]);
        }

        var lists = new List<int>[provinceCount];
        for (var province = 0; province < provinceCount; province++) lists[province] = new List<int>();
        foreach (var edge in edges)
        {
            var a = (int)(edge >> 32);
            var b = (int)(edge & 0xFFFFFFFFL);
            lists[a].Add(b);
            lists[b].Add(a);
        }
        neighbors = new int[provinceCount][];
        for (var province = 0; province < provinceCount; province++)
        {
            lists[province].Sort();
            neighbors[province] = lists[province].ToArray();
        }

        void AddEdge(int a, int b)
        {
            if (a <= 0 || b <= 0 || a == b) return;
            if (a > b) (a, b) = (b, a);
            edges.Add(((long)a << 32) | (uint)b);
        }
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var remainder = a % b;
            a = b;
            b = remainder;
        }
        return Math.Abs(a);
    }
}
