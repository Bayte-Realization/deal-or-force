using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class TerritorialTopology
{
    /// <summary>
    /// Split one region into several, growing each share out of its own seed until the
    /// shares meet. Every cell goes to whichever seed reaches it first, so a share can only
    /// take ground it can actually walk to - a piece cut off by a bay belongs to whoever is
    /// on its side of the water, not to whoever is nearer as the crow flies.
    /// </summary>
    private static void ApplyPartition(Campaign campaign, ushort[] control, ushort[] states,
        int width, int height, RegionPartition partition, List<RepairSummary> summaries,
        Dictionary<int, ScenarioState> stateByMapIndex = null, List<int> precollectedGround = null)
    {
        // Whose ground is being divided. A rule may name an entry of the country table that is
        // not a country (common/country_merges): the shares are then regions of whoever holds
        // its ground, or of nobody - Antarctica's lands are made here and nobody owns the ice.
        var nation = campaign.NationByTag(CountryMerges.OwnerOf(partition.NationTag));
        var donorIndex = StateIndexByIso(partition.CarveFromIso);
        if ((nation is null && !CountryMerges.IsMerged(partition.NationTag)) || donorIndex == 0) return;

        // The ground being divided is the region plus everything already divided out of
        // it. Taking only what still carries the original index would re-divide the
        // leftovers on a second pass, and this runs twice - once as the campaign is built
        // and once when the map is repaired - so the map compiler and the game would
        // disagree about where the borders are, and the game would refuse its own map.
        var shares = new HashSet<ushort> { donorIndex };
        foreach (var piece in partition.Pieces) shares.Add((ushort)piece.MapIndex);
        // Other regions whose ground is divided with it, each kept by the share that has its index.
        var carved = new HashSet<ushort> { donorIndex };
        foreach (var iso in partition.AlsoCarveFromIso ?? Array.Empty<string>())
        {
            var extra = StateIndexByIso(iso);
            if (extra == 0) continue;
            shares.Add(extra);
            carved.Add(extra);
        }
        if (partition.Polar)
        {
            // Growing a million and a half cells is not free, and the game asks twice: once as
            // the campaign is built and once when the globe binds it, both on the one raster.
            ApplyPartitionRegions(campaign, nation, donorIndex, carved, partition, stateByMapIndex);
            var polarCounts = AppliedPolar.TryGetValue(states, out var done) && done.Contains(partition.CarveFromIso)
                ? CountShares(states, shares)
                : GrowPolar(states, width, height, shares, partition.Pieces);
            AppliedPolar.GetValue(states, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(partition.CarveFromIso);
            foreach (var piece in partition.Pieces)
                summaries.Add(new RepairSummary(piece.Name, "REGION", polarCounts.GetValueOrDefault((ushort)piece.MapIndex)));
            return;
        }
        List<int> ground;
        if (precollectedGround is not null)
        {
            ground = precollectedGround;
        }
        else
        {
            // A table of the shares rather than the set: this asks it of every cell in the world.
            var sharing = new bool[ushort.MaxValue + 1];
            foreach (var share in shares) sharing[share] = true;
            ground = new List<int>();
            for (var cell = 0; cell < states.Length; cell++)
                if (sharing[states[cell]]) ground.Add(cell);
        }
        if (ground.Count == 0) return;

        var donor = stateByMapIndex is not null
            ? stateByMapIndex.GetValueOrDefault(donorIndex)
            : campaign.States.FirstOrDefault(candidate => candidate.MapIndex == donorIndex);

        // Each share is a region of its own, except the one that keeps the index of what is
        // being split - that one is the same region under a truer name.
        foreach (var piece in partition.Pieces)
        {
            var state = stateByMapIndex is not null
                ? stateByMapIndex.GetValueOrDefault(piece.MapIndex)
                : campaign.States.FirstOrDefault(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null)
            {
                state = NewShare(campaign, nation, donor, piece, stateByMapIndex);
            }
            else if (carved.Contains((ushort)piece.MapIndex) && !string.IsNullOrWhiteSpace(piece.Name))
            {
                state.Name = piece.Name;
                if (!string.IsNullOrWhiteSpace(piece.Iso))
                {
                    state.Iso = piece.Iso;
                    state.Id = piece.Iso.ToLowerInvariant().Replace('-', '_');
                    Movements.NoteRegionRenamed(campaign);
                }
                if (!string.IsNullOrWhiteSpace(piece.Kind)) state.Kind = piece.Kind;
            }
        }

        // Compute tight modular bounding box around ground cells so Dijkstra + TidyStrayPieces
        // operate on compact flat arrays instead of allocating 200k-entry Dictionary/HashSet.
        int minY = height, maxY = -1;
        int minX1 = width, maxX1 = -1;
        int minX2 = width, maxX2 = -1;
        var halfW = width >> 1;
        for (var gi = 0; gi < ground.Count; gi++)
        {
            var cell = ground[gi];
            var y = cell / width;
            var x = cell - y * width;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (x < minX1) minX1 = x;
            if (x > maxX1) maxX1 = x;
            var sx = x + halfW;
            if (sx >= width) sx -= width;
            if (sx < minX2) minX2 = sx;
            if (sx > maxX2) maxX2 = sx;
        }
        var boxH = maxY - minY + 1;
        var span1 = maxX1 - minX1 + 1;
        var span2 = maxX2 - minX2 + 1;
        int xOrigin, boxW;
        if (span1 <= span2)
        {
            xOrigin = minX1;
            boxW = span1;
        }
        else
        {
            xOrigin = minX2 - halfW;
            if (xOrigin < 0) xOrigin += width;
            boxW = span2;
        }
        var boxLenLong = (long)boxH * boxW;
        if (boxLenLong > 4_000_000L)
        {
            ApplyPartitionSparse(states, width, height, ground, partition, summaries);
            return;
        }

        var boxLen = (int)boxLenLong;
        var localOwner = ArrayPool<ushort>.Shared.Rent(boxLen);
        var localReached = ArrayPool<int>.Shared.Rent(boxLen);
        try
        {
            Array.Clear(localOwner, 0, boxLen);
            Array.Fill(localReached, -1, 0, boxLen);
            for (var gi = 0; gi < ground.Count; gi++)
            {
                var cell = ground[gi];
                var y = cell / width;
                var x = cell - y * width;
                var col = x >= xOrigin ? x - xOrigin : x + width - xOrigin;
                localReached[(y - minY) * boxW + col] = int.MaxValue;
            }

            // Where each share starts: the piece of ground nearest the point it was given.
            var frontier = new PriorityQueue<int, int>();
            foreach (var piece in partition.Pieces)
            {
                var seedX = LongitudeToX(piece.Longitude, width);
                var seedY = LatitudeToY(piece.Latitude, height);
                var best = -1;
                var bestDistance = long.MaxValue;
                for (var gi = 0; gi < ground.Count; gi++)
                {
                    var cell = ground[gi];
                    var dy = (long)(cell / width) - seedY;
                    var dx = (long)(cell % width) - seedX;
                    if (dx > halfW) dx -= width;
                    if (dx < -halfW) dx += width;
                    var distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = cell;
                }
                if (best < 0) continue;
                var by = best / width;
                var bx = best - by * width;
                var bCol = bx >= xOrigin ? bx - xOrigin : bx + width - xOrigin;
                var bestLocal = (by - minY) * boxW + bCol;
                if (localOwner[bestLocal] != 0) continue;
                localOwner[bestLocal] = (ushort)piece.MapIndex;
                localReached[bestLocal] = 0;
                frontier.Enqueue(bestLocal, 0);
            }
            if (frontier.Count == 0) return;

            while (frontier.TryDequeue(out var local, out var cost))
            {
                if (cost > localReached[local]) continue;
                var mine = localOwner[local];
                var row = local / boxW;
                var col = local - row * boxW;
                var y = minY + row;
                for (var side = 0; side < 4; side++)
                {
                    var dx = side == 0 ? -1 : side == 1 ? 1 : 0;
                    var dy = side == 2 ? -1 : side == 3 ? 1 : 0;
                    var ny = y + dy;
                    if ((uint)ny >= (uint)height) continue;
                    var nRow = ny - minY;
                    if ((uint)nRow >= (uint)boxH) continue;
                    var nCol = col + dx;
                    if (boxW == width)
                    {
                        if (nCol < 0) nCol += width;
                        else if (nCol >= width) nCol -= width;
                    }
                    else if ((uint)nCol >= (uint)boxW)
                    {
                        continue;
                    }
                    var nextLocal = nRow * boxW + nCol;
                    var seen = localReached[nextLocal];
                    if (seen < 0) continue;
                    var nx = xOrigin + nCol;
                    if (nx >= width) nx -= width;
                    var step = cost + GroundCost(nx, ny);
                    if (seen <= step) continue;
                    localReached[nextLocal] = step;
                    localOwner[nextLocal] = mine;
                    frontier.Enqueue(nextLocal, step);
                }
            }

            TidyStrayPiecesBox(states, width, height, ground, minY, boxH, xOrigin, boxW, boxLen, localOwner, localReached);

            var counts = new Dictionary<ushort, int>();
            for (var gi = 0; gi < ground.Count; gi++)
            {
                var cell = ground[gi];
                var y = cell / width;
                var x = cell - y * width;
                var col = x >= xOrigin ? x - xOrigin : x + width - xOrigin;
                var index = localOwner[(y - minY) * boxW + col];
                if (index == ushort.MaxValue || index == 0) continue;
                states[cell] = index;
                counts[index] = counts.GetValueOrDefault(index) + 1;
            }
            foreach (var piece in partition.Pieces)
                summaries.Add(new RepairSummary(piece.Name, "REGION",
                    counts.GetValueOrDefault((ushort)piece.MapIndex)));
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(localOwner);
            ArrayPool<int>.Shared.Return(localReached);
        }
    }

    private static void ApplyPartitionSparse(ushort[] states, int width, int height,
        List<int> ground, RegionPartition partition, List<RepairSummary> summaries)
    {
        // Where each share starts: the piece of ground nearest the point it was given.
        var owner = new Dictionary<int, ushort>(ground.Count);
        var reached = new Dictionary<int, int>(ground.Count);
        var frontier = new PriorityQueue<int, int>();
        foreach (var piece in partition.Pieces)
        {
            var seedX = LongitudeToX(piece.Longitude, width);
            var seedY = LatitudeToY(piece.Latitude, height);
            var best = -1;
            var bestDistance = long.MaxValue;
            foreach (var cell in ground)
            {
                var dy = (long)(cell / width) - seedY;
                var dx = (long)(cell % width) - seedX;
                if (dx > width / 2) dx -= width;
                if (dx < -width / 2) dx += width;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = cell;
            }
            if (best < 0 || owner.ContainsKey(best)) continue;
            owner[best] = (ushort)piece.MapIndex;
            reached[best] = 0;
            frontier.Enqueue(best, 0);
        }
        if (frontier.Count == 0) return;

        var inside = new HashSet<int>(ground);
        while (frontier.Count > 0)
        {
            if (!frontier.TryDequeue(out var cell, out var cost)) break;
            if (reached.TryGetValue(cell, out var known) && cost > known) continue;
            var mine = owner[cell];
            var y = cell / width;
            var x = cell % width;
            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (ny < 0 || ny >= height) continue;
                nx = (nx + width) % width;
                var next = ny * width + nx;
                if (!inside.Contains(next)) continue;
                var step = cost + GroundCost(nx, ny);
                if (reached.TryGetValue(next, out var seen) && seen <= step) continue;
                reached[next] = step;
                owner[next] = mine;
                frontier.Enqueue(next, step);
            }
        }

        TidyStrayPieces(states, width, height, ground, owner);

        var counts = new Dictionary<ushort, int>();
        foreach (var (cell, index) in owner)
        {
            if (index == ushort.MaxValue) continue;
            states[cell] = index;
            counts[index] = counts.GetValueOrDefault(index) + 1;
        }
        foreach (var piece in partition.Pieces)
            summaries.Add(new RepairSummary(piece.Name, "REGION",
                counts.GetValueOrDefault((ushort)piece.MapIndex)));
    }

    private static readonly ConditionalWeakTable<ushort[], HashSet<string>> AppliedPolar = new();

    /// <summary>The regions of a partition, made or renamed - the part every campaign needs, whoever grew the raster.</summary>
    private static void ApplyPartitionRegions(Campaign campaign, Nation nation, ushort donorIndex,
        HashSet<ushort> carved, RegionPartition partition, Dictionary<int, ScenarioState> stateByMapIndex = null)
    {
        var donor = stateByMapIndex is not null
            ? stateByMapIndex.GetValueOrDefault(donorIndex)
            : campaign.States.FirstOrDefault(candidate => candidate.MapIndex == donorIndex);
        foreach (var piece in partition.Pieces)
        {
            var state = stateByMapIndex is not null
                ? stateByMapIndex.GetValueOrDefault(piece.MapIndex)
                : campaign.States.FirstOrDefault(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null)
            {
                state = NewShare(campaign, nation, donor, piece, stateByMapIndex);
            }
            else if (carved.Contains((ushort)piece.MapIndex) && !string.IsNullOrWhiteSpace(piece.Name))
            {
                state.Name = piece.Name;
                if (!string.IsNullOrWhiteSpace(piece.Kind)) state.Kind = piece.Kind;
            }
        }
    }

    /// <summary>
    /// One share of a divided region, as a region of its own.
    ///
    /// Its people are dealt out the way its country's are everywhere else, which needs a country
    /// to take the figures from. Ground nobody owns has neither: it is unclaimed land with what
    /// history/states writes for it and nothing worked out, the same as a region made for an
    /// unowned province (ColonisationCore.EnsureAuthoredRegions).
    /// </summary>
    private static ScenarioState NewShare(Campaign campaign, Nation nation, ScenarioState donor, RegionPiece piece,
        Dictionary<int, ScenarioState> stateByMapIndex = null)
    {
        var state = new ScenarioState
        {
            MapIndex = piece.MapIndex,
            NationMapIndex = nation?.MapIndex ?? 0,
            NationId = nation?.Tag ?? "",
            Id = piece.Iso.ToLowerInvariant().Replace('-', '_'),
            Name = piece.Name,
            Iso = piece.Iso,
            Kind = piece.Kind,
            Unclaimed = nation is null
        };
        campaign.States.Add(state);
        stateByMapIndex?.TryAdd(state.MapIndex, state);
        if (donor is not null && piece.Population > 0)
            EconomySystem.AdjustStatePopulation(donor, -piece.Population);
        if (nation is not null) EconomySystem.SeedState(campaign, state, nation, piece.Population);
        else state.Population = piece.Population;
        return state;
    }

    private static Dictionary<ushort, int> CountShares(ushort[] states, HashSet<ushort> shares)
    {
        var member = new bool[ushort.MaxValue + 1];
        foreach (var share in shares) member[share] = true;
        var counts = new Dictionary<ushort, int>();
        foreach (var index in states)
            if (member[index]) counts[index] = counts.GetValueOrDefault(index) + 1;
        return counts;
    }

    /// <summary>
    /// A partition grown on the ground rather than on the raster, for land near a pole.
    ///
    /// A cell of the raster is a sixteenth of a degree each way, and a sixteenth of a degree
    /// of longitude at eighty degrees south is a sixth of one at the equator. Grown a cell a
    /// step, a share near the pole runs round it along its circle of latitude, and the borders
    /// come out as the spokes and rings of a dartboard: Antarctica cut into sectors, which is
    /// what its claims look like on paper and what a border on the ground never does. Here a
    /// step costs the distance it covers, diagonals included, and the wander that keeps two
    /// shares from meeting on a straight line is noise laid over the pole itself - so it
    /// wanders as much at the pole as at the coast, rather than turning to static near it.
    ///
    /// What is left over: a scrap of one share only reachable through another goes to the one it
    /// touches most, as anywhere; ground no share could walk to at all - an island, an ice rise
    /// in a shelf - goes to the share nearest across the water, which is the one whose coast it
    /// lies off.
    /// </summary>
    private static Dictionary<ushort, int> GrowPolar(ushort[] states, int width, int height,
        HashSet<ushort> shares, RegionPiece[] pieces)
    {
        var counts = new Dictionary<ushort, int>();
        var member = new bool[ushort.MaxValue + 1];
        foreach (var share in shares) member[share] = true;
        int top = height, bottom = -1;
        for (var cell = 0; cell < states.Length; cell++)
        {
            if (!member[states[cell]]) continue;
            var row = cell / width;
            if (row < top) top = row;
            if (row > bottom) bottom = row;
        }
        if (bottom < 0) return counts;

        // The band of rows the ground lies in, as its own little grid.
        var rows = bottom - top + 1;
        var size = rows * width;
        var south = top + bottom >= height;
        var ground = new bool[size];
        var cosine = new float[rows];
        for (var row = 0; row < rows; row++)
            cosine[row] = MathF.Max(0.0005f, MathF.Cos(YToLatitude(top + row, height) * MathF.PI / 180f));
        for (var local = 0; local < size; local++) ground[local] = member[states[top * width + local]];

        // Precompute longitude trig once per column instead of 1.5M redundant Sin/Cos calls.
        var sinCol = ArrayPool<float>.Shared.Rent(width);
        var cosCol = ArrayPool<float>.Shared.Rent(width);
        var projectedX = new float[size];
        var projectedY = new float[size];
        try
        {
            for (var col = 0; col < width; col++)
            {
                var angle = (col + 0.5f) / width * MathF.Tau;
                sinCol[col] = MathF.Sin(angle);
                cosCol[col] = MathF.Cos(angle);
            }
            for (var row = 0; row < rows; row++)
            {
                var fromPole = south ? height - (top + row) - 0.5f : top + row + 0.5f;
                var rowBase = row * width;
                for (var col = 0; col < width; col++)
                {
                    var local = rowBase + col;
                    if (!ground[local]) continue;
                    projectedX[local] = fromPole * sinCol[col];
                    projectedY[local] = fromPole * cosCol[col];
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sinCol);
            ArrayPool<float>.Shared.Return(cosCol);
        }

        var price = new int[size];
        for (var local = 0; local < size; local++)
        {
            if (!ground[local]) continue;
            var x = projectedX[local];
            var y = projectedY[local];
            // Two scales of wander: a border that swings over a few hundred kilometres, and
            // is ragged over a few dozen.
            var wander = 0.55f * PolarNoise(x, y, 60f, 0x51A7u) + 0.30f * PolarNoise(x, y, 20f, 0x2F0Du) +
                         0.15f * PolarNoise(x, y, 7f, 0x7C3Bu);
            price[local] = (int)(100f * MathF.Exp(2.4f * (wander - 0.5f)));
        }

        // Each share starts from the piece of ground nearest the point it was given.
        var owner = new ushort[size];
        var reached = new int[size];
        Array.Fill(reached, int.MaxValue);
        var frontier = new PriorityQueue<int, int>();
        foreach (var piece in pieces)
        {
            var seedRow = LatitudeToY(piece.Latitude, height) - top;
            var seedColumn = LongitudeToX(piece.Longitude, width);
            var seedFromPole = south ? height - (top + seedRow) - 0.5f : top + seedRow + 0.5f;
            var seedAngle = (seedColumn + 0.5f) / width * MathF.Tau;
            var sx = seedFromPole * MathF.Sin(seedAngle);
            var sy = seedFromPole * MathF.Cos(seedAngle);
            var best = -1;
            var bestDistance = float.MaxValue;
            for (var local = 0; local < size; local++)
            {
                if (!ground[local] || owner[local] != 0) continue;
                var x = projectedX[local];
                var y = projectedY[local];
                var distance = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = local;
            }
            if (best < 0) continue;
            owner[best] = (ushort)piece.MapIndex;
            reached[best] = 0;
            frontier.Enqueue(best, 0);
        }

        while (frontier.TryDequeue(out var local, out var cost))
        {
            if (cost > reached[local]) continue;
            var row = local / width;
            var column = local % width;
            for (var dy = -1; dy <= 1; dy++)
            {
                var nextRow = row + dy;
                if (nextRow < 0 || nextRow >= rows) continue;
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var next = nextRow * width + (column + dx + width) % width;
                    if (!ground[next]) continue;
                    // The distance the step covers: a degree of longitude shrinks with the latitude.
                    var across = dx == 0 ? 0f : 0.5f * (cosine[row] + cosine[nextRow]);
                    var length = dy == 0 ? across : dx == 0 ? 1f : MathF.Sqrt(1f + across * across);
                    var step = cost + Math.Max(1, (int)(price[next] * length));
                    if (step >= reached[next]) continue;
                    reached[next] = step;
                    owner[next] = owner[local];
                    frontier.Enqueue(next, step);
                }
            }
        }

        // And then bent. Grown ground meets along lines that are straight for hundreds of
        // kilometres at a time however the cost wanders, which is how a cell of a Voronoi
        // diagram looks and how no border between two places does. Each cell takes the share of
        // a point a little way off, in a direction that turns slowly across the map - at three
        // scales, so the border swings, meanders and frays - and the straight lines come out as
        // the kind of line a river or a watershed draws.
        var bent = new ushort[size];
        for (var local = 0; local < size; local++)
        {
            if (!ground[local]) continue;
            bent[local] = owner[local];
            var x = projectedX[local];
            var y = projectedY[local];
            var shiftX = 0f;
            var shiftY = 0f;
            foreach (var (lattice, reach, salt) in Bends)
            {
                shiftX += reach * (PolarNoise(x, y, lattice, salt) * 2f - 1f);
                shiftY += reach * (PolarNoise(x, y, lattice, salt ^ 0x9E37u) * 2f - 1f);
            }
            var sx = x + shiftX;
            var sy = y + shiftY;
            var fromPole = MathF.Sqrt(sx * sx + sy * sy);
            var row = (south ? height - fromPole - 0.5f : fromPole - 0.5f) - top;
            var angle = MathF.Atan2(sx, sy);
            if (angle < 0f) angle += MathF.Tau;
            var column = (int)(angle / MathF.Tau * width - 0.5f + width) % width;
            var nearRow = (int)MathF.Round(row);
            if (nearRow < 0 || nearRow >= rows) continue;
            var source = nearRow * width + column;
            if (ground[source] && owner[source] != 0) bent[local] = owner[source];
        }
        owner = bent;

        // The pieces of each share, four-connected: a share joined to itself only at a corner
        // is two pieces on the screen.
        var pieceOf = new int[size];
        Array.Fill(pieceOf, -1);
        var pieceOwner = new List<ushort>();
        var pieceCells = new List<List<int>>();
        var largest = new Dictionary<ushort, int>();
        var queue = new Queue<int>();
        for (var start = 0; start < size; start++)
        {
            if (!ground[start] || pieceOf[start] >= 0) continue;
            var id = pieceCells.Count;
            var mine = owner[start];
            var cells = new List<int>();
            pieceOf[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var local = queue.Dequeue();
                cells.Add(local);
                var row = local / width;
                var column = local % width;
                for (var side = 0; side < 4; side++)
                {
                    var nextRow = row + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (nextRow < 0 || nextRow >= rows) continue;
                    var next = nextRow * width + (column + (side == 0 ? -1 : side == 1 ? 1 : 0) + width) % width;
                    if (!ground[next] || pieceOf[next] >= 0 || owner[next] != mine) continue;
                    pieceOf[next] = id;
                    queue.Enqueue(next);
                }
            }
            pieceOwner.Add(mine);
            pieceCells.Add(cells);
            if (mine != 0 && cells.Count > largest.GetValueOrDefault(mine)) largest[mine] = cells.Count;
        }

        // A stray that touches another share goes to the one it touches most.
        var islands = new List<int>();
        for (var id = 0; id < pieceCells.Count; id++)
        {
            var mine = pieceOwner[id];
            if (mine != 0 && pieceCells[id].Count >= largest.GetValueOrDefault(mine)) continue;
            var shared = new Dictionary<ushort, int>();
            foreach (var local in pieceCells[id])
            {
                var row = local / width;
                var column = local % width;
                for (var side = 0; side < 4; side++)
                {
                    var nextRow = row + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (nextRow < 0 || nextRow >= rows) continue;
                    var next = nextRow * width + (column + (side == 0 ? -1 : side == 1 ? 1 : 0) + width) % width;
                    if (!ground[next] || owner[next] == mine || owner[next] == 0) continue;
                    shared[owner[next]] = shared.GetValueOrDefault(owner[next]) + 1;
                }
            }
            if (shared.Count == 0)
            {
                islands.Add(id);
                continue;
            }
            ushort best = 0;
            var bestEdge = 0;
            foreach (var (neighbour, edge) in shared)
                if (edge > bestEdge || edge == bestEdge && neighbour < best)
                {
                    best = neighbour;
                    bestEdge = edge;
                }
            foreach (var local in pieceCells[id]) owner[local] = best;
        }

        // And an island to whoever is nearest across the water: a flood out from every coast,
        // over the sea as well as the land, and the first share to wash up on it has it.
        if (islands.Count > 0)
        {
            var island = new bool[size];
            foreach (var id in islands)
                foreach (var local in pieceCells[id]) island[local] = true;
            var tide = new ushort[size];
            var flood = new Queue<int>();
            for (var local = 0; local < size; local++)
                if (ground[local] && !island[local] && owner[local] != 0)
                {
                    tide[local] = owner[local];
                    flood.Enqueue(local);
                }
            while (flood.Count > 0)
            {
                var local = flood.Dequeue();
                var row = local / width;
                var column = local % width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var nextRow = row + dy;
                    if (nextRow < 0 || nextRow >= rows) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var next = nextRow * width + (column + dx + width) % width;
                        if (tide[next] != 0) continue;
                        tide[next] = tide[local];
                        flood.Enqueue(next);
                    }
                }
            }
            foreach (var id in islands)
            {
                // The island goes whole, to whoever reached most of it.
                var landed = new Dictionary<ushort, int>();
                foreach (var local in pieceCells[id])
                    if (tide[local] != 0) landed[tide[local]] = landed.GetValueOrDefault(tide[local]) + 1;
                ushort best = 0;
                var most = 0;
                foreach (var (share, cells) in landed)
                    if (cells > most || cells == most && share < best)
                    {
                        best = share;
                        most = cells;
                    }
                if (best == 0) continue;
                foreach (var local in pieceCells[id]) owner[local] = best;
            }
        }

        for (var local = 0; local < size; local++)
        {
            if (!ground[local] || owner[local] == 0) continue;
            states[top * width + local] = owner[local];
            counts[owner[local]] = counts.GetValueOrDefault(owner[local]) + 1;
        }
        return counts;
    }

    /// <summary>How a polar border is bent: the scale of each turn, how far it reaches, in cells of latitude.</summary>
    private static readonly (float Lattice, float Reach, uint Salt)[] Bends =
    {
        (90f, 30f, 0xB5E1u), (30f, 13f, 0x4D2Bu), (10f, 4f, 0x1F77u)
    };

    /// <summary>Smooth value noise at any point, fixed to the map rather than to the run.</summary>
    private static float PolarNoise(float x, float y, float lattice, uint salt)
    {
        var gx = x / lattice;
        var gy = y / lattice;
        var cellX = (int)MathF.Floor(gx);
        var cellY = (int)MathF.Floor(gy);
        var fx = gx - cellX;
        var fy = gy - cellY;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float Corner(int cx, int cy)
        {
            var hash = (uint)(cx * 374761393 + cy * 668265263) ^ salt * 2246822519u;
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        var upper = Corner(cellX, cellY) + (Corner(cellX + 1, cellY) - Corner(cellX, cellY)) * fx;
        var lower = Corner(cellX, cellY + 1) + (Corner(cellX + 1, cellY + 1) - Corner(cellX, cellY + 1)) * fx;
        return upper + (lower - upper) * fy;
    }

    /// <summary>
    /// What one step across this cell costs a growing region.
    ///
    /// Smooth value noise on a coarse lattice, hashed off the cell's own coordinates so it
    /// is the same on every machine and every run. Sixteen cells to a lattice square makes
    /// the border wander over tens of kilometres rather than jitter pixel by pixel, which
    /// reads as a border rather than as static.
    /// </summary>
    private static int GroundCost(int x, int y)
    {
        const int lattice = 8;
        var cellX = x / lattice;
        var cellY = y / lattice;
        var fx = (x % lattice) / (float)lattice;
        var fy = (y % lattice) / (float)lattice;
        // Smoothstep, so the lattice itself never shows as a grid.
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        static float Corner(int cx, int cy)
        {
            var hash = (uint)(cx * 374761393 + cy * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        var top = Corner(cellX, cellY) + (Corner(cellX + 1, cellY) - Corner(cellX, cellY)) * fx;
        var bottom = Corner(cellX, cellY + 1) + (Corner(cellX + 1, cellY + 1) - Corner(cellX, cellY + 1)) * fx;
        var value = top + (bottom - top) * fy;
        return 100 + (int)(value * 130f);
    }

    /// <summary>
    /// Hand every stray scrap to a neighbour.
    ///
    /// A share can end up holding ground it cannot reach - the enclave east of Beijing is
    /// the standing example, a fragment of one province wedged between two others that has
    /// to be walked around to be reached at all. Left alone it draws as a speck of the
    /// wrong colour sitting on its own, which reads as a mistake because it looks like one.
    /// Each fragment goes to whichever region it shares the most border with, which is the
    /// neighbour it already looks like part of.
    /// </summary>
    private static void TidyStrayPieces(ushort[] states, int width, int height,
        List<int> ground, Dictionary<int, ushort> owner)
    {
        var visited = new HashSet<int>();
        var component = new List<int>();
        var queue = new Queue<int>();
        var largest = new Dictionary<ushort, int>();
        var components = new List<(ushort Owner, List<int> Cells)>();

        // Ground the growth never reached is ground no share can walk to - an enclave, an
        // island, a scrap on the far side of somebody else's territory. It has no owner at
        // all, and leaving it be is what left a speck of Hebei sitting alone east of
        // Beijing. Treated as its own fragment, it goes to a neighbour like any other.
        const ushort unreached = ushort.MaxValue;
        foreach (var cell in ground)
            if (!owner.ContainsKey(cell)) owner[cell] = unreached;

        foreach (var start in ground)
        {
            if (!owner.TryGetValue(start, out var mine) || !visited.Add(start)) continue;
            component.Clear();
            queue.Clear();
            queue.Enqueue(start);
            component.Add(start);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var y = cell / width;
                var x = cell % width;
                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (ny < 0 || ny >= height) continue;
                    nx = (nx + width) % width;
                    var next = ny * width + nx;
                    if (!owner.TryGetValue(next, out var theirs) || theirs != mine) continue;
                    if (!visited.Add(next)) continue;
                    queue.Enqueue(next);
                    component.Add(next);
                }
            }
            components.Add((mine, new List<int>(component)));
            if (component.Count > largest.GetValueOrDefault(mine)) largest[mine] = component.Count;
        }

        foreach (var (mine, cells) in components)
        {
            // Unreached ground is always a stray, however much of it there is.
            if (mine != unreached && cells.Count >= largest.GetValueOrDefault(mine)) continue;

            // Whoever this scrap already touches most.
            var shared = new Dictionary<ushort, int>();
            foreach (var cell in cells)
            {
                var y = cell / width;
                var x = cell % width;
                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (ny < 0 || ny >= height) continue;
                    nx = (nx + width) % width;
                    var next = ny * width + nx;
                    var neighbour = owner.TryGetValue(next, out var inside) ? inside : states[next];
                    if (neighbour == 0 || neighbour == mine || neighbour == unreached) continue;
                    shared[neighbour] = shared.GetValueOrDefault(neighbour) + 1;
                }
            }
            if (shared.Count == 0) continue;

            var best = (ushort)0;
            var bestEdge = 0;
            foreach (var (neighbour, edge) in shared)
                if (edge > bestEdge || (edge == bestEdge && neighbour < best))
                {
                    bestEdge = edge;
                    best = neighbour;
                }
            if (best == 0) continue;
            foreach (var cell in cells) owner[cell] = best;
        }
    }

    private static void TidyStrayPiecesBox(ushort[] states, int width, int height,
        List<int> ground, int minY, int boxH, int xOrigin, int boxW, int boxLen,
        ushort[] localOwner, int[] localReached)
    {
        const ushort unreached = ushort.MaxValue;
        for (var gi = 0; gi < ground.Count; gi++)
        {
            var cell = ground[gi];
            var y = cell / width;
            var x = cell - y * width;
            var col = x >= xOrigin ? x - xOrigin : x + width - xOrigin;
            var local = (y - minY) * boxW + col;
            if (localOwner[local] == 0) localOwner[local] = unreached;
        }

        var visited = ArrayPool<bool>.Shared.Rent(boxLen);
        var queue = new Queue<int>();
        var largest = new Dictionary<ushort, int>();
        var allCompCells = new List<int>(ground.Count);
        var components = new List<(ushort Owner, int Start, int Length)>();
        var shared = new Dictionary<ushort, int>();
        try
        {
            Array.Clear(visited, 0, boxLen);
            for (var gi = 0; gi < ground.Count; gi++)
            {
                var startCell = ground[gi];
                var sy = startCell / width;
                var sx = startCell - sy * width;
                var sCol = sx >= xOrigin ? sx - xOrigin : sx + width - xOrigin;
                var startLocal = (sy - minY) * boxW + sCol;
                if (visited[startLocal]) continue;
                visited[startLocal] = true;
                var mine = localOwner[startLocal];
                var compStart = allCompCells.Count;
                queue.Clear();
                queue.Enqueue(startLocal);
                allCompCells.Add(startLocal);
                while (queue.Count > 0)
                {
                    var local = queue.Dequeue();
                    var row = local / boxW;
                    var col = local - row * boxW;
                    var y = minY + row;
                    for (var side = 0; side < 4; side++)
                    {
                        var dx = side == 0 ? -1 : side == 1 ? 1 : 0;
                        var dy = side == 2 ? -1 : side == 3 ? 1 : 0;
                        var ny = y + dy;
                        if ((uint)ny >= (uint)height) continue;
                        var nRow = ny - minY;
                        if ((uint)nRow >= (uint)boxH) continue;
                        var nCol = col + dx;
                        if (boxW == width)
                        {
                            if (nCol < 0) nCol += width;
                            else if (nCol >= width) nCol -= width;
                        }
                        else if ((uint)nCol >= (uint)boxW)
                        {
                            continue;
                        }
                        var nextLocal = nRow * boxW + nCol;
                        if (localReached[nextLocal] < 0 || localOwner[nextLocal] != mine || visited[nextLocal]) continue;
                        visited[nextLocal] = true;
                        queue.Enqueue(nextLocal);
                        allCompCells.Add(nextLocal);
                    }
                }
                var compLen = allCompCells.Count - compStart;
                components.Add((mine, compStart, compLen));
                if (compLen > largest.GetValueOrDefault(mine)) largest[mine] = compLen;
            }

            for (var ci = 0; ci < components.Count; ci++)
            {
                var (mine, compStart, compLen) = components[ci];
                if (mine != unreached && compLen >= largest.GetValueOrDefault(mine)) continue;
                shared.Clear();
                var compEnd = compStart + compLen;
                for (var idx = compStart; idx < compEnd; idx++)
                {
                    var local = allCompCells[idx];
                    var row = local / boxW;
                    var col = local - row * boxW;
                    var y = minY + row;
                    for (var side = 0; side < 4; side++)
                    {
                        var dx = side == 0 ? -1 : side == 1 ? 1 : 0;
                        var dy = side == 2 ? -1 : side == 3 ? 1 : 0;
                        var ny = y + dy;
                        if ((uint)ny >= (uint)height) continue;
                        ushort neighbour;
                        var nRow = ny - minY;
                        var nCol = col + dx;
                        if (boxW == width)
                        {
                            if (nCol < 0) nCol += width;
                            else if (nCol >= width) nCol -= width;
                            if ((uint)nRow < (uint)boxH)
                            {
                                var nextLocal = nRow * boxW + nCol;
                                neighbour = localReached[nextLocal] >= 0 ? localOwner[nextLocal] : states[ny * width + ((xOrigin + nCol) % width)];
                            }
                            else
                            {
                                neighbour = states[ny * width + ((xOrigin + nCol) % width)];
                            }
                        }
                        else if ((uint)nRow < (uint)boxH && (uint)nCol < (uint)boxW)
                        {
                            var nextLocal = nRow * boxW + nCol;
                            if (localReached[nextLocal] >= 0) neighbour = localOwner[nextLocal];
                            else
                            {
                                var nx = xOrigin + nCol;
                                if (nx >= width) nx -= width;
                                neighbour = states[ny * width + nx];
                            }
                        }
                        else
                        {
                            var x = xOrigin + col;
                            if (x >= width) x -= width;
                            var nx = (x + dx + width) % width;
                            neighbour = states[ny * width + nx];
                        }
                        if (neighbour == 0 || neighbour == mine || neighbour == unreached) continue;
                        shared[neighbour] = shared.GetValueOrDefault(neighbour) + 1;
                    }
                }
                if (shared.Count == 0) continue;

                var best = (ushort)0;
                var bestEdge = 0;
                foreach (var (neighbour, edge) in shared)
                    if (edge > bestEdge || (edge == bestEdge && neighbour < best))
                    {
                        bestEdge = edge;
                        best = neighbour;
                    }
                if (best == 0) continue;
                for (var idx = compStart; idx < compEnd; idx++) localOwner[allCompCells[idx]] = best;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

}
