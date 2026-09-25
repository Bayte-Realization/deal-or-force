using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Latitude beyond which the sea is ice, and nobody sails over the pole.</summary>
    public float SeaLaneIceLatitude { get; set; } = 80f;

    /// <summary>
    /// How many raster cells either side of its line a passage is cut. Wide enough to take in
    /// the whole of a real strait, so that a strait shut to a country is shut.
    /// </summary>
    public int SeaLanePassageWidth { get; set; } = 2;
}

/// <summary>
/// A strait or a canal: water the map is too coarse to draw, or water that somebody holds.
///
/// The Bosporus is a mile wide and the Suez Canal is a ditch, and on a raster whose cells
/// are seven kilometres across neither is there - so without this the Black Sea was a lake
/// and a ship from Bombay went round Africa to reach Toulon. A passage is a line of points
/// the sea lanes treat as water whatever the raster says.
///
/// A passage that `closes_to_enemies` is shut to every country at war with whoever holds
/// the ground at its middle, which is the whole of what owning the Suez Canal or the
/// Turkish Straits has meant in every war fought near them. A passage that
/// `closes_to_belligerents` is shut besides to the warships of every country at war with
/// anybody, unless its holder is fighting on that country's side - which is the Montreux
/// Convention, and what Turkey did to the Bosporus and the Dardanelles in February 2022.
/// `"open": false` shuts one outright, which is how a mod blocks a canal - or draws a new
/// one, by adding a key.
/// </summary>
public sealed class SeaPassage
{
    public string Id = "";
    public string Name = "";
    public bool ClosesToEnemies;
    public bool ClosesToBelligerents;
    public bool Open = true;
    public readonly List<(float Latitude, float Longitude)> Points = new();
    public (float Latitude, float Longitude)? Holder;
}

public static class SeaPassageCatalog
{
    private static readonly Dictionary<string, SeaPassage> ById = new(StringComparer.OrdinalIgnoreCase);
    private static List<SeaPassage> _ordered = new();

    static SeaPassageCatalog() => ResetCatalog();

    public static IReadOnlyList<SeaPassage> All => _ordered;

    /// <summary>Bumped on every change, so the sea lanes know to redraw the water.</summary>
    public static int Revision { get; private set; }

    public static SeaPassage Of(string id) =>
        id is not null && ById.TryGetValue(id, out var passage) ? passage : null;

    public static void ResetCatalog(List<string> errors = null)
    {
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "sea_lanes"))
            using (document)
                Merge(document.RootElement, errors);
        Settle();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) || entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!ById.TryGetValue(entry.Name, out var passage))
                ById[entry.Name] = passage = new SeaPassage { Id = entry.Name, Name = entry.Name };
            var value = entry.Value;
            if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                passage.Name = name.GetString() ?? passage.Name;
            if (value.TryGetProperty("closes_to_enemies", out var closes) &&
                closes.ValueKind is JsonValueKind.True or JsonValueKind.False)
                passage.ClosesToEnemies = closes.GetBoolean();
            if (value.TryGetProperty("closes_to_belligerents", out var belligerents) &&
                belligerents.ValueKind is JsonValueKind.True or JsonValueKind.False)
                passage.ClosesToBelligerents = belligerents.GetBoolean();
            if (value.TryGetProperty("open", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False)
                passage.Open = open.GetBoolean();
            if (value.TryGetProperty("holder", out var holder) && TryPoint(holder, out var held))
                passage.Holder = held;
            if (value.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            {
                passage.Points.Clear();
                foreach (var point in points.EnumerateArray())
                {
                    if (TryPoint(point, out var at)) passage.Points.Add(at);
                    else errors?.Add($"sea_lanes/{entry.Name}: a point must be [latitude, longitude].");
                }
                if (passage.Points.Count < 2) errors?.Add($"sea_lanes/{entry.Name}: a passage needs two points at least.");
            }
        }
        Settle();
    }

    private static bool TryPoint(JsonElement element, out (float Latitude, float Longitude) point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2) return false;
        if (!element[0].TryGetSingle(out var latitude) || !element[1].TryGetSingle(out var longitude)) return false;
        if (latitude is < -90f or > 90f || longitude is < -180f or > 180f) return false;
        point = (latitude, longitude);
        return true;
    }

    private static void Settle()
    {
        _ordered = new List<SeaPassage>(ById.Values);
        _ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        Revision++;
    }
}

/// <summary>
/// Where a ship can go.
///
/// Fleets used to sail a straight line over the globe, which on this map meant a squadron
/// from Vladivostok reached the Baltic across Siberia and a fleet ordered to an enemy coast
/// parked on the centre of a province forty miles inland. The map has no sea provinces, and
/// inventing thirty thousand of them for sixty fleets would be the tail wagging the dog - so
/// the sea is worked out once from the raster the provinces are cut from, where every cell
/// that belongs to no province is water.
///
/// The world is cut into one-degree squares, and the water in each square into the pieces
/// of it that are joined: a square with Cuba across it holds two, one each side of the
/// island. Those pieces are the nodes of the sea, and two are neighbours only where their
/// water touches on the raster - so a peninsula or an island narrower than a square still
/// stands in the way, where a coarser grid let a fleet sail through Basilicata and across
/// Panama at Chiriquí. A route is the shortest way through the pieces, pulled straight
/// wherever a line stays on water the whole way, so a crossing is one leg and a voyage round
/// a continent is a dozen.
///
/// Three more things fall out of the same work. A basin is a body of water every piece of
/// which reaches every other, so "can this fleet get there at all" is one comparison. A
/// harbour is the water beside a province's coast nearest its middle, which is where a
/// fleet lies when it is in that province's port, blockading it or landing on it. And a
/// passage the raster is too narrow to show - a strait, a canal - is read from
/// `common/sea_lanes` and cut into the water before any of it is worked out.
/// </summary>
public static class SeaLanes
{
    public const int Columns = 360;
    public const int Rows = 180;
    public const int Cells = Columns * Rows;

    private const byte Land = 0;
    private const byte Sea = 1;

    private static readonly object Gate = new();
    private static ProvinceTopology _map;
    private static int _passageRevision = -1;
    private static float _ice = -1f;
    private static int _width = -1;

    // The raster, and how it is cut into squares.
    private static int _rasterWidth;
    private static int _rasterHeight;
    private static ushort[] _raster = Array.Empty<ushort>();
    private static int[] _columnOf = Array.Empty<int>();
    private static int[] _rowOf = Array.Empty<int>();
    private static int[] ColumnStart = new int[Columns + 1];
    private static int[] RowStart = new int[Rows + 1];
    private static int _iceTop;
    private static int _iceBottom;

    // What the passages change: water cut through land, and water shut.
    private static Dictionary<int, byte> PassagePixels = new();
    private static HashSet<int> ShutPixels = new();
    private static bool[] Overridden = new bool[Cells];

    // The sea itself: one node for each joined piece of water in each square.
    private static int _nodes;
    private static int[] _nodePixel = Array.Empty<int>();
    private static float[] _nodeLatitude = Array.Empty<float>();
    private static float[] _nodeLongitude = Array.Empty<float>();
    private static short[] _nodePassage = Array.Empty<short>();
    private static int[] _nodeBasin = Array.Empty<int>();
    private static int[] _basinSize = Array.Empty<int>();

