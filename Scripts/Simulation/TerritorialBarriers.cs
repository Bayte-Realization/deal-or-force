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
}
