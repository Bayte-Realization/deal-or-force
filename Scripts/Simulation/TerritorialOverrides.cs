using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class TerritorialTopology
{
    public static void LoadOverrides(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Array)
        foreach (var value in regions.EnumerateArray())
        {
            try
            {
                var rule = new RegionRule(
                    value.GetProperty("map_index").GetInt32(), value.GetProperty("nation").GetString() ?? "",
                    value.TryGetProperty("source_nation", out var sourceNation) ? sourceNation.GetString() ?? "" : "",
                    value.GetProperty("name").GetString() ?? "", value.GetProperty("iso").GetString() ?? "",
                    value.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "Region" : "Region",
                    value.TryGetProperty("donor_iso", out var donor) ? donor.GetString() ?? "" : "",
                    value.TryGetProperty("population", out var population) ? population.GetInt64() : 0,
                    value.GetProperty("min_longitude").GetSingle(), value.GetProperty("max_longitude").GetSingle(),
                    value.GetProperty("min_latitude").GetSingle(), value.GetProperty("max_latitude").GetSingle(),
                    value.TryGetProperty("polygon", out var polygon) ? ParsePolygon(polygon) : Array.Empty<GeoPoint>(),
                    value.TryGetProperty("seed_points", out var seeds) ? ParseSeeds(seeds) : Array.Empty<RegionSeed>(),
                    value.TryGetProperty("carve_from_iso", out var carveFrom) ? carveFrom.GetString() ?? "" : "",
                    value.TryGetProperty("replace_existing", out var replace) && replace.GetBoolean());
                var index = RegionRules.FindIndex(item => item.MapIndex == rule.MapIndex);
                if (index >= 0) RegionRules[index] = rule; else RegionRules.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data region override: {exception.Message}"); }
        }
        if (document.RootElement.TryGetProperty("partitions", out var partitions) &&
            partitions.ValueKind == JsonValueKind.Array)
        foreach (var value in partitions.EnumerateArray())
        {
            try
            {
                var pieces = new List<RegionPiece>();
                if (value.TryGetProperty("pieces", out var listed) && listed.ValueKind == JsonValueKind.Array)
                    foreach (var piece in listed.EnumerateArray())
                    {
                        var seed = piece.GetProperty("seed");
                        pieces.Add(new RegionPiece(
                            piece.GetProperty("map_index").GetInt32(),
                            piece.GetProperty("name").GetString() ?? "",
                            piece.TryGetProperty("iso", out var iso) ? iso.GetString() ?? "" : "",
                            piece.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "Region" : "Region",
                            piece.TryGetProperty("population", out var people) ? people.GetInt64() : 0,
                            seed[0].GetSingle(), seed[1].GetSingle()));
                    }
                if (pieces.Count < 2) continue;
                var also = new List<string>();
                if (value.TryGetProperty("also_carve_from_iso", out var more) && more.ValueKind == JsonValueKind.Array)
                    foreach (var iso in more.EnumerateArray())
                        if (iso.GetString() is { Length: > 0 } code) also.Add(code);
                Partitions.Add(new RegionPartition(
                    value.GetProperty("nation").GetString() ?? "",
                    value.GetProperty("carve_from_iso").GetString() ?? "",
                    pieces.ToArray(),
                    also.ToArray(),
                    value.TryGetProperty("polar", out var polar) && polar.ValueKind == JsonValueKind.True));
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data partition: {exception.Message}"); }
        }

        if (document.RootElement.TryGetProperty("cell_transfers", out var transfers) && transfers.ValueKind == JsonValueKind.Array)
        foreach (var value in transfers.EnumerateArray())
        {
            try
            {
                var points = value.GetProperty("points").EnumerateArray().Select(p =>
                    new GeoPoint(p[0].GetSingle(), p[1].GetSingle())).ToArray();
                if (points.Length == 0 || points.Any(p => !float.IsFinite(p.Longitude) ||
                    !float.IsFinite(p.Latitude) || Math.Abs(p.Longitude) > 180 || Math.Abs(p.Latitude) > 90))
                    throw new InvalidDataException("cell transfers require finite geographic points");
                var rule = new CellTransfer(value.GetProperty("id").GetString() ?? "",
                    value.GetProperty("from_iso").GetString() ?? "", value.GetProperty("to_iso").GetString() ?? "",
                    value.GetProperty("from_nation").GetString() ?? "", value.GetProperty("to_nation").GetString() ?? "", points);
                if (string.IsNullOrWhiteSpace(rule.Id)) throw new InvalidDataException("cell transfer needs an id");
                CellTransfers.RemoveAll(r => r.Id == rule.Id);
                CellTransfers.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data cell transfer: {exception.Message}"); }
        }

        if (document.RootElement.TryGetProperty("adjacency_repairs", out var adjacency) && adjacency.ValueKind == JsonValueKind.Array)
        foreach (var value in adjacency.EnumerateArray())
        {
            try
            {
                var rule = new AdjacencyRule(
                    value.GetProperty("subject").GetString() ?? "", value.GetProperty("neighbor").GetString() ?? "",
                    value.GetProperty("through").GetString() ?? "", value.GetProperty("min_longitude").GetSingle(),
                    value.GetProperty("max_longitude").GetSingle(), value.GetProperty("min_latitude").GetSingle(),
                    value.GetProperty("max_latitude").GetSingle(), value.GetProperty("corridor_radius").GetInt32());
                var index = BaseRules.FindIndex(item => item.SubjectTag == rule.SubjectTag && item.NeighborTag == rule.NeighborTag);
                if (index >= 0) BaseRules[index] = rule; else BaseRules.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data adjacency repair: {exception.Message}"); }
        }
    }


    /// <summary>
    /// Which region currently holds most of the ground a rule is about to take. Used to
    /// find the donor when a rule does not name one, so authoring a new region is a
    /// bounding box and a population rather than a bounding box, a population, and a
    /// correct guess about what is underneath it.
    /// </summary>
    private static int DominantState(ushort[] control, ushort[] states, int width, int height,
        ushort sourceNation, RegionRule rule)
    {
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var tally = new Dictionary<ushort, int>();

        void Count(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var cell = y * width + x;
            if (control[cell] != sourceNation) return;
            if (rule.Polygon.Length >= 3 && !Contains(rule.Polygon, XToLongitude(x, width), YToLatitude(y, height)))
                return;
            var state = states[cell];
            if (state == 0) return;
            tally[state] = tally.GetValueOrDefault(state) + 1;
        }

        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            Count(x, y);

        foreach (var seed in rule.Seeds)
        {
            var centerX = LongitudeToX(seed.Longitude, width);
            var centerY = LatitudeToY(seed.Latitude, height);
            var radius = Math.Clamp(seed.RadiusCells, 0, 4);
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                Count(centerX + dx, centerY + dy);
        }

        return tally.Count == 0 ? -1 : tally.OrderByDescending(entry => entry.Value).First().Key;
    }

    private static int PaintRegion(
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        ushort nation,
        ushort sourceNation,
        ushort state,
        RegionRule rule,
        ushort carveFrom,
        out int connectedComponents,
        out string clippedAway)
    {
        // Ground the boundary rejected that the bounding box would have taken. A traced
        // border is only right if what it leaves out is the ground that is not the region:
        // reporting the leftovers as separate pieces with their coordinates is the
        // difference between drawing a border and guessing at one.
        var rejected = new HashSet<int>();
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var changed = 0;
        // The polygon, rasterised once for this box. Asking `Contains` per cell is what made a
        // campaign load take minutes; the mask is the same answer from the same float crossings,
        // and `VerifyPolygonMasks` proves the two agree over every rule in the catalogue.
        var maskWidth = maxX - minX + 1;
        var polygonMask = rule.Polygon.Length >= 3
            ? PolygonMask(rule.Polygon, minX, maxX, minY, maxY, width, height)
            : null;
        bool Inside(int x, int y) => polygonMask is null || polygonMask[(y - minY) * maskWidth + (x - minX)];
        // Replacing a placeholder contour must remove its old outside pixels, not union
        // a traced polygon with the circle. Never take land from unrelated neighbours.
        if (rule.ReplaceExisting && rule.Polygon.Length >= 3 && sourceNation != nation)
        {
            var donors = new List<(int X, int Y, ushort State)>();
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                if (control[y * width + x] == sourceNation && states[y * width + x] != state && states[y * width + x] != 0)
                    donors.Add((x, y, states[y * width + x]));
            // The donors as a grid over the box, so the nearest is found ring by ring around the
            // cell rather than by measuring every one of them for every cell: the same donor the
            // whole list would give, ties to the first in the list, in a fraction of the time.
            var boxWidth = maxX - minX + 1;
            var boxHeight = maxY - minY + 1;
            var donorGrid = donors.Count == 0 ? null : new ushort[boxWidth * boxHeight];
            foreach (var donor in donors) donorGrid[(donor.Y - minY) * boxWidth + (donor.X - minX)] = donor.State;
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
            {
                var cell = y * width + x;
                if (control[cell] != nation || donors.Count == 0 || Inside(x, y)) continue;
                control[cell] = sourceNation; states[cell] = NearestDonor(donorGrid, boxWidth, boxHeight, x - minX, y - minY); changed++;
            }
        }
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var cell = y * width + x;
            if ((control[cell] != sourceNation && control[cell] != nation) || states[cell] == state) continue;
            if (carveFrom != 0 && states[cell] != carveFrom) continue;
            if (!Inside(x, y))
            {
                rejected.Add(cell);
                continue;
            }
            control[cell] = nation;
            states[cell] = state;
            changed++;
        }
        if (rule.Polygon.Length >= 3)
            changed += BridgeRegionComponents(control, states, width, height, nation, sourceNation, state,
                minX, maxX, minY, maxY, 14);
        changed += PaintSeeds(control, states, width, height, nation, sourceNation, state, rule.Seeds);
        connectedComponents = rule.Polygon.Length >= 3
            ? RegionComponents(states, width, height, state, minX, maxX, minY, maxY).Count
            : 0;
        rejected.RemoveWhere(cell => states[cell] == state);
        clippedAway = Describe(rejected, width, height);
        return changed;
    }

    /// <summary>
    /// The donor nearest a cell of the box, as the scan-ordered list's first minimum would have
    /// it: least squared distance, then the lower row, then the lower column. A donor on the
    /// ring r cells out is at least r away, so the search stops at the first ring beyond the
    /// best distance found - after taking in that ring, which can hold an equal one.
    /// </summary>
    private static ushort NearestDonor(ushort[] grid, int boxWidth, int boxHeight, int x, int y)
    {
        var best = int.MaxValue;
        int bestX = 0, bestY = 0;
        ushort found = 0;
        void Consider(int cx, int cy)
        {
            if (cx < 0 || cx >= boxWidth || cy < 0 || cy >= boxHeight) return;
            var state = grid[cy * boxWidth + cx];
            if (state == 0) return;
            var distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance > best || distance == best && (cy > bestY || cy == bestY && cx >= bestX)) return;
            best = distance; bestX = cx; bestY = cy; found = state;
        }
        for (var r = 0; ; r++)
        {
            if (found != 0 && (long)r * r > best) break;
            if (x - r < 0 && x + r >= boxWidth && y - r < 0 && y + r >= boxHeight) break;
            if (r == 0) { Consider(x, y); continue; }
            for (var cx = x - r; cx <= x + r; cx++) { Consider(cx, y - r); Consider(cx, y + r); }
            for (var cy = y - r + 1; cy <= y + r - 1; cy++) { Consider(x - r, cy); Consider(x + r, cy); }
        }
        return found;
    }

    /// <summary>
    /// Break a loose set of cells into touching pieces and name each one by its size and
    /// the box it occupies, so a border can be judged against what it excluded.
    /// </summary>
    private static string Describe(HashSet<int> cells, int width, int height)
    {
        if (cells.Count == 0) return "";
        var seen = new HashSet<int>();
        var pieces = new List<(int Count, float West, float East, float South, float North)>();
        var queue = new Queue<int>();
        foreach (var start in cells)
        {
            if (!seen.Add(start)) continue;
            queue.Clear();
            queue.Enqueue(start);
            var count = 0;
            float west = 999f, east = -999f, south = 999f, north = -999f;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                count++;
                var longitude = XToLongitude(cell % width, width);
                var latitude = YToLatitude(cell / width, height);
                west = MathF.Min(west, longitude);
                east = MathF.Max(east, longitude);
                south = MathF.Min(south, latitude);
                north = MathF.Max(north, latitude);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var y = cell / width + dy;
                    if (y < 0 || y >= height) continue;
                    var neighbour = y * width + (cell % width + dx + width) % width;
                    if (cells.Contains(neighbour) && seen.Add(neighbour)) queue.Enqueue(neighbour);
                }
            }
            pieces.Add((count, west, east, south, north));
        }
        pieces.Sort((a, b) => b.Count.CompareTo(a.Count));
        return string.Join(", ", pieces.Take(6).Select(piece =>
            $"{piece.Count} cell(s) at {piece.West:0.00}-{piece.East:0.00}E {piece.South:0.00}-{piece.North:0.00}N"));
    }

    private static int PaintSeeds(ushort[] control, ushort[] states, int width, int height,
        ushort nation, ushort sourceNation, ushort state, IReadOnlyList<RegionSeed> seeds)
    {
        var changed = 0;
        foreach (var seed in seeds)
        {
            var centerX = LongitudeToX(seed.Longitude, width);
            var centerY = LatitudeToY(seed.Latitude, height);
            var radius = Math.Clamp(seed.RadiusCells, 0, 4);
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius + 1 || centerY + dy < 0 || centerY + dy >= height)
                    continue;
                var x = (centerX + dx + width) % width;
                var cell = (centerY + dy) * width + x;
                if (control[cell] != 0 && control[cell] != sourceNation && control[cell] != nation) continue;
                if (control[cell] == nation && states[cell] == state) continue;
                control[cell] = nation;
                states[cell] = state;
                changed++;
            }
        }
        return changed;
    }


    private static GeoPoint[] ParsePolygon(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<GeoPoint>();
        var points = new List<GeoPoint>();
        foreach (var point in value.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;
            points.Add(new GeoPoint(point[0].GetSingle(), point[1].GetSingle()));
        }
        return points.Count >= 3 ? points.ToArray() : Array.Empty<GeoPoint>();
    }

    private static RegionSeed[] ParseSeeds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<RegionSeed>();
        var seeds = new List<RegionSeed>();
        foreach (var seed in value.EnumerateArray())
        {
            if (seed.ValueKind != JsonValueKind.Array || seed.GetArrayLength() < 2) continue;
            seeds.Add(new RegionSeed(seed[0].GetSingle(), seed[1].GetSingle(),
                seed.GetArrayLength() >= 3 ? seed[2].GetInt32() : 1));
        }
        return seeds.ToArray();
    }

    private static bool Contains(IReadOnlyList<GeoPoint> polygon, float longitude, float latitude)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Latitude > latitude) == (b.Latitude > latitude)) continue;
            var crossing = (b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                (b.Latitude - a.Latitude) + a.Longitude;
            if (longitude < crossing) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// The same inside/outside test as <see cref="Contains"/>, asked once per pixel of the rule's
    /// box instead of once per cell per edge.
    ///
    /// `Contains` walks every edge of the polygon for every cell, so a traced border with a few
    /// hundred points costs hundreds of float operations per cell - and a campaign carries a few
    /// hundred of these rules, each over a box of tens of thousands of cells. That is where the
    /// minutes at load went.
    ///
    /// The arithmetic is not approximated, it is reordered. For a fixed latitude the crossings
    /// are the same numbers whatever the longitude, and the answer only depends on the parity of
    /// how many of them lie to the right of the cell. So the crossings are computed once per row,
    /// sorted, and each cell then costs a walk along them. Every `crossing` is the same float
    /// expression as in `Contains`, and every comparison is `longitude < crossing` against the
    /// same `XToLongitude(x, width)`, so the mask is the polygon the old test described - which
    /// `VerifyPolygonMasks` proves over every rule in the catalogue.
    /// </summary>
    private static bool[] PolygonMask(IReadOnlyList<GeoPoint> polygon, int minX, int maxX, int minY, int maxY,
        int width, int height)
    {
        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        var mask = new bool[boxWidth * boxHeight];
        if (boxWidth <= 0 || boxHeight <= 0) return mask;
        var crossings = new List<float>(polygon.Count);
        for (var y = minY; y <= maxY; y++)
        {
            var latitude = YToLatitude(y, height);
            crossings.Clear();
            for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
            {
                var a = polygon[current];
                var b = polygon[previous];
                if ((a.Latitude > latitude) == (b.Latitude > latitude)) continue;
                crossings.Add((b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                    (b.Latitude - a.Latitude) + a.Longitude);
            }
            if (crossings.Count == 0) continue;
            crossings.Sort();
            var row = (y - minY) * boxWidth;
            var next = 0;   // crossings[next] is the first one still to the right of the cell
            for (var x = minX; x <= maxX; x++)
            {
                var longitude = XToLongitude(x, width);
                while (next < crossings.Count && !(longitude < crossings[next])) next++;
                // Parity of the crossings to the right: exactly what the toggling loop counts.
                mask[row + (x - minX)] = ((crossings.Count - next) & 1) == 1;
            }
        }
        return mask;
    }

    /// <summary>
    /// Compares the rasterised mask against the per-cell test for every region rule that has a
    /// polygon, over its whole box. Returns the number of cells that disagree.
    ///
    /// This is the price of the speed-up: a reordered polygon test is easy to get subtly wrong,
    /// and a border that moves by one cell is a border that moved. The test is in the product
    /// rather than in a bench script because the rules are data: a mod that adds a region adds
    /// a box that nobody has checked until this runs.
    /// </summary>
    internal static (int Mismatches, int Cells, int Rules) VerifyPolygonMasks(int width, int height)
    {
        var mismatches = 0;
        var cells = 0;
        var rules = 0;
        foreach (var rule in RegionRules)
        {
            if (rule.Polygon is null || rule.Polygon.Length < 3) continue;
            rules++;
            var minX = LongitudeToX(rule.MinLongitude, width);
            var maxX = LongitudeToX(rule.MaxLongitude, width);
            var minY = LatitudeToY(rule.MaxLatitude, height);
            var maxY = LatitudeToY(rule.MinLatitude, height);
            var mask = PolygonMask(rule.Polygon, minX, maxX, minY, maxY, width, height);
            var boxWidth = maxX - minX + 1;
            for (var y = minY; y <= maxY; y++)
            {
                var latitude = YToLatitude(y, height);
                for (var x = minX; x <= maxX; x++)
                {
                    cells++;
                    var expected = Contains(rule.Polygon, XToLongitude(x, width), latitude);
                    if (mask[(y - minY) * boxWidth + (x - minX)] != expected) mismatches++;
                }
            }
        }
        return (mismatches, cells, rules);
    }


}
