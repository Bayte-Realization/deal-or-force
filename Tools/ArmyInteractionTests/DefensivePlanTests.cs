using System;
using System.Linq;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class DefensivePlanTests
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    public static void Run(Campaign c)
    {
        var own = c.NationByTag("KOR"); var map = c.ProvinceMap;
        c.ConfigureSinglePlayer(own.Id); c.Wars.Clear(); c.Battles.Clear(); c.Armies.Clear(); c.FrontLines.Clear();
        Array.Fill(c.ProvinceController, (ushort)own.MapIndex);
        var start = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && map.Neighbors[p].Length >= 4)
            .OrderBy(p => Math.Abs(map.CenterOf(p).Latitude - 50) + Math.Abs(map.CenterOf(p).Longitude - 35)).First();
        var seen = new HashSet<int> { start }; var queue = new Queue<int>(); queue.Enqueue(start);
        int[] path = null;
        while (queue.Count > 0 && path is null)
        {
            var p = queue.Dequeue();
            var route = map.FindRoute(start, p, (ushort)own.MapIndex, c.ProvinceController);
            if (route.Length == 4) { path = new[] { start }.Concat(route).ToArray(); break; }
            foreach (var n in map.Neighbors[p]) if (!map.IsWater(n) && seen.Add(n)) queue.Enqueue(n);
        }
        var stroke = new List<int>();
        Check(DefensivePlanning.Extend(c, own.Id, stroke, path[0]) && DefensivePlanning.Extend(c, own.Id, stroke, path[^1]) &&
              stroke.Count == 5 && DefensivePlanning.Valid(c, own.Id, stroke), "fast stroke fills skipped adjacent land posts");
        DefensivePlanning.Extend(c, own.Id, stroke, stroke[1]);
        Check(stroke.Count == 2, "backtracking trims rather than creates loops");
        Check(!DefensivePlanning.Valid(c, own.Id, new[] { path[0], path[4] }) &&
              !DefensivePlanning.Valid(c, own.Id, new[] { path[0], path[1], path[0] }), "disconnected and repeated command posts rejected");
        var armies = Enumerable.Range(0, 7).Select(i => new Army { Id = 9700 + i, NationId = own.Id, ProvinceId = start,
            StateMapIndex = map.ProvinceToState[start], Divisions = 1, Strength = 1, Organisation = 1, SupplyLevel = 1 }).ToArray();
        foreach (var a in armies) { (a.Latitude, a.Longitude) = map.CenterOf(start); c.Armies.Add(a); }
        var ids = armies.Select(a => a.Id).ToArray();
        var draw = new DrawFrontLineCommand { IssuerId = own.Id, Kind = FrontLineKind.Fallback, Provinces = path, ArmyIds = ids };
        draw.Apply(c);
        var line = c.FrontLines.Single();
        var counts = path.Select(p => armies.Count(a => a.OrderProvinceId == p)).ToArray();
        Check(counts.Min() == 1 && counts.Max() == 2, "seven divisions evenly allocated to five defensive posts");
        Check(armies.All(a => a.OrderKind == ArmyOrder.DefendFront && (a.ProvinceId == a.OrderProvinceId || a.Route.Count > 0)),
            "defensive line creates actual march orders at peace");
        for (var day = 0; day < 180 && armies.Any(a => a.ProvinceId != a.OrderProvinceId); day++)
        {
            foreach (var a in armies) { a.ReadyDay = 0; a.SupplyLevel = 1; }
            FrontManager.Tick(c); MilitaryOperations.Tick(c);
        }
        Check(armies.All(a => a.ProvinceId == a.OrderProvinceId), "all divisions reach their defensive positions");
        var positions = armies.Select(a => a.ProvinceId).ToArray();
        for (var day = 0; day < 10; day++) { FrontManager.Tick(c); MilitaryOperations.Tick(c); }
        Check(armies.Select(a => a.ProvinceId).SequenceEqual(positions) && armies.All(a => a.Entrenchment > 0), "hold and entrench without shuffling");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "defence-test"), "defence-test").Campaign;
        Check(loaded.FrontLines.Single().DefensiveSequence.SequenceEqual(path), "ordered defence survives save/network snapshot");
        Check(NetworkCommandCodec.TryEncode(draw, out var type, out var json) && NetworkCommandCodec.TryDecode(type, json, out _), "defence uses multiplayer command codec");
        new DrawFrontLineCommand { IssuerId = own.Id, Kind = FrontLineKind.Fallback, Provinces = new[] { path[0], path[4] }, ArmyIds = ids }.Apply(c);
        Check(c.FrontLines.Single() == line, "invalid redraw preserves original orders");
        c.ProvinceController[path[2]] = (ushort)c.NationByTag("USA").MapIndex; c.MarkMapChanged();
        FrontManager.Tick(c);
        Check(armies.All(a => a.OrderProvinceId != path[2]) && DefensivePlanning.Sequence(c, line).Count == 4, "lost province removed from defence without attacking to retake it");
        c.FrontLines.Clear(); foreach (var a in armies) a.FrontLineId = -1;
        c.ProvinceController[path[2]] = (ushort)own.MapIndex;
        new DrawFrontLineCommand { IssuerId = own.Id, Kind = FrontLineKind.Fallback, Provinces = path, ArmyIds = ids.Take(2).ToArray() }.Apply(c);
        Check(armies.Take(2).Select(a => a.OrderProvinceId).OrderBy(p => p).SequenceEqual(new[] { path[1], path[3] }.OrderBy(p => p)),
            "two divisions cover spaced sectors of a five-post line");
    }
}
