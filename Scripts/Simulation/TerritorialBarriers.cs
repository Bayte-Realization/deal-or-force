using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class TerritorialTopology
{
    private static int BridgeRegionComponents(
        ushort[] control, ushort[] states, int width, int height,
        ushort nation, ushort sourceNation, ushort state,
        int minX, int maxX, int minY, int maxY, int maximumGap)
    {
        var changed = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var components = RegionComponents(states, width, height, state, minX, maxX, minY, maxY);
            if (components.Count <= 1) break;
            components.Sort((a, b) => b.Count.CompareTo(a.Count));
            var main = components[0];
            var bestA = -1;
            var bestB = -1;
            var bestDistance = int.MaxValue;
            for (var componentIndex = 1; componentIndex < components.Count; componentIndex++)
            foreach (var a in main)
            foreach (var b in components[componentIndex])
            {
                var dx = a % width - b % width;
                var dy = a / width - b / width;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestA = a;
                bestB = b;
            }
            if (bestA < 0 || bestDistance > maximumGap * maximumGap) break;

            var x0 = bestA % width;
            var y0 = bestA / width;
            var x1 = bestB % width;
            var y1 = bestB / width;
            var dxLine = Math.Abs(x1 - x0);
            var sx = x0 < x1 ? 1 : -1;
            var dyLine = -Math.Abs(y1 - y0);
            var sy = y0 < y1 ? 1 : -1;
            var error = dxLine + dyLine;
            while (true)
            {
                var cell = y0 * width + x0;
                if ((control[cell] == sourceNation || control[cell] == nation) && states[cell] != state)
                {
                    control[cell] = nation;
                    states[cell] = state;
                    changed++;
                }
                if (x0 == x1 && y0 == y1) break;
                var doubled = error * 2;
                if (doubled >= dyLine) { error += dyLine; x0 += sx; }
                if (doubled <= dxLine) { error += dxLine; y0 += sy; }
            }
        }
        return changed;
    }

    private static List<List<int>> RegionComponents(
        ushort[] states, int width, int height, ushort state,
        int minX, int maxX, int minY, int maxY)
    {
        var boxWidth = maxX - minX + 1;
        var visited = new bool[boxWidth * (maxY - minY + 1)];
        var result = new List<List<int>>();
        var queue = new Queue<int>();
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var local = (y - minY) * boxWidth + x - minX;
            var cell = y * width + x;
            if (visited[local] || states[cell] != state) continue;
            visited[local] = true;
            queue.Enqueue(cell);
            var component = new List<int>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                var cx = current % width;
                var cy = current / width;
                Try(cx - 1, cy);
                Try(cx + 1, cy);
                Try(cx, cy - 1);
                Try(cx, cy + 1);
            }
            result.Add(component);

            void Try(int nextX, int nextY)
            {
                if (nextX < minX || nextX > maxX || nextY < minY || nextY > maxY) return;
                var nextLocal = (nextY - minY) * boxWidth + nextX - minX;
                var nextCell = nextY * width + nextX;
                if (visited[nextLocal] || states[nextCell] != state) return;
                visited[nextLocal] = true;
                queue.Enqueue(nextCell);
            }
        }
        return result;
    }


    private static int Connect(
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        ushort subject,
        ushort neighbor,
        ushort through,
        ushort subjectState,
        AdjacencyRule rule)
    {
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        if (boxWidth <= 0 || boxHeight <= 0) return 0;

        var count = boxWidth * boxHeight;
        var parents = new int[count];
        Array.Fill(parents, int.MinValue);
        var queue = new Queue<int>();

        for (var localY = 0; localY < boxHeight; localY++)
        {
            var row = (minY + localY) * width;
            for (var localX = 0; localX < boxWidth; localX++)
            {
                if (control[row + minX + localX] != subject) continue;
                var local = localY * boxWidth + localX;
                parents[local] = -1;
                queue.Enqueue(local);
            }
        }

        if (queue.Count == 0) return 0;

        var end = -1;
        ReadOnlySpan<int> dx = stackalloc[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, -1, 1 };

        while (queue.Count > 0 && end < 0)
        {
            var current = queue.Dequeue();
            var cx = current % boxWidth;
            var cy = current / boxWidth;

            for (var direction = 0; direction < 4; direction++)
            {
                var nx = cx + dx[direction];
                var ny = cy + dy[direction];
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;

                var mapCell = (minY + ny) * width + minX + nx;
                var value = control[mapCell];
                if (value == neighbor)
                {
                    end = current;
                    break;
                }

                var next = ny * boxWidth + nx;
                if (parents[next] != int.MinValue || (value != subject && value != through)) continue;
                parents[next] = current;
                queue.Enqueue(next);
            }
        }

        if (end < 0) return 0;

        // Include the contact cell itself. If an earlier map already contains only a
        // one-pixel ABK-RUS contact, the through-only path is empty; expanding around
        // the contact removes the Georgian halo while remaining inside the rule box.
        var corridor = new HashSet<int> { end };
        for (var cursor = end; cursor >= 0; cursor = parents[cursor])
        {
            var x = cursor % boxWidth;
            var y = cursor / boxWidth;
            var mapCell = (minY + y) * width + minX + x;
            if (control[mapCell] == through) corridor.Add(cursor);
        }

        var changed = 0;
        foreach (var local in corridor)
        {
            var cx = local % boxWidth;
            var cy = local / boxWidth;
            for (var oy = -rule.CorridorRadius; oy <= rule.CorridorRadius; oy++)
            for (var ox = -rule.CorridorRadius; ox <= rule.CorridorRadius; ox++)
            {
                if (Math.Abs(ox) + Math.Abs(oy) > rule.CorridorRadius) continue;
                var nx = cx + ox;
                var ny = cy + oy;
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;
                var mapCell = (minY + ny) * width + minX + nx;
                if (control[mapCell] != through) continue;
                control[mapCell] = subject;
                states[mapCell] = subjectState;
                changed++;
            }
        }

        // The old shortest-path bridge guaranteed graph adjacency but could leave a
        // visible one-cell Georgian ribbon along the rest of Abkhazia's northern edge.
        // Remove every thin GEO barrier that is simultaneously close to ABK and RUS.
        // This remains bounded to the rule box and never repaints sea or third countries.
        changed += ClearSharedBarrier(control, states, width, minX, minY, boxWidth, boxHeight,
            subject, neighbor, through, subjectState, rule.CorridorRadius);

        return changed;
    }

    private static int ClearSharedBarrier(
        ushort[] control,
        ushort[] states,
        int width,
        int minX,
        int minY,
        int boxWidth,
        int boxHeight,
        ushort subject,
        ushort neighbor,
        ushort through,
        ushort subjectState,
        int radius)
    {
        var maximumDistance = Math.Max(4, radius * 2 + 2);
        var fromSubject = ThroughDistance(control, width, minX, minY, boxWidth, boxHeight,
            subject, through, maximumDistance);
        var fromNeighbor = ThroughDistance(control, width, minX, minY, boxWidth, boxHeight,
            neighbor, through, maximumDistance);
        var changed = 0;
        for (var local = 0; local < fromSubject.Length; local++)
        {
            if (fromSubject[local] <= 0 || fromNeighbor[local] <= 0 ||
                fromSubject[local] + fromNeighbor[local] > maximumDistance)
                continue;
            var x = local % boxWidth;
            var y = local / boxWidth;
            var mapCell = (minY + y) * width + minX + x;
            if (control[mapCell] != through) continue;
            control[mapCell] = subject;
            states[mapCell] = subjectState;
            changed++;
        }
        return changed;
    }

    private static int[] ThroughDistance(
        ushort[] control,
        int width,
        int minX,
        int minY,
        int boxWidth,
        int boxHeight,
        ushort source,
        ushort through,
        int maximumDistance)
    {
        var distance = new int[boxWidth * boxHeight];
        Array.Fill(distance, -1);
        var queue = new Queue<int>();
        for (var y = 0; y < boxHeight; y++)
        for (var x = 0; x < boxWidth; x++)
        {
            var local = y * boxWidth + x;
            if (control[(minY + y) * width + minX + x] != source) continue;
            distance[local] = 0;
            queue.Enqueue(local);
        }

        ReadOnlySpan<int> dx = stackalloc[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, -1, 1 };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (distance[current] >= maximumDistance) continue;
            var cx = current % boxWidth;
            var cy = current / boxWidth;
            for (var direction = 0; direction < 4; direction++)
            {
                var nx = cx + dx[direction];
                var ny = cy + dy[direction];
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;
                var next = ny * boxWidth + nx;
                if (distance[next] >= 0) continue;
                var value = control[(minY + ny) * width + minX + nx];
                if (value != through) continue;
                distance[next] = distance[current] + 1;
                queue.Enqueue(next);
            }
        }
        return distance;
    }

    /// <summary>One province reassigned by <see cref="HealBorderSlivers"/>.</summary>
    public readonly record struct SliverHealSummary(
        int ProvinceId,
        ushort OldNation,
        ushort OldState,
        ushort NewNation,
        ushort NewState,
        float Latitude,
        float Longitude);

    // Explicit geographic shields for real-world historic exclaves/enclaves (minLat, maxLat, minLon, maxLon).
    private static readonly (float MinLat, float MaxLat, float MinLon, float MaxLon)[] ProtectedExclaveBoxes =
    {
        (54.20f, 55.50f, 19.40f, 23.00f),   // Kaliningrad
        (38.70f, 39.95f, 44.60f, 46.30f),   // Nakhchivan
        (51.00f, 72.00f, -180.0f, -129.0f), // Alaska
        (36.05f, 36.22f, -5.45f, -5.28f),   // Gibraltar
        (35.10f, 36.00f, -5.55f, -2.75f),   // Ceuta & Melilla
        (-5.90f, -4.30f, 11.85f, 13.25f),   // Cabinda
        (42.40f, 42.56f, 1.90f, 2.08f),     // Llivia
        (-9.55f, -9.10f, 124.00f, 124.65f), // Oecusse
        (4.25f, 4.98f, 115.00f, 115.45f),   // Temburong
        (25.50f, 26.55f, 55.90f, 56.65f),   // Musandam
        (45.92f, 46.02f, 8.92f, 9.04f),     // Campione d'Italia
        (47.64f, 47.76f, 8.64f, 8.78f),     // Busingen
        (51.40f, 51.50f, 4.88f, 5.00f),     // Baarle-Hertog / Baarle-Nassau
        (39.70f, 40.50f, 70.50f, 72.25f),   // Fergana Valley enclaves (Sokh, Shohimardon, Vorukh, Barak)
        (48.94f, 49.04f, -123.15f, -122.96f), // Point Roberts
        (49.00f, 49.42f, -95.25f, -94.75f), // Northwest Angle
        (19.85f, 20.08f, -75.26f, -75.04f), // Guantanamo
        (34.50f, 35.12f, 32.78f, 34.02f),   // Akrotiri & Dhekelia
        (2.00f, 6.00f, -54.70f, -51.50f),   // French Guiana
    };

    /// <summary>True when a province centre lies inside a protected real-world historic exclave/enclave.</summary>
    public static bool IsProtectedRealWorldExclave(float latitude, float longitude)
    {
        for (var i = 0; i < ProtectedExclaveBoxes.Length; i++)
        {
            var b = ProtectedExclaveBoxes[i];
            if (latitude >= b.MinLat && latitude <= b.MaxLat && longitude >= b.MinLon && longitude <= b.MaxLon)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Deterministically heal 1–2 province cut-off land/coastal border slivers (such as the 2 detached
    /// coastal Western Samegrelo-Zemo Svaneti provinces on Abkhazia's Black Sea coast) whose 100% of
    /// non-water <see cref="ProvinceTopology.LandNeighbors"/> belong to a single surrounding foreign nation.
    ///
    /// Strictly preserves:
    /// - Islands (0 foreign land neighbors),
    /// - Sovereign microstates (nations with &lt;= 4 total land provinces),
    /// - Distinct whole-state exclaves (states with no other provinces outside the component),
    /// - Explicitly shielded real-world historic exclaves/enclaves (<see cref="IsProtectedRealWorldExclave"/>).
    /// Operates purely on the province graph so <c>WorldMapSource.States</c> and <c>compiled.json</c> stay untouched.
    /// </summary>
    public static IReadOnlyList<SliverHealSummary> HealBorderSlivers(ProvinceTopology topology)
    {
        if (topology is null || topology.ProvinceCount <= 0)
            return Array.Empty<SliverHealSummary>();

        var count = topology.ProvinceCount;
        var nations = topology.ProvinceToNation;
        var states = topology.ProvinceToState;
        var landNeighbors = topology.LandNeighbors;
        var nationLandCounts = new Dictionary<ushort, int>();
        var stateLandCounts = new Dictionary<ushort, int>();

        for (var p = 1; p <= count; p++)
        {
            if (topology.IsWater(p)) continue;
            var owner = nations[p];
            var state = states[p];
            if (owner == 0 || state == 0) continue;
            nationLandCounts[owner] = nationLandCounts.GetValueOrDefault(owner) + 1;
            stateLandCounts[state] = stateLandCounts.GetValueOrDefault(state) + 1;
        }

        // Find connected components of provinces belonging to the same owner via pure LandNeighbors (no strait crossings).
        var visited = new bool[count + 1];
        var queue = new Queue<int>();
        var component = new List<int>(4);
        var foreignStateCounts = new Dictionary<ushort, int>();
        var heals = new List<SliverHealSummary>();

        for (var start = 1; start <= count; start++)
        {
            if (visited[start] || topology.IsWater(start)) continue;
            var owner = nations[start];
            var state = states[start];
            if (owner == 0 || state == 0) continue;

            visited[start] = true;
            component.Clear();
            queue.Clear();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                component.Add(cur);
                var adj = landNeighbors[cur];
                for (var i = 0; i < adj.Length; i++)
                {
                    var next = adj[i];
                    if (next <= 0 || next > count || visited[next] || topology.IsWater(next)) continue;
                    if (nations[next] != owner) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            // Only heal tiny 1–2 province detached slivers.
            if (component.Count is < 1 or > 2) continue;

            // Strictly preserve sovereign microstates (<= 4 total land provinces).
            if (nationLandCounts.GetValueOrDefault(owner) <= 4) continue;

            // Require every province in the sliver to belong to a state that has a larger body elsewhere
            // (a split fragment of a region, never a whole distinct real-world exclave state).
            var allSplitFragments = true;
            var anyProtectedExclave = false;
            for (var i = 0; i < component.Count; i++)
            {
                var p = component[i];
                if (stateLandCounts.GetValueOrDefault(states[p]) <= component.Count)
                {
                    allSplitFragments = false;
                    break;
                }
                var (lat, lon) = topology.CenterOf(p);
                if (IsProtectedRealWorldExclave(lat, lon))
                {
                    anyProtectedExclave = true;
                    break;
                }
            }
            if (!allSplitFragments || anyProtectedExclave) continue;

            // Inspect all non-water foreign LandNeighbors of the component.
            ushort surroundingNation = 0;
            var foreignContacts = 0;
            var multipleForeignNations = false;
            foreignStateCounts.Clear();

            for (var i = 0; i < component.Count && !multipleForeignNations; i++)
            {
                var adj = landNeighbors[component[i]];
                for (var j = 0; j < adj.Length; j++)
                {
                    var n = adj[j];
                    if (n <= 0 || n > count || topology.IsWater(n)) continue;
                    var nOwner = nations[n];
                    var nState = states[n];
                    if (nOwner == 0 || nState == 0 || nOwner == owner) continue;
                    if (surroundingNation == 0) surroundingNation = nOwner;
                    else if (surroundingNation != nOwner) { multipleForeignNations = true; break; }
                    foreignContacts++;
                    foreignStateCounts[nState] = foreignStateCounts.GetValueOrDefault(nState) + 1;
                }
            }

            // Strictly preserve islands (0 foreign land neighbors) and multi-neighbor border pieces.
            if (foreignContacts == 0 || multipleForeignNations || surroundingNation == 0) continue;

            // Pick the majority surrounding state of that single foreign nation (deterministic tie-break by lowest ID).
            ushort targetState = 0;
            var bestContacts = -1;
            foreach (var kv in foreignStateCounts)
            {
                if (kv.Value > bestContacts || (kv.Value == bestContacts && kv.Key < targetState))
                {
                    bestContacts = kv.Value;
                    targetState = kv.Key;
                }
            }
            if (targetState == 0) continue;

            component.Sort();
            for (var i = 0; i < component.Count; i++)
            {
                var p = component[i];
                var (lat, lon) = topology.CenterOf(p);
                heals.Add(new SliverHealSummary(p, nations[p], states[p], surroundingNation, targetState, lat, lon));
            }
        }

        // Apply deterministically after full scan so order never cascades into multi-hop creep.
        for (var i = 0; i < heals.Count; i++)
        {
            var h = heals[i];
            nations[h.ProvinceId] = h.NewNation;
            states[h.ProvinceId] = h.NewState;
        }
        return heals;
    }
}
