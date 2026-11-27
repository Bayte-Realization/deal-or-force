using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Parameters controlling how administrative states are subdivided into tactical provinces.
/// <see cref="Standard"/> matches the shipped 34,138-province world bit-for-bit; map packs override
/// density globally or per climate/region (<see cref="SiberiaMultiplier"/>, <see cref="PolarMultiplier"/>,
/// <see cref="DisputedCellsPerProvince"/>).
/// </summary>
public sealed record ProvinceBuildProfile(
    int CellsPerProvince = ProvinceTopology.CellsPerProvince,
    int DisputedCellsPerProvince = ProvinceTopology.DisputedCellsPerProvince,
    float PolarFromLatitude = ProvinceTopology.PolarFromLatitude,
    float PopulatedShare = ProvinceTopology.PopulatedShare,
    float SiberiaMultiplier = 1f,
    float PolarMultiplier = 1f,
    int MinimumProvinceCells = ProvinceTopology.MinimumProvinceCells)
{
    public static readonly ProvinceBuildProfile Standard = new();
}

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

    /// <summary>Latitude past which a province grows with the real ground it covers (Build).</summary>
    public const float PolarFromLatitude = 60f;
    /// <summary>
    /// From this latitude a province is grown by distance over the ground rather than over raster
    /// cells. The raster is equirectangular: a cell of longitude at 80 degrees is a sixth of the
    /// ground it is at the equator, so a province grown by cell distance came out six times taller
    /// than wide, and the provinces of Antarctica and the Arctic islands were thin wedges meeting
    /// at the pole - a sunburst of borders. Below it the world keeps the shapes it had.
    /// </summary>
    public const float PolarShapeFromLatitude = 50f;

    /// <summary>The narrowest raster read as the whole world for the latitude rule (Build).</summary>
    public const int WorldRasterWidth = 2048;

    /// <summary>Province size below PolarFromLatitude, as a share of CellsPerProvince: the provinces the poles give up.</summary>
    public const float PopulatedShare = 0.72f;

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

    /// <summary>
    /// Where a formation, a supply column or an attack can go next from a province: the
    /// provinces it shares a border with, and the far shore of any strait crossing that
    /// starts in it (<see cref="AttachCrossings"/>). This is the graph every march, front
    /// and supply trace walks, which is the whole point of a crossing - the staff sees
    /// Tarifa and Tangier as neighbours without being taught the word "ferry".
    /// </summary>
    public int[][] Neighbors { get; }

    /// <summary>
    /// The provinces a province shares a drawn border with, and nothing else.
    ///
    /// A bridge lets a division cross a strait; it does not make Sicily part of Calabria.
    /// Everything that asks what the map looks like rather than where an army can go -
    /// which regions a peace may hand over as one piece, whether a country is in two
    /// parts, what a breakaway takes with it, where a land corridor could be bought -
    /// reads this one, so a crossing never welds two shores into one territory.
    /// Row for row the same arrays as <see cref="Neighbors"/> except at a crossing's ends.
    /// </summary>
    public int[][] LandNeighbors { get; }
    public int Width { get; }
    public int Height { get; }
    public int EdgeCount { get; }
    public int ProvinceCount => ProvinceToState.Length - 1;

    private readonly object _provinceMembershipGate = new();
    private volatile ProvinceMembershipIndex _provincesByState;
    private ProvinceMembershipIndex _lastProvinceMembership;

    /// <summary>Invalidate the state-to-provinces index after the map's borders may have changed.</summary>
    public void InvalidateStateProvinces()
    {
        lock (_provinceMembershipGate)
            _provincesByState = null;
    }

    /// <summary>The provinces of a state, in ascending province order.</summary>
    public ReadOnlySpan<int> ProvincesOfState(int stateMapIndex)
    {
        if ((uint)stateMapIndex > ushort.MaxValue) return ReadOnlySpan<int>.Empty;
        var index = _provincesByState ?? RefreshProvinceMembership();
        return index.ForState(stateMapIndex);
    }

    private ProvinceMembershipIndex RefreshProvinceMembership()
    {
        lock (_provinceMembershipGate)
        {
            if (_provincesByState is { } current) return current;

            // Ownership changes also invalidate the state's caches. When province
            // membership stayed the same, the completed index can be published again.
            if (_lastProvinceMembership is null ||
                !ProvinceToState.AsSpan().SequenceEqual(_lastProvinceMembership.StateByProvince))
                _lastProvinceMembership = new ProvinceMembershipIndex((ushort[])ProvinceToState.Clone());

            _provincesByState = _lastProvinceMembership;
            return _lastProvinceMembership;
        }
    }

    private sealed class ProvinceMembershipIndex
    {
        public readonly ushort[] StateByProvince;
        private readonly int[] _offsets;
        private readonly int[] _provinces;

        public ProvinceMembershipIndex(ushort[] stateByProvince)
        {
            StateByProvince = stateByProvince;
            var highestState = 0;
            for (var province = 1; province < stateByProvince.Length; province++)
                highestState = Math.Max(highestState, stateByProvince[province]);

            _offsets = new int[highestState + 2];
            for (var province = 1; province < stateByProvince.Length; province++)
                _offsets[stateByProvince[province]]++;
            for (var state = 1; state < _offsets.Length; state++)
                _offsets[state] += _offsets[state - 1];

            _provinces = new int[stateByProvince.Length - 1];
            // Walk backwards from each row's end to preserve ascending province order.
            // The same counters end up at each row's start.
            for (var province = stateByProvince.Length - 1; province > 0; province--)
                _provinces[--_offsets[stateByProvince[province]]] = province;
        }

        public ReadOnlySpan<int> ForState(int stateMapIndex) =>
            stateMapIndex < _offsets.Length - 1
                ? _provinces.AsSpan(_offsets[stateMapIndex], _offsets[stateMapIndex + 1] - _offsets[stateMapIndex])
                : ReadOnlySpan<int>.Empty;
    }

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
    public bool[] DestroyedByProvince { get; private set; }
    public int RiverCrossingCount => _riverCrossings.Count;
    public TerrainDefinition[] TerrainByProvince { get; private set; } = Array.Empty<TerrainDefinition>();
    public byte[] TerrainSurfaceSlots { get; internal set; } = Array.Empty<byte>();
    public TerrainDefinition TerrainAt(int province) => province > 0 && province < TerrainByProvince.Length
        ? TerrainByProvince[province] : TerrainCatalog.Get("plains");
    public void AttachTerrain() => TerrainByProvince = TerrainCatalog.Bind(this);
    public void AttachTerrain(RgbDataMap decoded) => TerrainByProvince = TerrainCatalog.Bind(this, decoded);
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
        // The rows are shared and the spine is not: attaching a crossing replaces two rows of
        // Neighbors and must leave the drawn borders, and every clone's own crossings, alone.
        LandNeighbors = neighbors;
        Neighbors = (int[][])neighbors.Clone();
        _crossingEnd = new bool[neighbors.Length];
        Width = width;
        Height = height;
        var directedEdges = 0;
        foreach (var list in neighbors) directedEdges += list.Length;
        EdgeCount = directedEdges / 2;
        RiverIntensityByProvince = new byte[ProvinceCount + 1];
        LakeAccessByProvince = new bool[ProvinceCount + 1];
        WaterByProvince = new bool[ProvinceCount + 1];
        DestroyedByProvince = new bool[ProvinceCount + 1];
        for (var province = 1; province <= ProvinceCount; province++)
            WaterByProvince[province] = LakeRegions.IsLakeRegion(ProvinceToState[province]);
    }

    public static ProvinceTopology Build(
        ushort[] states,
        ushort[] control,
        int width,
        int height,
        Func<int, bool> isDisputed) =>
        Build(states, control, width, height, isDisputed, ProvinceBuildProfile.Standard);

    public static ProvinceTopology Build(
        ushort[] states,
        ushort[] control,
        int width,
        int height,
        Func<int, bool> isDisputed,
        ProvinceBuildProfile profile)
    {
        profile ??= ProvinceBuildProfile.Standard;
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
        var rowGround = RowGround(width, height);
        var polarFromLatitude = profile.PolarFromLatitude;
        var populatedShare = profile.PopulatedShare;
        var minProvinceCells = Math.Max(1, profile.MinimumProvinceCells);
        var minFloor = minProvinceCells == MinimumProvinceCells
            ? 18
            : Math.Max(6, (int)MathF.Round(minProvinceCells * 0.75f));

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
            var baseline = disputedStates.Contains(state) ? profile.DisputedCellsPerProvince : profile.CellsPerProvince;
            // The raster is equirectangular: a cell at 72 degrees covers a third of the ground a
            // cell at the equator does, so provinces of a fixed cell count put three times as many
            // provinces on Greenland per square kilometre as on the Ukraine steppe, and six times as
            // many on Antarctica. Past PolarFromLatitude a province grows with the ground it covers;
            // the provinces the ice gives up are spread over everywhere else (PopulatedShare), so
            // the map keeps its count and spends it where people live and fight.
            var rawLatitude = 90f - (start / width + 0.5f) * 180f / height;
            var seedLatitude = MathF.Abs(rawLatitude);
            // Only the world raster: a test grid of sixty-four cells has no latitudes to speak of.
            var ground = width < WorldRasterWidth ? 1f : seedLatitude <= polarFromLatitude ? populatedShare
                : MathF.Cos(polarFromLatitude * MathF.PI / 180f) / MathF.Max(0.0025f, MathF.Cos(seedLatitude * MathF.PI / 180f));
            var multiplier = 1f;
            if (width >= WorldRasterWidth)
            {
                if (profile.SiberiaMultiplier > 1f && rawLatitude >= 50f)
                {
                    var seedLongitude = (start % width + 0.5f) * 360f / width - 180f;
                    if (seedLongitude >= 60f && seedLongitude <= 180f)
                        multiplier *= profile.SiberiaMultiplier;
                }
                if (profile.PolarMultiplier > 1f && seedLatitude >= polarFromLatitude)
                    multiplier *= profile.PolarMultiplier;
            }
            var clampedGround = Math.Clamp(ground, 0.5f, 200f);
            var scaledCells = multiplier == 1f
                ? baseline * clampedGround
                : baseline * clampedGround * multiplier;
            baseline = Math.Max(minProvinceCells + 6, (int)MathF.Round(scaledCells));
            // Keep neighbouring provinces recognisably different without turning
            // their borders into tendrils.  The resulting cells are a relaxed,
            // irregular Voronoi-like layer: mostly convex, occasionally elongated,
            // and deterministic so province IDs remain stable in save games.
            // Deliberately use a broad area distribution. A narrow +/-25 % band made
            // the 40k layer read as a repeating field of fish-scales at strategic
            // zoom even though every border was technically irregular.
            var minimumCap = Math.Max(minFloor, (int)MathF.Round(baseline * 0.43f));
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
                    float localX = dx * MathF.Sign(nextX - seedX == 0 ? 1 : nextX - seedX);
                    if (Math.Abs(nextX - seedX) > width / 2) localX = -localX;
                    // Over the ground: near a pole many cells of longitude are one step east.
                    var cellGround = rowGround[y + oy];
                    localX *= cellGround;
                    var localY = dy;
                    var major = localX * cos + localY * sin;
                    var minor = -localX * sin + localY * cos + major * skew;
                    var distance = major * major / axisRatio + minor * minor * axisRatio;

                    // Low-amplitude coherent noise prevents repeated geometric cells.
                    // It is deliberately weaker than the elliptical distance field,
                    // keeping borders readable for front lines and selection.
                    // The noise cells are ground-sized too, or they would be thin wedges themselves.
                    var noiseX = cellGround >= 1f ? nextX / 3 : (int)(nextX * cellGround / 3f);
                    var coarseKey = (uint)(noiseX * 73856093 ^ ((y + oy) / 3) * 19349663 ^ province * 83492791);
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
        // A province is a run of one colour along most rows: the colour just looked up is the
        // likeliest next one, and asking the table again for it was most of this loop.
        var lastColor = -1;
        var lastProvince = 0;
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
            int province;
            if (color != 0 && color == lastColor) province = lastProvince;
            else if (color == 0 || !colorToProvince.TryGetValue(color, out province))
                throw new InvalidDataException($"Land pixel {cell} uses unknown province colour #{color:X6}.");
            else (lastColor, lastProvince) = (color, province);
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
    /// A copy that shares the expensive parts and owns its own political arrays. Used to
    /// try a political override without touching the map the campaign is running on.
    /// </summary>
    public ProvinceTopology CloneForOverrides()
    {
        var clone = new ProvinceTopology(CellToProvince, (ushort[])ProvinceToState.Clone(), (ushort[])ProvinceToNation.Clone(),
            ProvinceCenterCell, LandNeighbors, Width, Height)
        { TerrainByProvince = (TerrainDefinition[])TerrainByProvince.Clone(), TerrainSurfaceSlots = TerrainSurfaceSlots };
        // The same map, so the same straits are bridged on it.
        if (_crossings.Length > 0) clone.AttachCrossings(_crossings);
        return clone;
    }

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

    /// <summary>
    /// Turns the seed-growth fallback into a restrained anisotropic Voronoi-like map
    /// before it is baked to provinces.png. IDs, political/state borders and one seed
    /// pixel per province remain fixed; only internal tactical borders are relaxed.
    /// </summary>
    /// <summary>
    /// How much ground a cell of longitude is on each row, against a cell of latitude: one below
    /// PolarShapeFromLatitude (the shapes the world was cut with), eased into the true cosine of the
    /// latitude over the next fifteen degrees, so no line of latitude shows where the rule changes.
    /// </summary>
    private static float[] RowGround(int width, int height)
    {
        var rows = new float[height];
        for (var row = 0; row < height; row++)
        {
            var latitude = MathF.Abs(90f - (row + 0.5f) * 180f / height);
            var toGround = width < WorldRasterWidth ? 0f : Math.Clamp((latitude - PolarShapeFromLatitude) / 15f, 0f, 1f);
            toGround = toGround * toGround * (3f - 2f * toGround);
            rows[row] = 1f + (MathF.Cos(latitude * MathF.PI / 180f) - 1f) * toGround;
        }
        return rows;
    }

    public ProvinceTopology RelaxShapes(ushort[] states, ushort[] control, int iterations = 3)
    {
        var rowGround = RowGround(Width, Height);
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
                var bestCost = ShapeCost(province, x, y, cosines, sines, ratios, skews, rowGround);
                for (var index = 1; index < count; index++)
                {
                    var candidate = candidates[index];
                    var cost = ShapeCost(candidate, x, y, cosines, sines, ratios, skews, rowGround);
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

    private float ShapeCost(int province, int x, int y, float[] cosines, float[] sines, float[] ratios, float[] skews,
        float[] rowGround)
    {
        var seed = ProvinceCenterCell[province];
        var seedX = seed % Width;
        var seedY = seed / Width;
        var dx = x - seedX;
        if (dx > Width / 2) dx -= Width;
        else if (dx < -Width / 2) dx += Width;
        var dy = y - seedY;
        // Over the ground, as the province was grown (RowGround).
        var across = dx * rowGround[y];
        var major = across * cosines[province] + dy * sines[province];
        var minor = -across * sines[province] + dy * cosines[province] + major * skews[province];
        var ratio = ratios[province];
        var distance = major * major / ratio + minor * minor * ratio;
        var noiseX = rowGround[y] >= 1f ? x / 5 : (int)(x * rowGround[y] / 5f);
        var noiseKey = (uint)(noiseX * 73856093 ^ (y / 5) * 19349663 ^ province * 83492791);
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
        Array.Clear(DestroyedByProvince);
        InvalidateStateProvinces();
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

    public bool IsRiverCrossing(int firstProvince, int secondProvince)
    {
        var rivers = RiverIntensityByProvince;
        if (_riverCrossings.Count == 0 ||
            (uint)firstProvince >= (uint)rivers.Length || rivers[firstProvince] == 0 ||
            (uint)secondProvince >= (uint)rivers.Length || rivers[secondProvince] == 0)
            return false;
        return _riverCrossings.ContainsKey(EdgeKey(firstProvince, secondProvince));
    }

    public float RiverCrossingStrength(int firstProvince, int secondProvince)
    {
        var rivers = RiverIntensityByProvince;
        if (_riverCrossings.Count == 0 ||
            (uint)firstProvince >= (uint)rivers.Length || rivers[firstProvince] == 0 ||
            (uint)secondProvince >= (uint)rivers.Length || rivers[secondProvince] == 0)
            return 0f;
        return _riverCrossings.TryGetValue(EdgeKey(firstProvince, secondProvince), out var value) ? value / 255f : 0f;
    }

    public bool HasRiver(int provinceId) => provinceId > 0 && provinceId < RiverIntensityByProvince.Length &&
        RiverIntensityByProvince[provinceId] > 0;

    public bool HasLakeAccess(int provinceId) => provinceId > 0 && provinceId < LakeAccessByProvince.Length &&
        LakeAccessByProvince[provinceId];

    /// <summary>Whether this province is water. Nothing on foot may enter or cross one.</summary>
    public bool IsWater(int provinceId) => provinceId > 0 && WaterByProvince is not null &&
        provinceId < WaterByProvince.Length && (WaterByProvince[provinceId] || DestroyedByProvince[provinceId]);

    // A river runs between provinces that share a bank, so this is asked of the drawn borders:
    // the two ends of a bridge are neighbours for a march and never for a river.
    private bool AreNeighbors(int first, int second)
    {
        if (first <= 0 || second <= 0 || first >= LandNeighbors.Length || second >= LandNeighbors.Length) return false;
        foreach (var candidate in LandNeighbors[first]) if (candidate == second) return true;
        return false;
    }

    // ─── Strait crossings ──────────────────────────────────────────────────────
    // The links themselves are authored in map_data/crossings and resolved from their
    // coordinates by LandCrossings; the topology only carries the result, because this
    // is where "what is next to what" is answered and a second place to ask it would
    // be a second answer.
    private ResolvedCrossing[] _crossings = Array.Empty<ResolvedCrossing>();
    private readonly Dictionary<long, ResolvedCrossing> _crossingByEdge = new();
    private bool[] _crossingEnd;

    /// <summary>Every crossing on this map, in the order they were attached (id order).</summary>
    public IReadOnlyList<ResolvedCrossing> Crossings => _crossings;

    /// <summary>
    /// Bumped whenever the crossings change. Anything that keeps a copy of
    /// <see cref="Neighbors"/> keeps this beside it and rebuilds when the two disagree.
    /// </summary>
    public int AdjacencyRevision { get; private set; }

    /// <summary>
    /// Replace the map's crossings with this set: each becomes an edge of
    /// <see cref="Neighbors"/> in both directions, and the edges of the previous set are
    /// taken out first, so attaching an empty list is how a map loses its bridges.
    /// The caller has already checked each link (LandCrossings.Resolve); a link that
    /// would not be a crossing - an end off the map or in the water, both ends the same
    /// province, two provinces that already share a border, a pair already linked - is
    /// refused here as well rather than quietly corrupting the graph.
    /// </summary>
    public void AttachCrossings(IReadOnlyList<ResolvedCrossing> crossings)
    {
        // Every entry is judged against the land borders before anything is changed: a refusal
        // part of the way through used to leave the graph half-joined, with the old list still
        // recorded as what was attached - so the next attempt could not even undo it.
        var seen = new HashSet<long>();
        if (crossings is not null)
            foreach (var crossing in crossings)
            {
                if (crossing is null) continue;
                int from = crossing.From, to = crossing.To;
                if (from <= 0 || to <= 0 || from >= LandNeighbors.Length || to >= LandNeighbors.Length || from == to)
                    throw new ArgumentException($"Crossing '{crossing.Id}' does not join two provinces of this map.");
                if (WaterByProvince[from] || WaterByProvince[to])
                    throw new ArgumentException($"Crossing '{crossing.Id}' ends in the water.");
                if (Array.IndexOf(LandNeighbors[from], to) >= 0)
                    throw new ArgumentException($"Crossing '{crossing.Id}' joins provinces {from} and {to}, which already share a border.");
                if (!seen.Add(EdgeKey(from, to)))
                    throw new ArgumentException($"Crossing '{crossing.Id}' repeats the link between provinces {from} and {to}.");
            }

        foreach (var old in _crossings)
        {
            Neighbors[old.From] = LandNeighbors[old.From];
            Neighbors[old.To] = LandNeighbors[old.To];
            _crossingEnd[old.From] = _crossingEnd[old.To] = false;
        }
        _crossingByEdge.Clear();
        var kept = new List<ResolvedCrossing>(crossings?.Count ?? 0);
        if (crossings is not null)
            foreach (var crossing in crossings)
            {
                if (crossing is null) continue;
                int from = crossing.From, to = crossing.To;
                _crossingByEdge[EdgeKey(from, to)] = crossing;
                Neighbors[from] = WithNeighbor(Neighbors[from], to);
                Neighbors[to] = WithNeighbor(Neighbors[to], from);
                _crossingEnd[from] = _crossingEnd[to] = true;
                kept.Add(crossing);
            }
        _crossings = kept.ToArray();
        AdjacencyRevision++;
    }

    /// <summary>A neighbour row with one more province in it, kept in id order like the rest.</summary>
    private static int[] WithNeighbor(int[] row, int province)
    {
        var widened = new int[row.Length + 1];
        var at = 0;
        while (at < row.Length && row[at] < province) { widened[at] = row[at]; at++; }
        widened[at] = province;
        Array.Copy(row, at, widened, at + 1, row.Length - at);
        return widened;
    }

    /// <summary>Whether any crossing starts or ends here: one array read, for the march and the assault.</summary>
    public bool IsCrossingEnd(int province) =>
        province > 0 && province < _crossingEnd.Length && _crossingEnd[province];

    /// <summary>The crossing that joins two provinces, or null when they are joined by a border or not at all.</summary>
    public ResolvedCrossing CrossingBetween(int firstProvince, int secondProvince) =>
        IsCrossingEnd(firstProvince) && IsCrossingEnd(secondProvince) &&
        _crossingByEdge.TryGetValue(EdgeKey(firstProvince, secondProvince), out var crossing) ? crossing : null;

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

    public void FillOperationalRoute(int startProvince, int targetProvince,
        IReadOnlySet<ushort> allowedControllers, ushort[] controllers, List<int> destination)
    {
        if (destination is null) return;
        destination.Clear();
        if (allowedControllers is null) return;
        if (!PopulateOperationalRoute(startProvince, targetProvince, 0, 0, allowedControllers, controllers)) return;
        for (var i = 0; i < _routeBuffer.Count; i++)
            destination.Add(_routeBuffer[i]);
    }

    private int[] FindOperationalRoute(int startProvince, int targetProvince, ushort friendlyMapIndex,
        ushort hostileMapIndex, IReadOnlySet<ushort> allowedControllers, ushort[] controllers)
        => PopulateOperationalRoute(startProvince, targetProvince, friendlyMapIndex, hostileMapIndex, allowedControllers, controllers)
            ? _routeBuffer.ToArray()
            : Array.Empty<int>();

    private bool PopulateOperationalRoute(int startProvince, int targetProvince, ushort friendlyMapIndex,
        ushort hostileMapIndex, IReadOnlySet<ushort> allowedControllers, ushort[] controllers)
    {
        if (startProvince <= 0 || targetProvince <= 0 ||
            startProvince >= Neighbors.Length || targetProvince >= Neighbors.Length ||
            controllers is null || controllers.Length < Neighbors.Length ||
            IsWater(startProvince) || IsWater(targetProvince))
            return false;
        if (startProvince == targetProvince) return false;

        static bool Allowed(ushort controller, ushort friendly, ushort hostile, IReadOnlySet<ushort> coalition) =>
            coalition is null ? controller == friendly || controller == hostile : coalition.Contains(controller);
        if (!Allowed(controllers[startProvince], friendlyMapIndex, hostileMapIndex, allowedControllers) ||
            !Allowed(controllers[targetProvince], friendlyMapIndex, hostileMapIndex, allowedControllers))
            return false;

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

        if (stamp[targetProvince] != generation) return false;
        var route = _routeBuffer;
        route.Clear();
        for (var cursor = targetProvince; cursor != startProvince; cursor = previous[cursor])
            route.Add(cursor);
        route.Reverse();
        return true;
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
        var originXByProv = new int[provinceCount];
        var counts = new int[provinceCount];
        var sumX = new long[provinceCount];
        var sumY = new long[provinceCount];
        Array.Fill(originXByProv, -1);
        var halfWidth = width / 2;

        // Pass 1: accumulate centroid sums with nested (y, x) loops (zero % or / per pixel).
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                var province = cellToProvince[rowStart + x];
                if (province <= 0) continue;
                var originX = originXByProv[province];
                if (originX < 0) { originX = x; originXByProv[province] = x; }
                var dx = x - originX;
                if (dx > halfWidth) dx -= width;
                else if (dx < -halfWidth) dx += width;
                sumX[province] += originX + dx;
                sumY[province] += y;
                counts[province]++;
            }
        }

        // Precompute integer target (x, y) once per province instead of once per pixel.
        var targetXByProv = new int[provinceCount];
        var targetYByProv = new int[provinceCount];
        for (var p = 1; p < provinceCount; p++)
        {
            var c = counts[p];
            if (c <= 0) continue;
            var tx = (int)(sumX[p] / c);
            tx %= width;
            if (tx < 0) tx += width;
            targetXByProv[p] = tx;
            targetYByProv[p] = (int)(sumY[p] / c);
        }

        centers = new int[provinceCount];
        Array.Fill(centers, -1);
        var best = new long[provinceCount];
        Array.Fill(best, long.MaxValue);
        var edges = new HashSet<long>(Math.Max(64, provinceCount * 4));

        // Pass 2: pick nearest center cell AND collect horizontal/vertical neighbor edges in one pass.
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width;
            var hasBelow = y + 1 < height;
            for (var x = 0; x < width; x++)
            {
                var cell = rowStart + x;
                var province = cellToProvince[cell];
                if (province <= 0) continue;

                var dx = x - targetXByProv[province];
                if (dx < 0) dx = -dx;
                var wrapDx = width - dx;
                if (wrapDx < dx) dx = wrapDx;
                var dy = y - targetYByProv[province];
                var score = (long)dx * dx + (long)dy * dy;
                if (score < best[province])
                {
                    best[province] = score;
                    centers[province] = cell;
                }

                var right = cellToProvince[x + 1 < width ? cell + 1 : rowStart];
                if (right > 0 && right != province)
                {
                    var a = province < right ? province : right;
                    var b = province < right ? right : province;
                    edges.Add(((long)a << 32) | (uint)b);
                }
                if (hasBelow)
                {
                    var down = cellToProvince[cell + width];
                    if (down > 0 && down != province)
                    {
                        var a = province < down ? province : down;
                        var b = province < down ? down : province;
                        edges.Add(((long)a << 32) | (uint)b);
                    }
                }
            }
        }

        var lists = new List<int>[provinceCount];
        for (var province = 0; province < provinceCount; province++) lists[province] = new List<int>(8);
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