    /// <summary>
    /// Pieces a body of water needs before a ship is put on it. The raster has specks of
    /// unclaimed ground inland and pools at the heads of fjords that are water to the letter
    /// of the rule and to nobody else; a fleet snapped into one of those is a fleet in a pond.
    /// </summary>
    private const int OpenSeaPieces = 16;
    private static int[] CellFirst = new int[Cells + 1];
    private static int[] _linkStart = Array.Empty<int>();
    private static int[] _links = Array.Empty<int>();
    private static int _basins;

    private static int[] _harbourPixel = Array.Empty<int>();
    private static int[] _harbourNode = Array.Empty<int>();
    private static int[] _coastal = Array.Empty<int>();
    private static int[] _holders = Array.Empty<int>();

    // The search's own scratch, reused: a route is asked for a few times a day, and fresh
    // arrays for each would be the whole of the navy's garbage.
    private static float[] _cost = Array.Empty<float>();
    private static int[] _came = Array.Empty<int>();
    private static int[] _seen = Array.Empty<int>();
    private static int[] _done = Array.Empty<int>();
    private static int _generation;
    private static readonly PriorityQueue<int, float> Frontier = new();
    private static readonly List<int> PathNodes = new();
    private static readonly List<(float Latitude, float Longitude)> PathPoints = new();

    // And a flood fill's, the size of one square.
    private static int[] _fillStamp = Array.Empty<int>();
    private static int _fillGeneration;
    private static readonly Stack<int> FillStack = new();

    public static int BasinCount { get { lock (Gate) return _basins; } }

