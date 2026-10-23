using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Exact route oracle, including tie breaks, incremental requests and dirty pooled memory.</summary>
static class FriendlyRouteSearchTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL incremental friendly route: " + label);
        Console.WriteLine("PASS incremental friendly route: " + label);
    }

    public static void Run()
    {
        foreach (var reversed in new[] { false, true })
        foreach (var water in new[] { false, true })
        foreach (var access in new[] { new ushort[] { 500 }, new ushort[] { 500, 501 },
            new ushort[] { 500, 502 }, new ushort[] { 500, 501, 502 } })
            AllPairs(reversed, water, access);
        WholeExpansionAndKnownTargets();
        InvalidAndDisposedRequests();
        DirtyPoolReuse();
        AccessSnapshot();
    }

    internal static ProvinceTopology Map(bool reversed = false, bool water = false, bool isolated = false)
    {
        const int width = 64, height = 32;
        var states = new ushort[width * height]; var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var p = y / 16 * 4 + x / 16 + 1; var i = y * width + x;
            states[i] = (ushort)(2000 + p); owners[i] = 500; rgb[i * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        int[][] graph = { Array.Empty<int>(), new[] { 2, 3 }, new[] { 1, 4, 5 },
            new[] { 1, 4, 6 }, new[] { 2, 3, 7 }, new[] { 2, 7 }, new[] { 3, 7 },
            isolated ? new[] { 4, 5, 6 } : new[] { 4, 5, 6, 8 }, isolated ? Array.Empty<int>() : new[] { 7 } };
        for (var p = 1; p <= 8; p++)
            map.Neighbors[p] = reversed ? graph[p].Reverse().ToArray() : graph[p];
        map.WaterByProvince[4] = water;
        return map;
    }

    static void AllPairs(bool reversed, bool water, ushort[] access)
    {
        var map = Map(reversed, water);
        var controllers = (ushort[])map.ProvinceToNation.Clone();
        controllers[5] = 501; controllers[6] = 502; controllers[8] = 503;
        var allowed = access.ToHashSet(); var requests = new[] { Enumerable.Range(1, 8).ToArray(),
            Enumerable.Range(1, 8).Reverse().ToArray(), new[] { 4, 2, 7, 1, 8, 6, 5, 3 } };
        var checkedRoutes = 0;
        for (var from = 1; from <= 8; from++) foreach (var order in requests)
        {
            using var tree = new FriendlyRouteSearch(map, controllers, from, allowed);
            foreach (var to in order)
            {
                var expected = access.Length == 1
                    ? map.FindRoute(from, to, access[0], controllers)
                    : map.FindOperationalRoute(from, to, allowed, controllers);
                var actual = tree.Route(to, out _);
                if (!actual.SequenceEqual(expected))
                    throw new Exception("FAIL incremental route parity " + from + "→" + to +
                        " expected=" + string.Join(",", expected) + " actual=" + string.Join(",", actual) +
                        " reversed=" + reversed + " water=" + water + " access=" + string.Join(",", access));
                checkedRoutes++;
            }
        }
        Check(checkedRoutes == 192, "all pairs and three target orders: reverse=" + reversed +
            ", water=" + water + ", access=" + string.Join(",", access));
    }

    static int BfsCalls() => SimProfiler.All.Where(s => s.Name.Trim() == "disengage:bfs").Sum(s => s.Runs);

    static void WholeExpansionAndKnownTargets()
    {
        var map = Map(isolated: true); map.Neighbors[1] = new[] { 3, 2 };
        var controllers = (ushort[])map.ProvinceToNation.Clone(); var allowed = new HashSet<ushort> { 500 };
        using var tree = new FriendlyRouteSearch(map, controllers, 1, allowed);
        var count = BfsCalls();
        Check(tree.Route(3, out var searched).SequenceEqual(new[] { 3 }) && searched && BfsCalls() == count + 1,
            "first target expands the forward frontier and reports one real BFS interval");
        count = BfsCalls();
        Check(tree.Route(2, out searched).SequenceEqual(new[] { 2 }) && !searched && BfsCalls() == count,
            "all neighbours were visited after the first target was discovered mid-expansion");
        Check(tree.Route(7, out searched).SequenceEqual(map.FindRoute(1, 7, 500, controllers)) && searched,
            "a later target resumes the same forward queue with unchanged legacy tie breaks");
        count = BfsCalls();
        var route = tree.Route(6, out searched);
        Check(route.SequenceEqual(map.FindRoute(1, 6, 500, controllers)) && !searched && BfsCalls() == count,
            "an already discovered target uses its predecessor chain without another expansion");
        route[0] = 8;
        Check(tree.Route(6, out searched).SequenceEqual(map.FindRoute(1, 6, 500, controllers)) && !searched,
            "returned route arrays do not expose or mutate the retained tree");
        Check(tree.Route(8, out searched).Length == 0 && searched,
            "an inaccessible component exhausts the remaining frontier once");
        count = BfsCalls();
        Check(tree.Route(8, out searched).Length == 0 && !searched && BfsCalls() == count,
            "a known unreachable target performs no repeated full flood");
    }

    static void InvalidAndDisposedRequests()
    {
        var map = Map(); var controllers = (ushort[])map.ProvinceToNation.Clone();
        var allowed = new HashSet<ushort> { 500 };
        foreach (var origin in new[] { -1, 0, 9, int.MaxValue })
        {
            using var invalid = new FriendlyRouteSearch(map, controllers, origin, allowed);
            Check(invalid.Route(2, out var searched).Length == 0 && !searched, "invalid origin " + origin + " is inert");
        }
        using (var missingMap = new FriendlyRouteSearch(null, controllers, 1, allowed))
            Check(missingMap.Route(2, out var searched).Length == 0 && !searched, "missing map is inert");
        using (var missingControllers = new FriendlyRouteSearch(map, null, 1, allowed))
            Check(missingControllers.Route(2, out var searched).Length == 0 && !searched, "missing controller layer is inert");
        using (var shortControllers = new FriendlyRouteSearch(map, new ushort[2], 1, allowed))
            Check(shortControllers.Route(2, out var searched).Length == 0 && !searched, "short controller layer is inert");
        using (var missingAccess = new FriendlyRouteSearch(map, controllers, 1, null))
            Check(missingAccess.Route(2, out var searched).Length == 0 && !searched, "missing access set is inert");
        using (var emptyAccess = new FriendlyRouteSearch(map, controllers, 1, new HashSet<ushort>()))
            Check(emptyAccess.Route(2, out var searched).Length == 0 && !searched, "empty access set is inert");
        controllers[5] = 501; map.WaterByProvince[4] = true;
        using (var forbiddenStart = new FriendlyRouteSearch(map, controllers, 5, allowed))
            Check(forbiddenStart.Route(2, out var searched).Length == 0 && !searched, "foreign start is rejected before flooding");
        using (var waterStart = new FriendlyRouteSearch(map, controllers, 4, allowed))
            Check(waterStart.Route(2, out var searched).Length == 0 && !searched, "water start is rejected before flooding");
        var tree = new FriendlyRouteSearch(map, controllers, 1, allowed);
        foreach (var target in new[] { -1, 0, 9, int.MaxValue, 5, 4, 1 })
            Check(tree.Route(target, out var searched).Length == 0 && !searched,
                "invalid, forbidden, water or zero-distance target does not expand: " + target);
        tree.Dispose(); tree.Dispose();
        Check(tree.Route(2, out var afterDispose).Length == 0 && !afterDispose,
            "repeated disposal is safe and disposed searches cannot read returned pool memory");
    }

    static void DirtyPoolReuse()
    {
        var map = Map(); var controllers = (ushort[])map.ProvinceToNation.Clone();
        var allowed = new HashSet<ushort> { 500 }; var equal = true;
        for (var cycle = 0; cycle < 32; cycle++)
        {
            // Encourage both rentals to contain non-zero stale data; the test does not
            // depend on pool identity or assume that Rent gives a fresh array.
            var dirtyOne = ArrayPool<int>.Shared.Rent(map.Neighbors.Length);
            var dirtyTwo = ArrayPool<int>.Shared.Rent(map.Neighbors.Length);
            Array.Fill(dirtyOne, int.MaxValue); Array.Fill(dirtyTwo, int.MaxValue);
            ArrayPool<int>.Shared.Return(dirtyOne); ArrayPool<int>.Shared.Return(dirtyTwo);
            var from = cycle % 8 + 1;
            using var tree = new FriendlyRouteSearch(map, controllers, from, allowed);
            foreach (var to in Enumerable.Range(1, 8).Reverse())
                equal &= tree.Route(to, out _).SequenceEqual(map.FindRoute(from, to, 500, controllers));
        }
        Check(equal, "32 dirty pool/dispose cycles retain exact paths from changing origins");
    }

    static void AccessSnapshot()
    {
        var map = Map(); var controllers = (ushort[])map.ProvinceToNation.Clone(); controllers[6] = 502;
        var allowed = new HashSet<ushort> { 500, 502 };
        using var tree = new FriendlyRouteSearch(map, controllers, 1, allowed);
        allowed.Clear(); allowed.Add(501);
        Check(tree.Route(6, out var searched).SequenceEqual(map.FindOperationalRoute(1, 6,
            new HashSet<ushort> { 500, 502 }, controllers)) && searched,
            "a caller's later access-set mutation cannot rewrite an existing forward tree");
    }
}