    /// <summary>Forget the sea, so the next question works it out again from the raster.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            if (_map is not null) Seas.Remove(_map);
            _map = null;
        }
    }

    public static int CellAt(float latitude, float longitude)
    {
        var row = Math.Clamp((int)MathF.Floor(90f - latitude), 0, Rows - 1);
        var column = (int)MathF.Floor(longitude + 180f) % Columns;
        if (column < 0) column += Columns;
        return row * Columns + column;
    }

    public static (float Latitude, float Longitude) CentreOf(int cell) =>
        (90f - (cell / Columns + 0.5f), -180f + (cell % Columns + 0.5f));

    /// <summary>Whether a square has any water in it at all, on this map.</summary>
    public static bool IsWater(ProvinceTopology map, int cell)
    {
        if (map is null || cell < 0 || cell >= Cells) return false;
        lock (Gate)
        {
            Prepare(map, null);
            return CellFirst[cell + 1] > CellFirst[cell];
        }
    }

    /// <summary>Whether a point is on water: the sea, or a passage cut through the land.</summary>
    public static bool IsWaterAt(ProvinceTopology map, float latitude, float longitude)
    {
        if (map is null) return true;
        lock (Gate)
        {
            Prepare(map, null);
            return Passable(PixelAt(latitude, longitude), null);
        }
    }

    /// <summary>Which body of water a point is on or beside, or -1 far inland.</summary>
    public static int BasinAt(ProvinceTopology map, float latitude, float longitude)
    {
        if (map is null) return 0;
        lock (Gate)
        {
            Prepare(map, null);
            var node = NodeNear(PixelAt(latitude, longitude), null, out _);
            return node < 0 ? -1 : _nodeBasin[node];
        }
    }

    /// <summary>
    /// The piece of sea off a province's coast, or -1 for a province with none. A lake shore
    /// is not a coast: nothing that floats on the Caspian reaches the ocean.
    /// </summary>
    public static int HarbourOf(ProvinceTopology map, int province)
    {
        if (map is null) return -1;
        lock (Gate)
        {
            Prepare(map, null);
            return province > 0 && province < _harbourNode.Length ? _harbourNode[province] : -1;
        }
    }

    /// <summary>Where a fleet lies when it is off a province: on the water beside its coast.</summary>
    public static bool TryHarbourPoint(ProvinceTopology map, int province, out float latitude, out float longitude)
    {
        latitude = longitude = 0f;
        if (map is null) return false;
        lock (Gate)
        {
            Prepare(map, null);
            if (province <= 0 || province >= _harbourPixel.Length || _harbourPixel[province] < 0) return false;
            (latitude, longitude) = PixelCentre(_harbourPixel[province]);
            return true;
        }
    }

    public static int HarbourBasin(ProvinceTopology map, int province)
    {
        var node = HarbourOf(map, province);
        if (node < 0) return -1;
        lock (Gate) return _nodeBasin[node];
    }

    /// <summary>Every province with a harbour, in id order.</summary>
    public static IReadOnlyList<int> CoastalProvinces(ProvinceTopology map)
    {
        if (map is null) return Array.Empty<int>();
        lock (Gate)
        {
            Prepare(map, null);
            return _coastal;
        }
    }

    /// <summary>The nearest point on water, or the point itself when it is already on water.</summary>
    public static (float Latitude, float Longitude) WaterPointNear(ProvinceTopology map, float latitude, float longitude)
    {
        if (map is null) return (latitude, longitude);
        lock (Gate)
        {
            Prepare(map, null);
            var pixel = PixelAt(latitude, longitude);
            if (Passable(pixel, null)) return (latitude, longitude);
            var near = NearestWaterPixel(pixel, 48, null);
            return near < 0 ? (latitude, longitude) : PixelCentre(near);
        }
    }

    /// <summary>The province that holds a passage, whose enemies it is shut to.</summary>
    public static int HolderOf(ProvinceTopology map, int passage)
    {
        if (map is null) return -1;
        lock (Gate)
        {
            Prepare(map, null);
            return passage >= 0 && passage < _holders.Length ? _holders[passage] : -1;
        }
    }

    // --- for the bench's picture ------------------------------------------------------

    public static int NodeCount(ProvinceTopology map)
    {
        if (map is null) return 0;
        lock (Gate) { Prepare(map, null); return _nodes; }
    }

    public static (float Latitude, float Longitude, int Basin, bool Passage) Node(ProvinceTopology map, int node)
    {
        lock (Gate)
        {
            Prepare(map, null);
            return (_nodeLatitude[node], _nodeLongitude[node], _nodeBasin[node], _nodePassage[node] >= 0);
        }
    }

    public static IEnumerable<int> Links(ProvinceTopology map, int node)
    {
        int start, end;
        lock (Gate)
        {
            Prepare(map, null);
            start = _linkStart[node];
            end = _linkStart[node + 1];
        }
        for (var index = start; index < end; index++) yield return _links[index];
    }

    // --- plotting a course -------------------------------------------------------------

    /// <summary>
    /// Plot a course. Fills `waypoints` with latitude, longitude pairs, the last of which is
    /// where the fleet stops: the destination itself when it is on water, the nearest water
    /// to it when it is not. False when the destination cannot be reached by sea at all, or
    /// only through a passage shut to this country.
    /// </summary>
    public static bool Plan(Campaign campaign, int nationId, float fromLatitude, float fromLongitude,
        float toLatitude, float toLongitude, List<float> waypoints, out float length)
    {
        waypoints.Clear();
        length = 0f;
        var map = campaign?.ProvinceMap;
        if (map is null)
        {
            // No map, no coasts: the open-ocean model the navy was written against.
            waypoints.Add(toLatitude);
            waypoints.Add(toLongitude);
            length = NavalOperations.Distance(fromLatitude, fromLongitude, toLatitude, toLongitude);
            return true;
        }

        lock (Gate)
        {
            Prepare(map, campaign);
            var closed = ClosedPassages(campaign, nationId);

            var start = NodeNear(PixelAt(fromLatitude, fromLongitude), closed, out _);
            var goalPixel = PixelAt(toLatitude, toLongitude);
            var goal = NodeNear(goalPixel, closed, out var landing);
            if (start < 0 || goal < 0 || _nodeBasin[start] != _nodeBasin[goal]) return false;
            // Behind a shut strait the sea is the same basin and a different body of water:
            // asked of the search, "no way through" costs a walk over the whole ocean before
            // it is known. Asked of the components for this set of shut passages, it is free.
            if (closed is not null)
            {
                var parts = Components(closed);
                if (parts[start] != parts[goal]) return false;
            }

            var end = landing == goalPixel ? (toLatitude, toLongitude) : PixelCentre(landing);
            var from = (fromLatitude, fromLongitude);
            if (Clear(from, end, true, closed))
            {
                waypoints.Add(end.Item1);
                waypoints.Add(end.Item2);
                length = NavalOperations.Distance(from.Item1, from.Item2, end.Item1, end.Item2);
                return true;
            }

            if (!Search(start, goal, closed)) return false;

            PathPoints.Clear();
            PathPoints.Add(from);
            foreach (var node in PathNodes) PathPoints.Add((_nodeLatitude[node], _nodeLongitude[node]));
            PathPoints.Add(end);

            // Pull the staircase straight wherever the water lets it: from each point, the
            // furthest one ahead that can be sailed to in a line. A failure is not the end of
            // the look - a headland can hide one point and not the next - so it looks a
            // little further before settling.
            var anchor = 0;
            var last = PathPoints.Count - 1;
            var here = from;
            while (anchor < last)
            {
                var next = anchor + 1;
                var misses = 0;
                for (var ahead = anchor + 2; ahead <= last && misses < 8; ahead++)
                {
                    if (Clear(PathPoints[anchor], PathPoints[ahead], anchor == 0, closed))
                    {
                        next = ahead;
                        misses = 0;
                    }
                    else misses++;
                }
                var point = PathPoints[next];
                // The step to the very next piece of sea is taken on trust - the pieces touch -
                // but the points that stand for two pieces are a pixel of each, and the line
                // between them can cut the headland the pieces wrap round: Cape Maisí on the
                // way to Panama, the Lyakhov islands on the way along Siberia. Such a leg is
                // walked pixel by pixel instead, and pulled straight again.
                if (next == anchor + 1 && !Clear(PathPoints[anchor], point, anchor == 0, closed))
                {
                    Refine(PathPoints[anchor], point, closed, Refined);
                    foreach (var step in Refined)
                    {
                        waypoints.Add(step.Latitude);
                        waypoints.Add(step.Longitude);
                        length += NavalOperations.Distance(here.Item1, here.Item2, step.Latitude, step.Longitude);
                        here = step;
                    }
                    anchor = next;
                    continue;
                }
                waypoints.Add(point.Latitude);
                waypoints.Add(point.Longitude);
                length += NavalOperations.Distance(here.Item1, here.Item2, point.Latitude, point.Longitude);
                here = point;
                anchor = next;
            }
            return true;
        }
    }

    private static readonly List<(float Latitude, float Longitude)> Refined = new();

    /// <summary>
    /// A leg that does not clear the land in a straight line, found on the raster itself: a
    /// search over the water pixels of a window round the two ends, then the pixels pulled
    /// into as few straight lines as clear the land. Fills `into` with the points after
    /// `from`, ending with `to`; when no way is found in the window, just `to`.
    /// </summary>
    private static void Refine((float Latitude, float Longitude) from, (float Latitude, float Longitude) to,
        bool[] closed, List<(float Latitude, float Longitude)> into)
    {
        into.Clear();
        var start = PixelAt(from.Latitude, from.Longitude);
        var goal = PixelAt(to.Latitude, to.Longitude);
        int sx = start % _rasterWidth, sy = start / _rasterWidth, gx = goal % _rasterWidth, gy = goal / _rasterWidth;
        var margin = Math.Max(8, _rasterWidth / Columns);
        // Across the date line the window would be the width of the world; the graph never
        // makes a leg that long, and this is not the place to start.
        if (Math.Abs(gx - sx) > _rasterWidth / 2)
        {
            into.Add(to);
            return;
        }
        var x0 = Math.Max(0, Math.Min(sx, gx) - margin);
        var x1 = Math.Min(_rasterWidth - 1, Math.Max(sx, gx) + margin);
        var y0 = Math.Max(0, Math.Min(sy, gy) - margin);
        var y1 = Math.Min(_rasterHeight - 1, Math.Max(sy, gy) + margin);
        var w = x1 - x0 + 1;
        var h = y1 - y0 + 1;
        if ((long)w * h > 2_000_000)
        {
            into.Add(to);
            return;
        }

        var came = new int[w * h];
        Array.Fill(came, -2);
        var queue = new Queue<int>();
        var origin = (sy - y0) * w + (sx - x0);
        var target = (gy - y0) * w + (gx - x0);
        came[origin] = -1;
        queue.Enqueue(origin);
        var found = false;
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == target)
            {
                found = true;
                break;
            }
            var ax = at % w + x0;
            var ay = at / w + y0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = ax + dx;
                var ny = ay + dy;
                if (nx < x0 || nx > x1 || ny < y0 || ny > y1) continue;
                var local = (ny - y0) * w + (nx - x0);
                if (came[local] != -2) continue;
                if (local != target && !Passable(ny * _rasterWidth + nx, closed)) continue;
                // Not diagonally between two pixels of land: that is a corner, not a channel.
                if (dx != 0 && dy != 0 && !Passable(ay * _rasterWidth + nx, closed) &&
                    !Passable(ny * _rasterWidth + ax, closed)) continue;
                came[local] = at;
                queue.Enqueue(local);
            }
        }
        if (!found)
        {
            into.Add(to);
            return;
        }

        var path = new List<(float Latitude, float Longitude)>();
        for (var at = came[target]; at >= 0 && came[at] != -1; at = came[at])
            path.Add(PixelCentre((at / w + y0) * _rasterWidth + at % w + x0));
        path.Reverse();
        path.Insert(0, from);
        path.Add(to);

        var anchor = 0;
        while (anchor < path.Count - 1)
        {
            var next = anchor + 1;
            var misses = 0;
            for (var ahead = anchor + 2; ahead < path.Count && misses < 24; ahead++)
            {
                if (Clear(path[anchor], path[ahead], false, closed))
                {
                    next = ahead;
                    misses = 0;
                }
                else misses++;
            }
            into.Add(path[next]);
            anchor = next;
        }
    }

    /// <summary>Just whether two points share a body of water, for choosing between destinations.</summary>
    public static bool Reachable(Campaign campaign, float fromLatitude, float fromLongitude,
        float toLatitude, float toLongitude)
    {
        var map = campaign?.ProvinceMap;
        if (map is null) return true;
        lock (Gate)
        {
            Prepare(map, campaign);
            var start = NodeNear(PixelAt(fromLatitude, fromLongitude), null, out _);
            var goal = NodeNear(PixelAt(toLatitude, toLongitude), null, out _);
            return start >= 0 && goal >= 0 && _nodeBasin[start] == _nodeBasin[goal];
        }
    }

    // --- building the sea ----------------------------------------------------------------

    private static void Prepare(ProvinceTopology map, Campaign campaign)
    {
        var defines = campaign?.Defines;
        var ice = defines?.SeaLaneIceLatitude ?? (_ice < 0f ? 80f : _ice);
        var width = defines?.SeaLanePassageWidth ?? (_width < 0 ? 2 : _width);
        if (ReferenceEquals(map, _map) && _passageRevision == SeaPassageCatalog.Revision &&
            MathF.Abs(ice - _ice) < 0.0001f && width == _width) return;

        // More than one map can be alive at once - a bench stepping two peers side by side, a
        // client rebuilding from a snapshot beside the world it replaces - and each has its own
        // sea. Remembering only the last one rebuilt the whole graph, most of half a second, on
        // every switch between them: every hour, once an island garrison needed a port.
        if (_map is not null) Seas.AddOrUpdate(_map, Save());
        if (Seas.TryGetValue(map, out var kept) && kept.PassageRevision == SeaPassageCatalog.Revision &&
            MathF.Abs(kept.Ice - ice) < 0.0001f && kept.Width == width)
        {
            Load(kept);
            _map = map;
            return;
        }
        _map = map;
        _passageRevision = SeaPassageCatalog.Revision;
        _ice = ice;
        _width = width;
        Build(map, ice, Math.Clamp(width, 0, 8));
    }

    /// <summary>Everything worked out for one map, kept for as long as the map is.</summary>
    private sealed class Kept
    {
        public int PassageRevision, Width, RasterWidth, RasterHeight, IceTop, IceBottom, Nodes, Basins, Generation, FillGeneration;
        public float Ice;
        public ushort[] Raster;
        public int[] ColumnOf, RowOf, ColumnStart, RowStart, NodePixel, NodeBasin, BasinSize, CellFirst, LinkStart, Links,
            HarbourPixel, HarbourNode, Coastal, Holders, Came, Seen, Done, FillStamp;
        public Dictionary<int, byte> PassagePixels;
        public HashSet<int> ShutPixels;
        public bool[] Overridden;
        public float[] NodeLatitude, NodeLongitude, Cost;
        public short[] NodePassage;
        public Dictionary<long, int[]> ComponentsByMask;
    }

    private static readonly ConditionalWeakTable<ProvinceTopology, Kept> Seas = new();

    private static Kept Save() => new()
    {
        PassageRevision = _passageRevision, Ice = _ice, Width = _width,
        RasterWidth = _rasterWidth, RasterHeight = _rasterHeight, Raster = _raster, ColumnOf = _columnOf, RowOf = _rowOf,
        ColumnStart = ColumnStart, RowStart = RowStart, IceTop = _iceTop, IceBottom = _iceBottom,
        PassagePixels = PassagePixels, ShutPixels = ShutPixels, Overridden = Overridden,
        Nodes = _nodes, NodePixel = _nodePixel, NodeLatitude = _nodeLatitude, NodeLongitude = _nodeLongitude,
        NodePassage = _nodePassage, NodeBasin = _nodeBasin, BasinSize = _basinSize, CellFirst = CellFirst,
        LinkStart = _linkStart, Links = _links, Basins = _basins, HarbourPixel = _harbourPixel, HarbourNode = _harbourNode,
        Coastal = _coastal, Holders = _holders, ComponentsByMask = ComponentsByMask,
        Cost = _cost, Came = _came, Seen = _seen, Done = _done, Generation = _generation,
        FillStamp = _fillStamp, FillGeneration = _fillGeneration
    };

    private static void Load(Kept sea)
    {
        _passageRevision = sea.PassageRevision; _ice = sea.Ice; _width = sea.Width;
        _rasterWidth = sea.RasterWidth; _rasterHeight = sea.RasterHeight; _raster = sea.Raster;
        _columnOf = sea.ColumnOf; _rowOf = sea.RowOf; ColumnStart = sea.ColumnStart; RowStart = sea.RowStart;
        _iceTop = sea.IceTop; _iceBottom = sea.IceBottom;
        PassagePixels = sea.PassagePixels; ShutPixels = sea.ShutPixels; Overridden = sea.Overridden;
        _nodes = sea.Nodes; _nodePixel = sea.NodePixel; _nodeLatitude = sea.NodeLatitude; _nodeLongitude = sea.NodeLongitude;
        _nodePassage = sea.NodePassage; _nodeBasin = sea.NodeBasin; _basinSize = sea.BasinSize; CellFirst = sea.CellFirst;
        _linkStart = sea.LinkStart; _links = sea.Links; _basins = sea.Basins;
        _harbourPixel = sea.HarbourPixel; _harbourNode = sea.HarbourNode; _coastal = sea.Coastal; _holders = sea.Holders;
        ComponentsByMask = sea.ComponentsByMask;
        _cost = sea.Cost; _came = sea.Came; _seen = sea.Seen; _done = sea.Done; _generation = sea.Generation;
        _fillStamp = sea.FillStamp; _fillGeneration = sea.FillGeneration;
    }

    private static void Build(ProvinceTopology map, float ice, int width)
    {
        _rasterWidth = map.Width;
        _rasterHeight = map.Height;
        _raster = map.CellToProvince;
        ColumnStart = new int[Columns + 1];
        RowStart = new int[Rows + 1];
        CellFirst = new int[Cells + 1];
        PassagePixels = new Dictionary<int, byte>();
        ShutPixels = new HashSet<int>();
        Overridden = new bool[Cells];
        ComponentsByMask = new Dictionary<long, int[]>();
        var rasterWidth = _rasterWidth;
        var rasterHeight = _rasterHeight;

        _columnOf = new int[rasterWidth];
        for (var x = 0; x < rasterWidth; x++) _columnOf[x] = x * Columns / rasterWidth;
        _rowOf = new int[rasterHeight];
        for (var y = 0; y < rasterHeight; y++) _rowOf[y] = y * Rows / rasterHeight;
        for (var column = 0; column <= Columns; column++)
            ColumnStart[column] = (column * rasterWidth + Columns - 1) / Columns;
        for (var row = 0; row <= Rows; row++)
            RowStart[row] = (row * rasterHeight + Rows - 1) / Rows;
        _iceTop = Math.Clamp((int)MathF.Ceiling((90f - ice) / 180f * rasterHeight - 0.5f), 0, rasterHeight);
        _iceBottom = Math.Clamp((int)MathF.Floor((90f + ice) / 180f * rasterHeight - 0.5f) + 1, 0, rasterHeight);

        CutPassages(width);

        // Every square, row by row: its water split into the pieces that touch, each piece a
        // node, and each node joined to the pieces it touches in the squares to its west and
        // north - and, at the date line, to the east.
        var maxWidth = 0;
        var maxHeight = 0;
        for (var column = 0; column < Columns; column++) maxWidth = Math.Max(maxWidth, ColumnStart[column + 1] - ColumnStart[column]);
        for (var row = 0; row < Rows; row++) maxHeight = Math.Max(maxHeight, RowStart[row + 1] - RowStart[row]);
        var local = new int[maxWidth * maxHeight];
        var eastEdge = new int[rasterHeight];
        var westEdge = new int[rasterHeight];
        var southEdge = new int[rasterWidth];
        var northEdge = new int[rasterWidth];
        Array.Fill(southEdge, -1);

        var pixels = new List<int>();
        var nodePixel = new List<int>();
        var nodePassage = new List<short>();
        var links = new List<long>();
        var fill = new Stack<int>();

        for (var row = 0; row < Rows; row++)
        {
            var y0 = RowStart[row];
            var y1 = RowStart[row + 1];
            var h = y1 - y0;
            for (var column = 0; column < Columns; column++)
            {
                var x0 = ColumnStart[column];
                var x1 = ColumnStart[column + 1];
                var w = x1 - x0;
                var cell = row * Columns + column;
                CellFirst[cell] = nodePixel.Count;
                if (w <= 0 || h <= 0) continue;

                for (var index = 0; index < w * h; index++) local[index] = -1;
                for (var ly = 0; ly < h; ly++)
                for (var lx = 0; lx < w; lx++)
                {
                    var at = ly * w + lx;
                    if (local[at] != -1) continue;
                    var kind = Class((y0 + ly) * rasterWidth + x0 + lx);
                    if (kind == Land) { local[at] = -2; continue; }

                    // One piece: everything of the same kind this pixel's water reaches
                    // without leaving the square.
                    var node = nodePixel.Count;
                    pixels.Clear();
                    long sumX = 0, sumY = 0;
                    local[at] = node;
                    fill.Push(at);
                    while (fill.Count > 0)
                    {
                        var current = fill.Pop();
                        pixels.Add(current);
                        var cx = current % w;
                        var cy = current / w;
                        sumX += cx;
                        sumY += cy;
                        for (var direction = 0; direction < 4; direction++)
                        {
                            var nx = cx + (direction == 0 ? 1 : direction == 1 ? -1 : 0);
                            var ny = cy + (direction == 2 ? 1 : direction == 3 ? -1 : 0);
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            var neighbour = ny * w + nx;
                            if (local[neighbour] != -1) continue;
                            if (Class((y0 + ny) * rasterWidth + x0 + nx) != kind) continue;
                            local[neighbour] = node;
                            fill.Push(neighbour);
                        }
                    }

                    // It is drawn at the pixel of it nearest its own middle, so a ship heading
                    // for a bay heads for water rather than for the headland in its mouth.
                    var middleX = (float)sumX / pixels.Count;
                    var middleY = (float)sumY / pixels.Count;
                    var best = pixels[0];
                    var bestDistance = float.MaxValue;
                    foreach (var candidate in pixels)
                    {
                        var dx = candidate % w - middleX;
                        var dy = candidate / w - middleY;
                        var distance = dx * dx + dy * dy;
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        best = candidate;
                    }
                    nodePixel.Add((y0 + best / w) * rasterWidth + x0 + best % w);
                    nodePassage.Add(kind >= 2 ? (short)(kind - 2) : (short)-1);
                }

                // Pieces of different kinds side by side in the square - a canal meeting the
                // sea - are joined where they touch.
                for (var ly = 0; ly < h; ly++)
                for (var lx = 0; lx < w; lx++)
                {
                    var here = local[ly * w + lx];
                    if (here < 0) continue;
                    if (lx + 1 < w && local[ly * w + lx + 1] >= 0 && local[ly * w + lx + 1] != here)
                        Link(links, here, local[ly * w + lx + 1]);
                    if (ly + 1 < h && local[(ly + 1) * w + lx] >= 0 && local[(ly + 1) * w + lx] != here)
                        Link(links, here, local[(ly + 1) * w + lx]);
                }

                // Across the edges into squares already built.
                for (var ly = 0; ly < h; ly++)
                {
                    var here = local[ly * w];
                    if (column > 0 && here >= 0 && eastEdge[y0 + ly] >= 0) Link(links, here, eastEdge[y0 + ly]);
                    if (column == 0) westEdge[y0 + ly] = Math.Max(-1, here);
                }
                for (var lx = 0; lx < w; lx++)
                {
                    var here = local[lx];
                    if (row > 0 && here >= 0 && southEdge[x0 + lx] >= 0) Link(links, here, southEdge[x0 + lx]);
                }
                for (var ly = 0; ly < h; ly++)
                {
                    var edge = local[ly * w + w - 1];
                    eastEdge[y0 + ly] = Math.Max(-1, edge);
                    if (column == Columns - 1 && edge >= 0 && westEdge[y0 + ly] >= 0) Link(links, edge, westEdge[y0 + ly]);
                }
                for (var lx = 0; lx < w; lx++) northEdge[x0 + lx] = Math.Max(-1, local[(h - 1) * w + lx]);
            }
            // This row's southern edge is the next row's northern one.
            (southEdge, northEdge) = (northEdge, southEdge);
        }
        CellFirst[Cells] = nodePixel.Count;

        _nodes = nodePixel.Count;
        _nodePixel = nodePixel.ToArray();
        _nodePassage = nodePassage.ToArray();
        _nodeLatitude = new float[_nodes];
        _nodeLongitude = new float[_nodes];
        for (var node = 0; node < _nodes; node++)
            (_nodeLatitude[node], _nodeLongitude[node]) = PixelCentre(_nodePixel[node]);

        // Neighbours, packed: each node's list is a slice of one array.
        links.Sort();
        var degree = new int[_nodes + 1];
        long previous = -1;
        var unique = 0;
        foreach (var link in links)
        {
            if (link == previous) continue;
            previous = link;
            degree[(int)(link >> 32)]++;
            degree[(int)(link & 0xFFFFFFFF)]++;
            unique++;
        }
        _linkStart = new int[_nodes + 1];
        for (var node = 0; node < _nodes; node++) _linkStart[node + 1] = _linkStart[node] + degree[node];
        _links = new int[unique * 2];
        var cursor = new int[_nodes];
        previous = -1;
        foreach (var link in links)
        {
            if (link == previous) continue;
            previous = link;
            var a = (int)(link >> 32);
            var b = (int)(link & 0xFFFFFFFF);
            _links[_linkStart[a] + cursor[a]++] = b;
            _links[_linkStart[b] + cursor[b]++] = a;
        }

        // Bodies of water: everything each node reaches.
        _nodeBasin = new int[_nodes];
        Array.Fill(_nodeBasin, -1);
        _basins = 0;
        var sizes = new List<int>();
        var queue = new Queue<int>();
        for (var seed = 0; seed < _nodes; seed++)
        {
            if (_nodeBasin[seed] >= 0) continue;
            _nodeBasin[seed] = _basins;
            queue.Enqueue(seed);
            var size = 0;
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                size++;
                for (var index = _linkStart[node]; index < _linkStart[node + 1]; index++)
                {
                    var next = _links[index];
                    if (_nodeBasin[next] >= 0) continue;
                    _nodeBasin[next] = _basins;
                    queue.Enqueue(next);
                }
            }
            sizes.Add(size);
            _basins++;
        }
        _basinSize = sizes.ToArray();
        ComponentsByMask.Clear();

        _cost = new float[_nodes];
        _came = new int[_nodes];
        _seen = new int[_nodes];
        _done = new int[_nodes];
        _generation = 0;
        _fillStamp = new int[maxWidth * maxHeight];
        _fillGeneration = 0;

        FindHarbours(map);

        // Who holds each passage: the province on the ground nearest its middle.
        var passages = SeaPassageCatalog.All;
        _holders = new int[passages.Count];
        for (var index = 0; index < passages.Count; index++)
        {
            var passage = passages[index];
            _holders[index] = -1;
            if (passage.Points.Count == 0) continue;
            var at = passage.Holder ?? passage.Points[passage.Points.Count / 2];
            _holders[index] = ProvinceNear(map, at.Latitude, at.Longitude, 40);
        }
    }

    private static void Link(List<long> links, int a, int b)
    {
        if (a == b) return;
        if (a > b) (a, b) = (b, a);
        links.Add(((long)a << 32) | (uint)b);
    }

    /// <summary>
    /// Cut the passages into the water: a band a few pixels wide along each line, wide
    /// enough to take in the whole of a real strait so that shutting it shuts it.
    /// </summary>
    private static void CutPassages(int width)
    {
        PassagePixels.Clear();
        ShutPixels.Clear();
        Array.Clear(Overridden);
        var passages = SeaPassageCatalog.All;
        for (var index = 0; index < passages.Count && index < 250; index++)
        {
            var passage = passages[index];
            for (var point = 1; point < passage.Points.Count; point++)
            {
                var (fromLatitude, fromLongitude) = passage.Points[point - 1];
                var (toLatitude, toLongitude) = passage.Points[point];
                var fromX = (fromLongitude + 180f) / 360f * _rasterWidth;
                var fromY = (90f - fromLatitude) / 180f * _rasterHeight;
                var deltaX = Shortest(toLongitude - fromLongitude) / 360f * _rasterWidth;
                var deltaY = (fromLatitude - toLatitude) / 180f * _rasterHeight;
                var samples = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(deltaX), MathF.Abs(deltaY)) * 2f));
                for (var sample = 0; sample <= samples; sample++)
                {
                    var t = sample / (float)samples;
                    var cx = (int)MathF.Floor(fromX + deltaX * t);
                    var cy = (int)MathF.Floor(fromY + deltaY * t);
                    for (var dy = -width; dy <= width; dy++)
                    for (var dx = -width; dx <= width; dx++)
                    {
                        var y = cy + dy;
                        if (y < 0 || y >= _rasterHeight) continue;
                        var x = ((cx + dx) % _rasterWidth + _rasterWidth) % _rasterWidth;
                        var pixel = y * _rasterWidth + x;
                        if (passage.Open) PassagePixels[pixel] = (byte)(2 + index);
                        else ShutPixels.Add(pixel);
                        Overridden[_rowOf[y] * Columns + _columnOf[x]] = true;
                    }
                }
            }
        }
    }

    /// <summary>What a pixel is to a ship: land, sea, or a passage by its number plus two.</summary>
    private static byte Class(int pixel)
    {
        var y = pixel / _rasterWidth;
        if (y < _iceTop || y >= _iceBottom) return Land;
        if (Overridden[_rowOf[y] * Columns + _columnOf[pixel - y * _rasterWidth]])
        {
            if (PassagePixels.TryGetValue(pixel, out var passage)) return passage;
            if (ShutPixels.Contains(pixel)) return Land;
        }
        return _raster[pixel] == 0 ? Sea : Land;
    }

    /// <summary>
    /// A harbour for every coastal province: the pixel of sea beside the stretch of its coast
    /// nearest its own middle. Only squares with both land and sea in them can hold a coast,
    /// so only those are read.
    /// </summary>
    private static void FindHarbours(ProvinceTopology map)
    {
        var provinces = map.ProvinceCount;
        _harbourPixel = new int[provinces + 1];
        _harbourNode = new int[provinces + 1];
        Array.Fill(_harbourPixel, -1);
        Array.Fill(_harbourNode, -1);
        var bestScore = new long[provinces + 1];
        Array.Fill(bestScore, long.MaxValue);
        var centreX = new int[provinces + 1];
        var centreY = new int[provinces + 1];
        for (var province = 1; province <= provinces; province++)
        {
            var centre = map.ProvinceCenterCell[province];
            centreX[province] = centre % _rasterWidth;
            centreY[province] = centre / _rasterWidth;
        }

        for (var cell = 0; cell < Cells; cell++)
        {
            if (CellFirst[cell + 1] == CellFirst[cell]) continue;
            var row = cell / Columns;
            var column = cell % Columns;
            for (var y = RowStart[row]; y < RowStart[row + 1]; y++)
            for (var x = ColumnStart[column]; x < ColumnStart[column + 1]; x++)
            {
                var pixel = y * _rasterWidth + x;
                var province = _raster[pixel];
                if (province == 0 || province > provinces || map.WaterByProvince[province]) continue;
                if (Class(pixel) != Land) continue;
                var touching = -1;
                var east = y * _rasterWidth + (x + 1) % _rasterWidth;
                var west = y * _rasterWidth + (x - 1 + _rasterWidth) % _rasterWidth;
                if (Class(east) != Land) touching = east;
                else if (Class(west) != Land) touching = west;
                else if (y > 0 && Class(pixel - _rasterWidth) != Land) touching = pixel - _rasterWidth;
                else if (y + 1 < _rasterHeight && Class(pixel + _rasterWidth) != Land) touching = pixel + _rasterWidth;
                if (touching < 0) continue;

                var dx = Math.Abs(x - centreX[province]);
                dx = Math.Min(dx, _rasterWidth - dx);
                var dy = y - centreY[province];
                var score = (long)dx * dx + (long)dy * dy;
                if (score >= bestScore[province]) continue;
                bestScore[province] = score;
                _harbourPixel[province] = touching;
            }
        }

        // A harbour on a pond is no harbour. Where the nearest stretch of coast faces one, the
        // coast is read again for the nearest stretch that faces the open sea.
        var retry = new bool[provinces + 1];
        var retries = 0;
        for (var province = 1; province <= provinces; province++)
        {
            if (_harbourPixel[province] < 0) continue;
            _harbourNode[province] = NodeOfPixel(_harbourPixel[province]);
            if (_harbourNode[province] >= 0 && _basinSize[_nodeBasin[_harbourNode[province]]] >= OpenSeaPieces) continue;
            retry[province] = true;
            retries++;
            _harbourPixel[province] = -1;
            _harbourNode[province] = -1;
            bestScore[province] = long.MaxValue;
        }
        if (retries > 0)
        {
            var sides = new int[4];
            for (var cell = 0; cell < Cells; cell++)
            {
                if (CellFirst[cell + 1] == CellFirst[cell]) continue;
                var row = cell / Columns;
                var column = cell % Columns;
                for (var y = RowStart[row]; y < RowStart[row + 1]; y++)
                for (var x = ColumnStart[column]; x < ColumnStart[column + 1]; x++)
                {
                    var pixel = y * _rasterWidth + x;
                    var province = _raster[pixel];
                    if (province == 0 || province > provinces || !retry[province]) continue;
                    if (Class(pixel) != Land) continue;
                    var dx = Math.Abs(x - centreX[province]);
                    dx = Math.Min(dx, _rasterWidth - dx);
                    var dy = y - centreY[province];
                    var score = (long)dx * dx + (long)dy * dy;
                    if (score >= bestScore[province]) continue;
                    sides[0] = y * _rasterWidth + (x + 1) % _rasterWidth;
                    sides[1] = y * _rasterWidth + (x - 1 + _rasterWidth) % _rasterWidth;
                    sides[2] = y > 0 ? pixel - _rasterWidth : -1;
                    sides[3] = y + 1 < _rasterHeight ? pixel + _rasterWidth : -1;
                    foreach (var touching in sides)
                    {
                        if (touching < 0 || Class(touching) == Land) continue;
                        var node = NodeOfPixel(touching);
                        if (node < 0 || _basinSize[_nodeBasin[node]] < OpenSeaPieces) continue;
                        bestScore[province] = score;
                        _harbourPixel[province] = touching;
                        _harbourNode[province] = node;
                        break;
                    }
                }
            }
        }

        var coastal = new List<int>();
        for (var province = 1; province <= provinces; province++)
            if (_harbourNode[province] >= 0) coastal.Add(province);
        _coastal = coastal.ToArray();
    }

    private static int ProvinceNear(ProvinceTopology map, float latitude, float longitude, int radius)
    {
        var width = map.Width;
        var height = map.Height;
        var cx = Math.Clamp((int)((longitude + 180f) / 360f * width), 0, width - 1);
        var cy = Math.Clamp((int)((90f - latitude) / 180f * height), 0, height - 1);
        var bestProvince = -1;
        var bestDistance = int.MaxValue;
        for (var dy = -radius; dy <= radius; dy++)
        {
            var y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (var dx = -radius; dx <= radius; dx++)
            {
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                // A search radius can exceed an entire small scenario/test raster.
                // C# remainder stays negative; adding width once is not sufficient.
                var x = (cx + dx) % width;
                if (x < 0) x += width;
                var province = map.CellToProvince[y * width + x];
                if (province == 0 || map.IsWater(province)) continue;
                bestDistance = distance;
                bestProvince = province;
            }
        }
        return bestProvince;
    }

    // --- finding one's way ---------------------------------------------------------------

    private static int PixelAt(float latitude, float longitude)
    {
        var x = (int)MathF.Floor((longitude + 180f) / 360f * _rasterWidth);
        x = ((x % _rasterWidth) + _rasterWidth) % _rasterWidth;
        var y = Math.Clamp((int)MathF.Floor((90f - latitude) / 180f * _rasterHeight), 0, _rasterHeight - 1);
        return y * _rasterWidth + x;
    }

    private static (float Latitude, float Longitude) PixelCentre(int pixel)
    {
        var x = pixel % _rasterWidth;
        var y = pixel / _rasterWidth;
        return (90f - (y + 0.5f) * 180f / _rasterHeight, -180f + (x + 0.5f) * 360f / _rasterWidth);
    }

    private static Dictionary<long, int[]> ComponentsByMask = new();

    /// <summary>
    /// Which body of water each piece belongs to with these passages shut, worked out once
    /// for each set of shut passages the world actually produces - a handful - and kept.
    /// </summary>
    private static int[] Components(bool[] closed)
    {
        long mask = 0;
        for (var index = 0; index < closed.Length && index < 62; index++)
            if (closed[index]) mask |= 1L << index;
        if (mask == 0) return _nodeBasin;
        if (ComponentsByMask.TryGetValue(mask, out var known)) return known;

        var parts = new int[_nodes];
        Array.Fill(parts, -1);
        var label = 0;
        var queue = new Queue<int>();
        for (var seed = 0; seed < _nodes; seed++)
        {
            if (parts[seed] >= 0) continue;
            parts[seed] = label;
            if (!NodeOpen(seed, closed)) { label++; continue; }
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                for (var index = _linkStart[node]; index < _linkStart[node + 1]; index++)
                {
                    var next = _links[index];
                    if (parts[next] >= 0 || !NodeOpen(next, closed)) continue;
                    parts[next] = label;
                    queue.Enqueue(next);
                }
            }
            label++;
        }
        ComponentsByMask[mask] = parts;
        return parts;
    }

    private static bool[] ClosedPassages(Campaign campaign, int nationId)
    {
        var passages = SeaPassageCatalog.All;
        if (campaign is null || passages.Count == 0 || nationId < 0) return null;
        bool[] closed = null;
        var belligerent = -1;
        for (var index = 0; index < passages.Count && index < _holders.Length; index++)
        {
            var passage = passages[index];
            if (!passage.ClosesToEnemies && !passage.ClosesToBelligerents) continue;
            var holder = NavalOperations.HolderOf(campaign, _holders[index]);
            if (holder is null || holder.Id == nationId) continue;
            var shut = passage.ClosesToEnemies && CampaignDiplomacy.AtWar(campaign, holder.Id, nationId);
            if (!shut && passage.ClosesToBelligerents)
            {
                // At war with anybody, and the holder not fighting beside it.
                if (belligerent < 0) belligerent = AtWarWithAnybody(campaign, nationId) ? 1 : 0;
                shut = belligerent == 1 && !FightsBeside(campaign, holder.Id, nationId);
            }
            if (!shut) continue;
            closed ??= new bool[passages.Count];
            closed[index] = true;
        }
        return closed;
    }

    private static bool AtWarWithAnybody(Campaign campaign, int nationId)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nationId)) return true;
        return false;
    }

    private static bool FightsBeside(Campaign campaign, int holderId, int nationId)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(holderId) && war.Involves(nationId) && !war.Opposes(holderId, nationId))
                return true;
        return false;
    }

    private static bool Passable(int pixel, bool[] closed)
    {
        var kind = Class(pixel);
        if (kind == Land) return false;
        return kind == Sea || closed is null || kind - 2 >= closed.Length || !closed[kind - 2];
    }

    private static bool NodeOpen(int node, bool[] closed)
    {
        var passage = _nodePassage[node];
        return passage < 0 || closed is null || passage >= closed.Length || !closed[passage];
    }

    /// <summary>Which piece of water a pixel belongs to, found by filling its square from it.</summary>
    private static int NodeOfPixel(int pixel)
    {
        var kind = Class(pixel);
        if (kind == Land) return -1;
        var x = pixel % _rasterWidth;
        var y = pixel / _rasterWidth;
        var row = _rowOf[y];
        var column = _columnOf[x];
        var cell = row * Columns + column;
        var first = CellFirst[cell];
        var last = CellFirst[cell + 1];
        if (last - first == 1) return first;
        if (last == first) return -1;

        var x0 = ColumnStart[column];
        var y0 = RowStart[row];
        var w = ColumnStart[column + 1] - x0;
        var h = RowStart[row + 1] - y0;
        if (++_fillGeneration == int.MaxValue) { Array.Clear(_fillStamp); _fillGeneration = 1; }
        FillStack.Clear();
        var start = (y - y0) * w + (x - x0);
        _fillStamp[start] = _fillGeneration;
        FillStack.Push(start);
        while (FillStack.Count > 0)
        {
            var current = FillStack.Pop();
            var cx = current % w;
            var cy = current / w;
            var at = (y0 + cy) * _rasterWidth + x0 + cx;
            for (var node = first; node < last; node++)
                if (_nodePixel[node] == at) { FillStack.Clear(); return node; }
            for (var direction = 0; direction < 4; direction++)
            {
                var nx = cx + (direction == 0 ? 1 : direction == 1 ? -1 : 0);
                var ny = cy + (direction == 2 ? 1 : direction == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var neighbour = ny * w + nx;
                if (_fillStamp[neighbour] == _fillGeneration) continue;
                if (Class((y0 + ny) * _rasterWidth + x0 + nx) != kind) continue;
                _fillStamp[neighbour] = _fillGeneration;
                FillStack.Push(neighbour);
            }
        }
        return -1;
    }

    /// <summary>The nearest pixel of open water within a radius, or -1.</summary>
    private static int NearestWaterPixel(int pixel, int radius, bool[] closed)
    {
        if (Passable(pixel, closed)) return pixel;
        var x = pixel % _rasterWidth;
        var y = pixel / _rasterWidth;
        for (var ring = 1; ring <= radius; ring++)
        {
            var best = -1;
            var bestDistance = int.MaxValue;
            for (var dy = -ring; dy <= ring; dy++)
            {
                var ny = y + dy;
                if (ny < 0 || ny >= _rasterHeight) continue;
                var step = Math.Abs(dy) == ring ? 1 : ring * 2;
                for (var dx = -ring; dx <= ring; dx += step)
                {
                    var candidate = ny * _rasterWidth + ((x + dx) % _rasterWidth + _rasterWidth) % _rasterWidth;
                    if (!Passable(candidate, closed)) continue;
                    var distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = candidate;
                }
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    /// <summary>
    /// The piece of water at a pixel, or at the nearest open sea to it; -1 far inland. A pond
    /// is taken only when there is no sea at all within reach, which is a fleet a mod put on
    /// a pond on purpose.
    /// </summary>
    private static int NodeNear(int pixel, bool[] closed, out int water)
    {
        water = -1;
        var fallback = -1;
        var fallbackPixel = -1;
        var x = pixel % _rasterWidth;
        var y = pixel / _rasterWidth;
        for (var ring = 0; ring <= 48; ring++)
        {
            var best = -1;
            var bestPixel = -1;
            var bestDistance = int.MaxValue;
            for (var dy = -ring; dy <= ring; dy++)
            {
                var ny = y + dy;
                if (ny < 0 || ny >= _rasterHeight) continue;
                var step = ring == 0 || Math.Abs(dy) == ring ? 1 : ring * 2;
                for (var dx = -ring; dx <= ring; dx += step)
                {
                    var candidate = ny * _rasterWidth + ((x + dx) % _rasterWidth + _rasterWidth) % _rasterWidth;
                    var distance = dx * dx + dy * dy;
                    if (distance >= bestDistance || !Passable(candidate, closed)) continue;
                    var node = NodeOfPixel(candidate);
                    if (node < 0 || !NodeOpen(node, closed)) continue;
                    if (_basinSize[_nodeBasin[node]] < OpenSeaPieces)
                    {
                        if (fallback < 0) { fallback = node; fallbackPixel = candidate; }
                        continue;
                    }
                    bestDistance = distance;
                    best = node;
                    bestPixel = candidate;
                }
            }
            if (best >= 0) { water = bestPixel; return best; }
        }
        water = fallbackPixel;
        return fallback;
    }

    /// <summary>
    /// Whether a straight line between two points stays on water the whole way, pixel by
    /// pixel. A course that starts in a port may start a pixel or three inland, where the
    /// port is drawn; nothing else may touch land.
    /// </summary>
    private static bool Clear((float Latitude, float Longitude) from, (float Latitude, float Longitude) to,
        bool leavingPort, bool[] closed)
    {
        var deltaLatitude = to.Latitude - from.Latitude;
        var deltaLongitude = Shortest(to.Longitude - from.Longitude);
        var pixelDegrees = 360f / _rasterWidth;
        var span = MathF.Max(MathF.Abs(deltaLatitude), MathF.Abs(deltaLongitude));
        // Half a pixel at a time: a whole pixel's stride steps clean over the corner of a
        // headland, and a course that clips one is a course across land.
        var samples = Math.Max(1, (int)MathF.Ceiling(span / pixelDegrees * 2f));
        var ashore = leavingPort ? 8 : 0;
        for (var sample = 0; sample <= samples; sample++)
        {
            var t = sample / (float)samples;
            var pixel = PixelAt(from.Latitude + deltaLatitude * t, Wrap(from.Longitude + deltaLongitude * t));
            if (Passable(pixel, closed)) { ashore = 0; continue; }
            if (ashore-- > 0 && sample < 16) continue;
            return false;
        }
        return true;
    }

    private static bool Search(int start, int goal, bool[] closed)
    {
        PathNodes.Clear();
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_seen);
            Array.Clear(_done);
            _generation = 1;
        }
        var generation = _generation;
        var goalLatitude = _nodeLatitude[goal];
        var goalLongitude = _nodeLongitude[goal];
        Frontier.Clear();
        _cost[start] = 0f;
        _came[start] = -1;
        _seen[start] = generation;
        Frontier.Enqueue(start, 0f);
        var found = false;
        while (Frontier.TryDequeue(out var node, out _))
        {
            if (node == goal) { found = true; break; }
            if (_done[node] == generation) continue;
            _done[node] = generation;
            var latitude = _nodeLatitude[node];
            var longitude = _nodeLongitude[node];
            for (var index = _linkStart[node]; index < _linkStart[node + 1]; index++)
            {
                var next = _links[index];
                if (_done[next] == generation || !NodeOpen(next, closed)) continue;
                var cost = _cost[node] + NavalOperations.Distance(latitude, longitude,
                    _nodeLatitude[next], _nodeLongitude[next]);
                if (_seen[next] == generation && cost >= _cost[next]) continue;
                _seen[next] = generation;
                _cost[next] = cost;
                _came[next] = node;
                Frontier.Enqueue(next, cost + NavalOperations.Distance(_nodeLatitude[next], _nodeLongitude[next],
                    goalLatitude, goalLongitude));
            }
        }
        Frontier.Clear();
        if (!found) return false;
        for (var node = goal; node >= 0; node = _came[node]) PathNodes.Add(node);
        PathNodes.Reverse();
        return true;
    }

    private static float Shortest(float delta)
    {
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return delta;
    }

    private static float Wrap(float longitude)
    {
        while (longitude >= 180f) longitude -= 360f;
        while (longitude < -180f) longitude += 360f;
        return longitude;
    }
}
